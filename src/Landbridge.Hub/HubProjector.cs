using System.Text.Json;
using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Hub;

/// <summary>
/// LISTENs on session NOTIFY, snapshots the row, inserts <see cref="HubQueueRow"/>s,
/// then wakes SSE waiters. Own connection for LISTEN; factory for the writes.
/// </summary>
public sealed class HubProjector(
    string connectionString,
    IDbContextFactory<LandbridgeDbContext> dbFactory,
    HubWaiters waiters,
    TimeProvider clock,
    ILogger<HubProjector> logger) : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SessionEventListener _listener = new(connectionString);
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public Task WhenListening => _listener.Listening;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        if (_pump is not null)
        {
            try { await _pump; }
            catch (OperationCanceledException) { }
        }
        await _listener.DisposeAsync();
        _cts.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
            await StopAsync(CancellationToken.None);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sessionId in _listener.ListenAsync(ct))
            {
                try
                {
                    await EnqueueAsync(sessionId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "hub enqueue failed for session {SessionId}", sessionId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task EnqueueAsync(Guid sessionId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        var ids = await db.Sessions.AsNoTracking()
            .Select(s => s.Id)
            .ToListAsync(ct);
        var at = clock.GetUtcNow();

        if (session is not null)
        {
            db.HubQueue.Add(new HubQueueRow
            {
                Topic = HubQueueRow.SessionTopic,
                EntityId = session.Id,
                Payload = JsonSerializer.Serialize(SessionSnapshot.From(session), Json),
                CreatedAt = at,
            });
        }

        db.HubQueue.Add(new HubQueueRow
        {
            Topic = HubQueueRow.SessionsTopic,
            EntityId = null,
            Payload = JsonSerializer.Serialize(new { ids }, Json),
            CreatedAt = at,
        });

        await db.SaveChangesAsync(ct);
        if (session is not null)
            waiters.Wake(HubQueueRow.SessionTopic, session.Id);
        waiters.Wake(HubQueueRow.SessionsTopic, entityId: null);
    }
}

internal sealed record SessionSnapshot(
    Guid Id,
    Guid TeamId,
    string Slug,
    string OccupancyDesired,
    string OccupancyObserved,
    string Health,
    bool Hidden,
    string MessageState,
    string? Profile)
{
    public static SessionSnapshot From(SessionRow row) => new(
        row.Id,
        row.TeamId,
        row.Slug,
        row.OccupancyDesired.ToString(),
        row.OccupancyObserved.ToString(),
        row.Health.ToString(),
        row.Hidden,
        row.MessageState.ToString(),
        row.Profile);
}
