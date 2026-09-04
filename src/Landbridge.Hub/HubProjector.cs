using Landbridge.ControlPlane;

namespace Landbridge.Hub;

/// <summary>
/// LISTENs on session NOTIFY and wakes SSE waiters. Does not write — the
/// outbox row is already in <c>hub_queue</c> from <see cref="SessionStore"/>.
/// </summary>
public sealed class HubProjector(
    string connectionString,
    HubWaiters waiters,
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
                waiters.Wake(HubQueueRow.SessionTopic, sessionId);
                waiters.Wake(HubQueueRow.SessionsTopic, entityId: null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "hub LISTEN pump failed");
        }
    }
}
