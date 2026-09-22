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

    public async Task<CommandRow> EnqueueAsync(
        string actorKind,
        Guid actorId,
        Guid teamId,
        Guid sessionId,
        string kind,
        object payload,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        var key = $"{kind}:{sessionId:D}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16]}";
        var existing = await db.Commands.AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.ActorKind == actorKind && c.ActorId == actorId && c.IdempotencyKey == key, ct);
        if (existing is not null)
            return existing;

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
        catch (DbUpdateException)
        {
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
        CancellationToken ct)
    {
        var row = await EnqueueAsync(actorKind, actorId, teamId, sessionId, kind, payload, ct);
        if (row.Status is CommandRow.Applied or CommandRow.Rejected)
            return row;
        return await WaitAsync(row.Id, ct);
    }

    public async Task<CommandRow> WaitAsync(Guid commandId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var row = await db.Commands.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == commandId, ct)
                ?? throw new InvalidOperationException($"command {commandId} disappeared");
            if (row.Status is CommandRow.Applied or CommandRow.Rejected)
                return row;
            await Task.Delay(50, ct);
        }
        throw new OperationCanceledException(ct);
    }

    public Task<CommandRow?> GetAsync(Guid id, CancellationToken ct) =>
        db.Commands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
}
