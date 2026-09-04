using Landbridge.Mcp.Dashboard;

namespace Landbridge.Mcp.Tests;

public sealed class DashboardWindowTests
{
    [Fact]
    public void Parse_accepts_the_four_windows_and_rejects_unknown()
    {
        Assert.True(DashboardWindow.TryParse("5m", out var five));
        Assert.Equal(TimeSpan.FromMinutes(5), five.Duration);
        Assert.True(DashboardWindow.TryParse("30m", out var thirty));
        Assert.Equal(TimeSpan.FromMinutes(30), thirty.Duration);
        Assert.True(DashboardWindow.TryParse("2h", out var twoHours));
        Assert.Equal(TimeSpan.FromHours(2), twoHours.Duration);
        Assert.True(DashboardWindow.TryParse("1d", out var day));
        Assert.Equal(TimeSpan.FromDays(1), day.Duration);
        Assert.False(DashboardWindow.TryParse("15m", out _));
        Assert.Equal("30m", DashboardWindow.Default.Key);
    }

    [Fact]
    public void Href_sets_window_and_keeps_the_session_filter()
    {
        var href = DashboardWindow.Href(
            "https://localhost/dashboard/events?session=quiet-river-0001", "2h");
        Assert.StartsWith("/dashboard/events?", href, StringComparison.Ordinal);
        Assert.Contains("session=quiet-river-0001", href, StringComparison.Ordinal);
        Assert.Contains("window=2h", href, StringComparison.Ordinal);
        Assert.Equal("2h", DashboardWindow.QueryValue("https://localhost" + href));
        Assert.Null(DashboardWindow.QueryValue("https://localhost/dashboard/machines"));
    }

    [Fact]
    public void Team_href_sets_team_and_keeps_the_window()
    {
        var href = DashboardTeam.Href(
            "https://localhost/dashboard/machines?window=2h", "quiet-river-0001");
        Assert.StartsWith("/dashboard/machines?", href, StringComparison.Ordinal);
        Assert.Contains("window=2h", href, StringComparison.Ordinal);
        Assert.Contains("team=quiet-river-0001", href, StringComparison.Ordinal);
    }
}
