namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Circuit-side refresh. Prerender (tests, no-JS) paints once and never
/// starts this; a live Blazor Server circuit ticks every 2s.
/// </summary>
internal sealed class DashboardRefresh : IDisposable
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private Timer? _timer;

    public void Start(Func<Task> tick)
    {
        _timer ??= new Timer(async _ =>
        {
            try { await tick(); }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        }, null, Interval, Interval);
    }

    public void Dispose() => _timer?.Dispose();
}
