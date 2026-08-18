namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// 5s refresh the string dashboard used a meta-refresh for. On a live Blazor
/// Server circuit this ticks; prerender (tests, no-JS) paints once and stops.
/// </summary>
internal sealed class DashboardRefresh : IDisposable
{
    private Timer? _timer;

    public void Start(Func<Task> tick)
    {
        _timer ??= new Timer(async _ =>
        {
            try { await tick(); }
            catch (ObjectDisposedException) { }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _timer?.Dispose();
}
