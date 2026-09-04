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
    public async Task Heartbeat_appends_machine_and_process_outbox_rows()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var machineId = Guid.NewGuid();

        await HubOutbox.WriteHeartbeatAsync(db, new FakeTimeProvider(), machineId.ToString(), CancellationToken.None);

        var rows = await db.HubQueue.AsNoTracking().ToListAsync();
        Assert.Contains(rows, r => r.Topic == HubQueueRow.MachinesTopic && r.EntityId == machineId);
        Assert.Contains(rows, r => r.Topic == HubQueueRow.ProcessesTopic && r.EntityId == machineId);
    }

    [SkippableFact]
    public async Task Heartbeat_skips_non_guid_machine_ids()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        await HubOutbox.WriteHeartbeatAsync(db, new FakeTimeProvider(), "m1", CancellationToken.None);

        Assert.Empty(await db.HubQueue.AsNoTracking().ToListAsync());
    }
}
