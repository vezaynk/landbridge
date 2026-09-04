using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Hub;

/// <summary>
/// LISTENs on session NOTIFY, inserts wake rows into <see cref="HubQueueRow"/>
/// (id + topic only — clients refetch over HTTP), then wakes SSE waiters.
/// Own connection for LISTEN; factory for the writes.
/// </summary>
public sealed class HubProjector(
    string connectionString,
    IDbContextFactory<LandbridgeDbContext> dbFactory,
    HubWaiters waiters,
    TimeProvider clock,
    ILogger<HubProjector> logger) : IHostedService, IAsyncDisposable
{
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
        var at = clock.GetUtcNow();
        db.HubQueue.Add(new HubQueueRow
        {
            Topic = HubQueueRow.SessionTopic,
            EntityId = sessionId,
            CreatedAt = at,
        });
        db.HubQueue.Add(new HubQueueRow
        {
            Topic = HubQueueRow.SessionsTopic,
            EntityId = sessionId,
            CreatedAt = at,
        });
        await db.SaveChangesAsync(ct);
        waiters.Wake(HubQueueRow.SessionTopic, sessionId);
        waiters.Wake(HubQueueRow.SessionsTopic, entityId: null);
    }
}
