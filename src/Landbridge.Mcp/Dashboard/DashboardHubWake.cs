namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Circuit-side Hub membership SSE. A <c>change</c> on any collection stream
/// arms one coalesced refetch. Hub unset keeps <see cref="DashboardRefresh"/>.
/// </summary>
internal sealed class DashboardHubWake : IDisposable
{
    internal static readonly string[] Membership =
    [
        "/sessions/events",
        "/machines/events",
        "/services/events",
        "/forwards/events",
        "/previews/events",
        "/processes/events",
    ];

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _delay;

    public void Start(HubClient hub, string bearer, Func<Task> refetch, CancellationToken ct)
    {
        foreach (var path in Membership)
            _ = ListenAsync(hub, path, bearer, refetch, ct);
    }

    private async Task ListenAsync(
        HubClient hub, string path, string bearer, Func<Task> refetch, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in hub.WatchAsync(path, bearer, ct))
                Arm(refetch, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Arm(Func<Task> refetch, CancellationToken ct)
    {
        var delay = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _delay, delay);
        prev?.Cancel();
        prev?.Dispose();
        _ = FireAsync(delay, refetch, ct);
    }

    private async Task FireAsync(CancellationTokenSource delay, Func<Task> refetch, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(delay.Token, ct);
        try
        {
            await Task.Delay(Quiet, linked.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await refetch();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        _delay?.Cancel();
        _delay?.Dispose();
    }
}
