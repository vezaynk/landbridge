using Docket.Meta.Data;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Tests;

/// <summary>
/// Validates the hand-authored migration against a real Postgres (design note §2/§7):
/// the schema applies, and the partial unique index on <c>instances(name)</c> —
/// filtered to <c>destroyed_at IS NULL</c> — really lets a destroyed tombstone free
/// its name while blocking two live instances from sharing one. Skippable: runs in
/// CI (DOCKET_TEST_PG) or where local pg tooling exists, skips otherwise.
/// </summary>
[Collection(MetaPostgresCollection.Name)]
public class MetaMigrationTests(MetaPostgresFixture pg)
{
    [SkippableFact]
    public async Task Migration_applies_and_round_trips_a_host_and_instance()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost();
        db.Hosts.Add(host);
        db.Instances.Add(NewInstance("alpha", host.Id, InstanceState.Ready));
        await db.SaveChangesAsync();

        await using var read = pg.NewContext();
        var row = await read.Instances.Include(i => i.Host).SingleAsync(i => i.Name == "alpha");
        Assert.Equal(InstanceState.Ready, row.State);
        Assert.Equal(host.Name, row.Host!.Name);
    }

    /// <summary>
    /// Why the in-flight lifecycle states needed no migration: <c>instances.state</c> is a
    /// plain <c>text</c> column with no Postgres enum type and no check constraint behind it
    /// (the model converts the CLR enum to its name), so a new state is a new value and not a
    /// new schema. Asserted rather than assumed, because the whole no-migration claim rests
    /// on it — and asserted on the raw column too, so this would also catch the day someone
    /// narrows the column and expects the enum to keep widening for free.
    /// </summary>
    [SkippableFact]
    public async Task The_in_flight_states_round_trip_the_real_schema_with_no_migration()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost();
        db.Hosts.Add(host);
        foreach (var state in InstanceStates.InFlight)
            db.Instances.Add(NewInstance(state.ToString().ToLowerInvariant(), host.Id, state));
        await db.SaveChangesAsync();

        await using var read = pg.NewContext();
        foreach (var state in InstanceStates.InFlight)
        {
            var name = state.ToString().ToLowerInvariant();
            var row = await read.Instances.AsNoTracking().SingleAsync(i => i.Name == name);
            Assert.Equal(state, row.State);
            Assert.Equal(state.ToString(), await pg.RawColumnAsync("instances", "state", row.Id));
        }
    }

    [SkippableFact]
    public async Task Partial_unique_index_blocks_two_live_but_frees_a_destroyed_name()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await ResetAsync(db);

        var host = NewHost();
        db.Hosts.Add(host);
        var first = NewInstance("dup", host.Id, InstanceState.Ready);
        db.Instances.Add(first);
        await db.SaveChangesAsync();

        // A second LIVE instance of the same name violates the partial unique index.
        await using (var db2 = pg.NewContext())
        {
            db2.Instances.Add(NewInstance("dup", host.Id, InstanceState.Provisioning));
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        }

        // Tombstone the first, then the name is free again.
        first.State = InstanceState.Destroyed;
        first.DestroyedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await using var db3 = pg.NewContext();
        db3.Instances.Add(NewInstance("dup", host.Id, InstanceState.Provisioning));
        await db3.SaveChangesAsync(); // succeeds — no live "dup" remains
        Assert.Equal(2, await db3.Instances.CountAsync(i => i.Name == "dup"));
    }

    private static HostRow NewHost() => new()
    {
        Id = Guid.NewGuid(),
        Name = "host-" + Guid.NewGuid().ToString("N")[..8],
        EndpointUri = "unix:///var/run/docker.sock",
        EndpointKind = HostEndpointKind.UnixSocket,
        PublishedHost = "127.0.0.1",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static InstanceRow NewInstance(string name, Guid hostId, InstanceState state) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        HostId = hostId,
        ImageTag = "latest",
        State = state,
        PassphraseHash = "hash",
        DbPassword = "pw",
        RelayBearer = "bearer",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task ResetAsync(MetaDbContext db) =>
        await db.Database.ExecuteSqlRawAsync("TRUNCATE instances, instance_steps, hosts RESTART IDENTITY CASCADE");
}
