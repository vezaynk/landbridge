using Landbridge.ControlPlane;

namespace Landbridge.Hub;

/// <summary>
/// LISTENs on session NOTIFY (always) plus the hub channel, then wakes SSE
/// waiters. Does not write — the outbox row is already in <c>hub_queue</c>
/// from <see cref="SessionStore"/>. A dropped connection retries; each
/// successful re-LISTEN wakes every stream so catch-up SELECTs past the last id.
/// </summary>
public sealed class HubProjector(
    string connectionString,
    HubWaiters waiters,
    ILogger<HubProjector> logger) : IHostedService, IAsyncDisposable
{
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public Task WhenListening => _listening.Task;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token));
        await _listening.Task.WaitAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        if (_pump is not null)
        {
            try { await _pump.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
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
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var listener = new SessionEventListener(connectionString, LandbridgeDbContext.HubChannel);
            var consume = ConsumeAsync(listener, ct);
            try
            {
                await listener.Listening.WaitAsync(ct);
                _listening.TrySetResult();
                waiters.WakeAll();
                await consume;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await Observe(consume);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "hub LISTEN pump failed; retrying");
                await Observe(consume);
            }
            finally
            {
                await listener.DisposeAsync();
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConsumeAsync(SessionEventListener listener, CancellationToken ct)
    {
        await foreach (var _ in listener.ListenAsync(ct))
            waiters.WakeAll();
    }

    private static async Task Observe(Task task)
    {
        try { await task; }
        catch (Exception) { }
    }
}
