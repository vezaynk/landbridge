using Landbridge.Mcp.Dashboard;

namespace Landbridge.Mcp.Tests;

public sealed class DashboardFormatTests
{
    [Fact]
    public void Address_prefers_slug_and_falls_back_to_guid()
    {
        var id = Guid.Parse("e09cfd93-eb81-4b32-97f2-af808581d94f");
        Assert.Equal("quiet-river-0001", DashboardFormat.Address(id, "quiet-river-0001"));
        Assert.Equal(id.ToString(), DashboardFormat.Address(id, null));
        Assert.Equal(id.ToString(), DashboardFormat.Address(id, ""));
        Assert.Equal("/dashboard/teams/quiet-river-0001", DashboardFormat.TeamHref(id, "quiet-river-0001"));
        Assert.Equal("/dashboard/events?session=quiet-river-0001", DashboardFormat.SessionEventsHref(id, "quiet-river-0001"));
        Assert.Equal("/dashboard/sessions/quiet-river-0001/transcripts", DashboardFormat.TranscriptsHref(id, "quiet-river-0001"));
    }
}
