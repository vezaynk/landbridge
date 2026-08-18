using Landbridge.Meta.Data;
using Landbridge.Meta.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Meta.Tests;

/// <summary>
/// Port allocation is a read-then-insert, so it is only safe under the per-host
/// advisory lock <see cref="InstanceCreator.CreateAsync"/> takes. This drives real
/// concurrent creates through real Postgres — the in-memory suites are single-threaded
/// and cannot see the race, and the lock is a no-op on that provider anyway.
/// Skippable on the same terms as the migration tests.
/// </summary>
[Collection(MetaPostgresCollection.Name)]
public class PlacementConcurrencyTests(MetaPostgresFixture pg)
{
    [SkippableFact]
    public async Task Concurrent_creates_on_one_host_never_share_a_port()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var seed = pg.NewContext();
        await ResetAsync(seed);

        var host = new HostRow
        {
            Id = Guid.NewGuid(),
            Name = "concurrent-" + Guid.NewGuid().ToString("N")[..6],
            EndpointUri = "unix:///var/run/docker.sock",
            EndpointKind = HostEndpointKind.UnixSocket,
            PublishedHost = "127.0.0.1",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        seed.Hosts.Add(host);
        await seed.SaveChangesAsync();

        // Each task needs its OWN context (DbContext is not thread-safe) and its own
        // connection, so these really contend in the database rather than in EF.
        const int Racers = 8;
        var barrier = new Barrier(Racers);
        var results = await Task.WhenAll(Enumerable.Range(0, Racers).Select(async i =>
        {
            await using var db = pg.NewContext();
            var creator = new InstanceCreator(db, new PlacementService(db), new SecretGenerator(), new MetaOptions(), TimeProvider.System);
            // Line them all up so the reads genuinely overlap.
            await Task.Run(() => barrier.SignalAndWait());
            return await creator.CreateAsync(
                new CreateInstanceRequest($"race{i}", null, "latest", host.Id), default);
        }));

        Assert.Equal(Racers, results.Length);

        await using var read = pg.NewContext();
        var rows = await read.Instances.Where(x => x.HostId == host.Id).ToListAsync();
        Assert.Equal(Racers, rows.Count);

        // The real invariant: no port is used twice on this host, in EITHER role. A
        // per-column unique index would not have caught an mcp/relay cross-collision.
        var ports = rows.SelectMany(r => new[] { r.McpPublishedPort, r.RelayPublishedPort })
            .OfType<int>()
            .ToList();
        Assert.Equal(Racers * 2, ports.Count);
        Assert.Equal(ports.Count, ports.Distinct().Count());
    }

    private static async Task ResetAsync(MetaDbContext db) =>
        await db.Database.ExecuteSqlRawAsync("TRUNCATE instances, instance_steps, hosts RESTART IDENTITY CASCADE");
}
