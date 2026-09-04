using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HubOutboxTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Heartbeat_is_liveness_the_latest_outbox_row_within_the_window()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var machineId = Guid.NewGuid();
        var beat = new MachineHeartbeat(
            machineId.ToString(), Ready: true, UnderBackPressure: false,
            default, 0, ["default"], clock.GetUtcNow(),
            Processes: [new ProcessStatus("web", ProcessState.Running, Guid.NewGuid())]);

        await HubOutbox.WriteHeartbeatAsync(db, clock, machineId.ToString(), beat, CancellationToken.None);

        Assert.True(await HubOutbox.IsLiveAsync(
            db, machineId, clock.GetUtcNow(), WaitTtlSweeper.DefaultMachineLivenessWindow, CancellationToken.None));
        var live = await HubOutbox.LiveAsync(
            db, clock.GetUtcNow(), WaitTtlSweeper.DefaultMachineLivenessWindow, CancellationToken.None);
        var snap = Assert.Contains(machineId, live);
        Assert.True(snap.Ready);
        Assert.Equal("default", Assert.Single(snap.Profiles));
        Assert.Equal("web", Assert.Single(snap.Processes!).Name);

        clock.Advance(WaitTtlSweeper.DefaultMachineLivenessWindow + TimeSpan.FromSeconds(1));
        Assert.False(await HubOutbox.IsLiveAsync(
            db, machineId, clock.GetUtcNow(), WaitTtlSweeper.DefaultMachineLivenessWindow, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Heartbeat_skips_non_guid_machine_ids()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        await HubOutbox.WriteHeartbeatAsync(
            db, new FakeTimeProvider(), "m1",
            new MachineHeartbeat("m1", true, false, default, 0, ["default"], DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Empty(await db.HubQueue.AsNoTracking().ToListAsync());
    }
}
