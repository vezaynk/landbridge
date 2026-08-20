using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using CollabProgram = Landbridge.CollabHarness.Program;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// Zero-cost fleet e2e of the ACP bridge: landbridged's profile spawn is
/// <c>landbridge-acp-bridge connect</c>, and the far side is the scripted
/// <c>Landbridge.CollabHarness --acp</c>. Same plane, same MCP, no LLM.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AcpBridgeFleetTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Scripted_worker_reports_through_the_acp_bridge()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var ct = cts.Token;

        var collab = FleetRigCollabPath();
        using var far = AcpBridgeFarSide.Start(AcpBridgeFarSide.BridgePath(), [collab, "--acp"]);
        await using var rig = new FleetRig(pg, spawnArgv: [AcpBridgeFarSide.BridgePath(), "connect", far.Url]);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");

        const string seed = "bridge-seed";
        var task = await rig.CreateSessionAsync("map:" + seed, ct);
        await rig.DispatchToAsync("A", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.HasReportAsync(task, ct), TimeSpan.FromSeconds(30)),
            "bridged scripted worker never mailed a report. " + await rig.DiagnoseAsync(task, ct));
        Assert.Equal("map:" + CollabProgram.MapTransform(seed), await rig.ResultReferenceAsync(task, ct));
    }

    /// <summary>
    /// <see cref="FleetRig"/> keeps the CollabHarness locator private; same
    /// sibling-bin rule, from this assembly's location.
    /// </summary>
    private static string FleetRigCollabPath()
    {
        var testDir = Path.GetDirectoryName(typeof(AcpBridgeFleetTests).Assembly.Location)!;
        var harnessDir = testDir.Replace(
            Path.Combine("Landbridge.MultiMachine.Tests", "bin"),
            Path.Combine("Landbridge.CollabHarness", "bin"),
            StringComparison.Ordinal);
        var apphost = Path.Combine(
            harnessDir, OperatingSystem.IsWindows() ? "Landbridge.CollabHarness.exe" : "Landbridge.CollabHarness");
        return File.Exists(apphost)
            ? apphost
            : throw new FileNotFoundException("Landbridge.CollabHarness apphost not found at " + apphost);
    }
}
