using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
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
    public async Task Heartbeat_upserts_liveness_columns_and_process_rows()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var machineId = await EnrollAsync(db, clock, "box");
        var session = Guid.NewGuid();
        var beat = new MachineHeartbeat(
            machineId.ToString(), Ready: true, UnderBackPressure: false,
            default, 0, ["default", "gpu"], clock.GetUtcNow(),
            Processes: [new ProcessStatus("web", ProcessState.Running, session, clock.GetUtcNow(), StdinOpen: true)]);

        await HubOutbox.WriteHeartbeatAsync(db, clock, machineId.ToString(), beat, CancellationToken.None);

        var row = await db.Machines.AsNoTracking().SingleAsync(m => m.Id == machineId);
        Assert.Equal(clock.GetUtcNow(), row.LastSpokeAt);
        Assert.True(row.Ready);
        Assert.False(row.UnderBackPressure);
        Assert.Equal(["default", "gpu"], row.Profiles);
        var proc = Assert.Single(await db.MachineProcesses.AsNoTracking().Where(p => p.MachineId == machineId).ToListAsync());
        Assert.Equal("web", proc.Name);
        Assert.Equal(nameof(ProcessState.Running), proc.State);
        Assert.True(proc.StdinOpen);

        Assert.True(await HubOutbox.IsLiveAsync(
            db, machineId, clock.GetUtcNow(), WaitTtlSweeper.DefaultMachineLivenessWindow, CancellationToken.None));
        Assert.Contains(await db.HubQueue.AsNoTracking().ToListAsync(),
            r => r.Topic == HubQueueRow.MachinesTopic && r.EntityId == machineId);
        Assert.Contains(await db.HubQueue.AsNoTracking().ToListAsync(),
            r => r.Topic == HubQueueRow.ProcessTopic && r.EntityId == proc.Id);

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
        Assert.Empty(await db.MachineProcesses.AsNoTracking().ToListAsync());
    }

    private static async Task<Guid> EnrollAsync(LandbridgeDbContext db, TimeProvider clock, string name)
    {
        var tokens = new TokenService(db, clock);
        var enrollment = await tokens.IssueEnrollmentTokenAsync();
        var credentials = await tokens.ExchangeEnrollmentAsync(
            enrollment.Token, new MachineDeclaration(name, "macos"));
        return credentials!.MachineId;
    }
}
