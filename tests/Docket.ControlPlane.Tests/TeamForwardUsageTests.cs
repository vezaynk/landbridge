using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// §9 check 10 / §9.10 relay byte accounting — <b>measurement without enforcement</b>, and the
/// one number in §9's budget story that is genuinely measured rather than authorized.
///
/// <para>These tests pin what makes it honest: attribution comes from the forward id the plane
/// itself minted (the relay never learns whose bytes it moves), the total only rises, a report
/// naming an unknown forward is dropped rather than counted or fatal, and an unmeasured Team is
/// distinguishable from a Team measured at zero.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TeamForwardUsageTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static MachineSnapshot Machine() =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    [SkippableFact]
    public async Task An_unmeasured_team_is_distinguishable_from_one_measured_at_zero()
    {
        // Zero-with-no-report means no relay has spoken, which is an absence of data and not a
        // measurement of no traffic. A reader must be able to tell them apart before concluding
        // anything from a low number.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var usage = new TeamForwardUsageService(db, TimeProvider.System);

        var view = await usage.ReadAsync(TeamId.New());

        Assert.Equal(0, view.ForwardedBytes);
        Assert.Null(view.ReportedAt);
        Assert.False(view.Measured);
    }

    [SkippableFact]
    public async Task Bytes_are_attributed_to_the_team_that_owns_the_forward()
    {
        // The relay reports an opaque forward id and nothing else — it never learns which Team
        // it is moving bytes for (§8.3). The plane minted the id, so the plane owns attribution.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var forward = await IssuedForwardAsync(db, clock, team, "db");
        var usage = new TeamForwardUsageService(db, clock);

        var attributed = await usage.RecordAsync([new ForwardByteReport(forward, 4096)]);

        Assert.Equal(1, attributed);
        var view = await usage.ReadAsync(team);
        Assert.Equal(4096, view.ForwardedBytes);
        Assert.Equal(clock.GetUtcNow(), view.ReportedAt);
        Assert.True(view.Measured);
    }

    [SkippableFact]
    public async Task Reports_accumulate_and_the_total_only_rises()
    {
        // Deltas, not running totals: a report lost in flight costs that interval's bytes rather
        // than corrupting the total. And bytes already moved cannot un-move, so a negative
        // report — a bug or a lie — must not be able to reduce the figure.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var forward = await IssuedForwardAsync(db, clock, team, "db");
        var usage = new TeamForwardUsageService(db, clock);

        await usage.RecordAsync([new ForwardByteReport(forward, 1000)]);
        await usage.RecordAsync([new ForwardByteReport(forward, 500)]);

        Assert.Equal(1500, (await usage.ReadAsync(team)).ForwardedBytes);

        Assert.Equal(0, await usage.RecordAsync([new ForwardByteReport(forward, -900)]));
        Assert.Equal(0, await usage.RecordAsync([new ForwardByteReport(forward, 0)]));
        Assert.Equal(1500, (await usage.ReadAsync(team)).ForwardedBytes);
    }

    [SkippableFact]
    public async Task Several_forwards_in_one_team_aggregate_onto_one_row()
    {
        // §9.10 is a per-TEAM figure. A preview mints a forward per browser connection, so a
        // single batch routinely names many forwards for one Team; they must land as one row
        // update rather than fight each other.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var first = await IssuedForwardAsync(db, clock, team, "db");
        var second = await IssuedForwardAsync(db, clock, team, "db");
        var usage = new TeamForwardUsageService(db, clock);

        var attributed = await usage.RecordAsync(
            [new ForwardByteReport(first, 100), new ForwardByteReport(second, 250)]);

        Assert.Equal(2, attributed);
        Assert.Equal(350, (await usage.ReadAsync(team)).ForwardedBytes);
        await using var check = pg.NewContext();
        Assert.Single(await check.TeamForwardUsage.AsNoTracking().Where(u => u.TeamId == team.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task An_unknown_forward_is_dropped_without_failing_its_batch()
    {
        // Best-effort: the relay may be racing a grant's lifetime, and failing the whole batch
        // for one unrecognised id would throw away good measurements. The returned count is how
        // an operator sees the drop instead of it being silently counted.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var known = await IssuedForwardAsync(db, clock, team, "db");
        var usage = new TeamForwardUsageService(db, clock);

        var attributed = await usage.RecordAsync(
            [new ForwardByteReport(known, 700), new ForwardByteReport(Guid.NewGuid(), 999999)]);

        Assert.Equal(1, attributed);
        Assert.Equal(700, (await usage.ReadAsync(team)).ForwardedBytes);
    }

    [SkippableFact]
    public async Task One_teams_bytes_are_never_attributed_to_another()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var mine = TeamId.New();
        var theirs = TeamId.New();
        var myForward = await IssuedForwardAsync(db, clock, mine, "db");
        await IssuedForwardAsync(db, clock, theirs, "db");
        var usage = new TeamForwardUsageService(db, clock);

        await usage.RecordAsync([new ForwardByteReport(myForward, 8192)]);

        Assert.Equal(8192, (await usage.ReadAsync(mine)).ForwardedBytes);
        Assert.Equal(0, (await usage.ReadAsync(theirs)).ForwardedBytes);
        Assert.False((await usage.ReadAsync(theirs)).Measured);
    }

    [SkippableFact]
    public async Task A_revoked_grants_forward_still_attributes()
    {
        // §8.3: an established splice survives until the owning task leaves working, and it is
        // never severed by grant expiry — so bytes legitimately keep flowing after revocation
        // and must still be counted. Revocation marks the row, it does not delete it, which is
        // what makes this work.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var forward = await IssuedForwardAsync(db, clock, team, "db");

        await db.RelayGrants.Where(g => g.ForwardId == forward)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Revoked, true));

        var usage = new TeamForwardUsageService(db, clock);
        Assert.Equal(1, await usage.RecordAsync([new ForwardByteReport(forward, 2048)]));
        Assert.Equal(2048, (await usage.ReadAsync(team)).ForwardedBytes);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A working task in <paramref name="team"/> with a registered service and one
    /// issued grant; returns the forward id the relay would report against.</summary>
    private static async Task<Guid> IssuedForwardAsync(
        DocketDbContext db, TimeProvider clock, TeamId team, string serviceName)
    {
        var store = new TaskStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null, true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        var caller = new WorkerCaller(team, created.Task.Id, instance);
        // Registering the same name twice is fine; the second call refreshes the row.
        await store.RegisterServiceAsync(caller, serviceName, 5432);
        var issued = (RelayGrantResult.Issued)await new RelayGrantService(db, clock)
            .IssueAsync(new WorkerCaller(team, TaskId.New(), WorkerInstanceId.New()), serviceName);
        return issued.ForwardId;
    }
}
