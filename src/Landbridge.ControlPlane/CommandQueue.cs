using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Landbridge.ControlPlane;

/// <summary>
/// Accept path for the write queue. Façades insert <see cref="CommandRow.Queued"/>
/// and wait (default) so today's MCP return shape is unchanged. Tests leave
/// <c>Landbridge:WriteQueue</c> unset and keep calling <see cref="SessionStore"/>.
///
/// <para>Deduplication is opt-in: a caller that names its attempt gets retry safety,
/// and one that does not gets a command per call. Nothing infers sameness from the
/// payload, because two identical requests are not evidence of one.</para>
/// </summary>
public sealed class CommandQueue(LandbridgeDbContext db, TimeProvider clock, IConfiguration? config = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Enabled =>
        IsTrue(config?["Landbridge:WriteQueue"])
        || IsTrue(Environment.GetEnvironmentVariable("LANDBRIDGE_WRITE_QUEUE"));

    private static bool IsTrue(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || value == "1";

    /// <param name="idempotencyKey">
    /// The caller's name for this attempt, or null for none. A retry presenting the same
    /// key attaches to the command already accepted; without one, every call is its own
    /// command. Deliberately not derived from the payload — see
    /// <see cref="CommandRow.IdempotencyKey"/>.
    /// </param>
    public async Task<CommandRow> EnqueueAsync(
        string actorKind,
        Guid actorId,
        Guid teamId,
        Guid sessionId,
        string kind,
        object payload,
        CancellationToken ct,
        string? idempotencyKey = null)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        if (key is not null)
        {
            var existing = await db.Commands.AsNoTracking()
                .FirstOrDefaultAsync(
                    c => c.ActorKind == actorKind && c.ActorId == actorId && c.IdempotencyKey == key, ct);
            if (existing is not null)
                return existing;
        }

        var row = new CommandRow
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = key,
            ActorKind = actorKind,
            ActorId = actorId,
            TeamId = teamId,
            SessionId = sessionId,
            Kind = kind,
            Payload = json,
            Status = CommandRow.Queued,
            AcceptedAt = clock.GetUtcNow(),
        };
        db.Commands.Add(row);
        HubOutbox.Stage(db, clock, HubQueueRow.CommandsTopic, row.Id);
        HubOutbox.Stage(db, clock, HubQueueRow.SessionTopic, sessionId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (key is not null)
        {
            // Two requests carrying the same key raced; the one that lost reads back the
            // command the winner accepted. Unkeyed inserts cannot collide, so their
            // failures are real and propagate.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return await db.Commands.AsNoTracking()
                .FirstAsync(
                    c => c.ActorKind == actorKind && c.ActorId == actorId && c.IdempotencyKey == key, ct);
        }
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_notify({LandbridgeDbContext.CommandChannel}, {row.Id.ToString()})", ct);
        await HubOutbox.NotifyAsync(db, sessionId, ct);
        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<CommandRow> EnqueueAndWaitAsync(
        string actorKind,
        Guid actorId,
        Guid teamId,
        Guid sessionId,
        string kind,
        object payload,
        CancellationToken ct,
        string? idempotencyKey = null)
    {
        var row = await EnqueueAsync(
            actorKind, actorId, teamId, sessionId, kind, payload, ct, idempotencyKey);
        if (row.Status is CommandRow.Applied or CommandRow.Rejected)
            return row;
        return await WaitAsync(row.Id, ct);
    }

    /// <summary>
    /// How long a façade will wait for Core to resolve a command before answering with
    /// what it knows. Default 10s, overridable with <c>Landbridge:WriteQueueWaitMs</c>.
    /// </summary>
    public TimeSpan WaitBudget =>
        int.TryParse(config?["Landbridge:WriteQueueWaitMs"], out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for Core to apply or reject, and gives up on its own clock rather than the
    /// request's.
    ///
    /// <para>An unbounded wait makes every mutation hang for the caller's full timeout
    /// whenever Core is down — which is backwards, because the queue exists so that a
    /// façade can accept work Core is not currently able to apply. The row is durable the
    /// moment it is enqueued, so on expiry this returns it as it stands: the caller
    /// reports <c>accepted</c> with a command id, which is both true and pollable
    /// (<c>GET /commands</c>). Only the caller's own cancellation is an error.</para>
    /// </summary>
    public async Task<CommandRow> WaitAsync(Guid commandId, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(WaitBudget);
        while (true)
        {
            var row = await db.Commands.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == commandId, ct)
                ?? throw new InvalidOperationException($"command {commandId} disappeared");
            if (row.Status is CommandRow.Applied or CommandRow.Rejected)
                return row;
            if (budget.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                return row;
            }

            try
            {
                await Task.Delay(50, budget.Token);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    public Task<CommandRow?> GetAsync(Guid id, CancellationToken ct) =>
        db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
}
