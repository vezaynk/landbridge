using Landbridge.ControlPlane;
using Landbridge.Mcp.Dashboard;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The Hub board is the dashboard the operator sees. A machine whose last
/// heartbeat said "ready" stays that way in the row after the runner is gone;
/// the rail and the machines JSON keep only a heartbeat inside the liveness window.
/// </summary>
public sealed class DashboardHubBoardTests
{
    [Fact]
    public async Task A_silent_machine_drops_off_the_rail_even_when_its_ready_bit_is_still_set()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var live = Guid.NewGuid();
        var saturated = Guid.NewGuid();
        var stale = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-10-21T20:00:00Z");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/machines", () => new object[]
        {
            Machine(live, "billowing-wood-2324", ready: true, live: true, now),
            Machine(saturated, "cool-meadow-2501", ready: false, live: true, now, underBackPressure: true),
            Machine(stale, "frosty-cloud-6823", ready: true, live: false, now.AddDays(-21)),
        });
        app.MapGet("/sessions", () => Array.Empty<object>());
        app.MapGet("/services", () => Array.Empty<object>());
        app.MapGet("/previews", () => Array.Empty<object>());
        app.MapGet("/forwards", () => Array.Empty<object>());
        app.MapGet("/teams", () => Array.Empty<object>());
        await app.StartAsync(cts.Token);

        var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))),
        };
        var hub = new HubClient(http, NullLogger<HubClient>.Instance);

        var snap = await DashboardHubBoard.TrySnapshotAsync(
            hub, "t", now, TimeSpan.FromHours(1), teamScope: null, teamId: null, cts.Token);
        Assert.NotNull(snap);
        Assert.Equal(
            [live.ToString("D"), saturated.ToString("D")],
            snap.Machines.Select(m => m.MachineId).ToArray());
        Assert.Equal(2, snap.Summary.MachineCount);
        Assert.Contains(snap.Machines, m => m.MachineId == live.ToString("D") && m.Ready);
        Assert.Contains(snap.Machines, m => m.MachineId == saturated.ToString("D") && m.UnderBackPressure);

        var json = await DashboardHubBoard.TryMachinesAsync(hub, "t", cts.Token);
        Assert.NotNull(json);
        Assert.Equal(
            [live.ToString("D"), saturated.ToString("D")],
            json.Select(m => m.MachineId).ToArray());

        await app.StopAsync(cts.Token);
    }

    private static object Machine(
        Guid id, string slug, bool ready, bool live, DateTimeOffset spoke, bool underBackPressure = false) =>
        new
        {
            id,
            slug,
            name = "codex-apphost-linux",
            os = "linux",
            enrolledAt = spoke,
            revoked = false,
            lastSpokeAt = spoke,
            ready,
            underBackPressure,
            live,
            profiles = new[] { "codex-apphost-linux" },
            boundHumanId = (Guid?)null,
            boundAt = (DateTimeOffset?)null,
            processes = Array.Empty<object>(),
        };
}
