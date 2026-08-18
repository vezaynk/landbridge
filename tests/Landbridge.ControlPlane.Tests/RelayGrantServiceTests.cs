using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The forward-grant service (spec §8.3): issuance gated on a registered service
/// owned by a working task in the caller's Team, and single-use-per-role
/// validation with expiry and revocation. Backed by the same ephemeral Postgres
/// fixture as the rest of the store, so it exercises the real schema (the
/// AddRelayGrants migration) end to end.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RelayGrantServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private static LeadClaim LeadFor(TeamId team) => new(team);

    private static MachineSnapshot Machine() =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    /// <summary>
    /// A producer task in <paramref name="team"/> that is working and has
    /// registered <paramref name="serviceName"/> — the precondition IssueAsync
    /// checks. Returns the producer's task id.
    /// </summary>
    private static async Task<SessionId> WorkingServiceAsync(
        LandbridgeDbContext db, TimeProvider clock, TeamId team, string serviceName)
    {
        var store = new SessionStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(LeadFor(team), team, "criteria", "default"));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        var caller = new WorkerCaller(team, created.Session.Id, instance);
        Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(caller, serviceName, 5432));
        return created.Session.Id;
    }

    // ── Issuance ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Issue_mints_an_opaque_grant_for_a_registered_working_service()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var producer = await WorkingServiceAsync(db, clock, Team, "db");
        var grants = new RelayGrantService(db, clock);

        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var issued = Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        Assert.StartsWith("lbr_g_", issued.Grant);
        Assert.NotEqual(Guid.Empty, issued.ForwardId);
        Assert.Equal(clock.GetUtcNow() + RelayGrantService.GrantTtl, issued.ExpiresAt);

        var row = await db.RelayGrants.AsNoTracking().SingleAsync(g => g.ForwardId == issued.ForwardId);
        Assert.Equal(producer.Value, row.ProducerSessionId);
        Assert.Equal(consumer.Session.Value, row.ConsumerSessionId);
        Assert.Equal("db", row.ServiceName);
        Assert.False(row.Revoked);
        // Only the hash is at rest — never the grant itself (§5).
        Assert.DoesNotContain(issued.Grant, row.GrantHash);
    }

    [SkippableFact]
    public async Task Issue_refuses_an_unregistered_service_as_not_registered()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var grants = new RelayGrantService(db, clock);

        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var refused = Assert.IsType<RelayGrantResult.Refused>(await grants.IssueAsync(consumer, "nope"));

        Assert.Equal(Rule.ForwardsRequireRegistration, refused.Rule);
        Assert.Contains("no service 'nope'", refused.Reason);
    }

    [SkippableFact]
    public async Task Issue_refuses_when_the_service_is_registered_but_its_task_is_not_working()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var store = new SessionStore(db, clock);

        // A submitted (never-working) producer with a registered-services row
        // inserted directly: the store refuses to register while not working, so
        // we manufacture the defensive case the working-owner check exists for.
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(LeadFor(Team), Team, "criteria", "default"));
        db.RegisteredServices.Add(new RegisteredServiceRow
        {
            SessionId = created.Session.Id.Value, TeamId = Team.Value, Name = "db", Port = 5432, CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync();

        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var refused = Assert.IsType<RelayGrantResult.Refused>(await new RelayGrantService(db, clock).IssueAsync(consumer, "db"));

        Assert.Equal(Rule.ForwardsRequireRegistration, refused.Rule);
        Assert.Contains("no longer working", refused.Reason);
    }

    [SkippableFact]
    public async Task Issue_from_another_team_reads_as_not_registered_and_leaks_no_name()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        await WorkingServiceAsync(db, clock, Team, "secret-db");
        var grants = new RelayGrantService(db, clock);

        // A consumer in a different Team cannot even learn the service exists (§8.2).
        var otherTeam = TeamId.New();
        var consumer = new WorkerCaller(otherTeam, SessionId.New(), WorkerInstanceId.New());
        var refused = Assert.IsType<RelayGrantResult.Refused>(await grants.IssueAsync(consumer, "secret-db"));

        Assert.Equal(Rule.ForwardsRequireRegistration, refused.Rule);
        // Reads as not-registered, exactly like an unknown name — no cross-Team tell.
        Assert.Contains("no service 'secret-db'", refused.Reason);
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Validate_accepts_the_right_grant_once_per_role()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        await WorkingServiceAsync(db, clock, Team, "db");
        var grants = new RelayGrantService(db, clock);
        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var issued = Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        // Each end opens once: consumer and producer both validate true.
        Assert.True(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Consumer));
        Assert.True(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Producer));
    }

    [SkippableFact]
    public async Task Validate_rejects_a_wrong_forward_id()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        await WorkingServiceAsync(db, clock, Team, "db");
        var grants = new RelayGrantService(db, clock);
        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var issued = Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        Assert.False(await grants.ValidateAsync(issued.Grant, Guid.NewGuid(), RelayGrantRole.Consumer));
    }

    [SkippableFact]
    public async Task Validate_rejects_an_expired_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        await WorkingServiceAsync(db, clock, Team, "db");
        var grants = new RelayGrantService(db, clock);
        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var issued = Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        // Expiry gates open (§8.3): once the TTL lapses, no tunnel may be opened.
        clock.Advance(RelayGrantService.GrantTtl + TimeSpan.FromSeconds(1));
        Assert.False(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Consumer));
    }

    [SkippableFact]
    public async Task Validate_is_single_use_per_role_but_the_other_role_still_opens()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        await WorkingServiceAsync(db, clock, Team, "db");
        var grants = new RelayGrantService(db, clock);
        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var issued = Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        Assert.True(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Consumer));
        // A second consumer open is refused — this backs the relay's duplicate-role 409.
        Assert.False(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Consumer));
        // The producer end is a different slot and is unaffected.
        Assert.True(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Producer));
    }

    [SkippableFact]
    public async Task Validate_rejects_a_revoked_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        await WorkingServiceAsync(db, clock, Team, "db");
        var grants = new RelayGrantService(db, clock);
        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var issued = Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        await db.RelayGrants.Where(g => g.ForwardId == issued.ForwardId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Revoked, true));

        Assert.False(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Consumer));
    }

    [SkippableFact]
    public async Task Leaving_working_revokes_the_producers_live_grants()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var store = new SessionStore(db, clock);

        // Producer working + service registered; a consumer holds a live grant.
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(LeadFor(Team), Team, "criteria", "default"));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        var producerCaller = new WorkerCaller(Team, created.Session.Id, instance);
        Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(producerCaller, "db", 5432));

        var grants = new RelayGrantService(db, clock);
        var consumer = new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New());
        var issued = Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        // The producer leaves working: ClearServicesAndForwards clears its
        // registered services AND revokes its live grants (§6/§8.3), one atomic step.
        await store.ApplyAsync(created.Session.Id, new LivenessLost(LivenessLossReason.LivenessTimeout));

        await using var verify = pg.NewContext();
        Assert.Empty(await verify.RegisteredServices.AsNoTracking().Where(s => s.SessionId == created.Session.Id.Value).ToListAsync());
        Assert.True((await verify.RelayGrants.AsNoTracking().SingleAsync(g => g.ForwardId == issued.ForwardId)).Revoked);
        // The revoked grant no longer opens a tunnel.
        Assert.False(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Consumer));
    }

    // ── The forward rate limit (§9 check 10) ──────────────────────────────────

    [SkippableFact]
    public async Task A_team_over_its_forward_rate_is_refused_until_the_window_rolls()
    {
        // Enforced at the MINT, plane-side: a grant is the one thing no forward can happen
        // without, so this holds even when the relay is unreachable — and it cannot be
        // outrun by a peer, because the limit sits upstream of the thing it bounds.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        // Its own Team: the rate limit is a per-Team COUNT, so a test that shared a Team with
        // its neighbours would be measuring their mints too.
        var team = TeamId.New();
        await WorkingServiceAsync(db, clock, team, "db");
        var grants = new RelayGrantService(db, clock, forwardsPerWindow: 3, forwardWindow: TimeSpan.FromMinutes(1));
        var consumer = new WorkerCaller(team, SessionId.New(), WorkerInstanceId.New());

        for (var i = 0; i < 3; i++)
            Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        var refused = Assert.IsType<RelayGrantResult.Refused>(await grants.IssueAsync(consumer, "db"));
        Assert.Equal(Rule.TeamByteAllowance, refused.Rule);
        Assert.Contains("its limit", refused.Reason);

        // Rolling, not a fixed bucket: once the window has passed, minting resumes.
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));
    }

    [SkippableFact]
    public async Task One_teams_forward_rate_does_not_limit_another_teams()
    {
        // Per Team, like every other §9 allowance. A noisy Team must not deny service to a
        // quiet one — that would make the containment control an availability bug.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var loud = TeamId.New();
        var other = TeamId.New();
        await WorkingServiceAsync(db, clock, loud, "db");
        await WorkingServiceAsync(db, clock, other, "db");
        var grants = new RelayGrantService(db, clock, forwardsPerWindow: 2, forwardWindow: TimeSpan.FromMinutes(1));

        var noisy = new WorkerCaller(loud, SessionId.New(), WorkerInstanceId.New());
        for (var i = 0; i < 2; i++)
            Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(noisy, "db"));
        Assert.IsType<RelayGrantResult.Refused>(await grants.IssueAsync(noisy, "db"));

        var quiet = new WorkerCaller(other, SessionId.New(), WorkerInstanceId.New());
        Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(quiet, "db"));
    }

    [SkippableFact]
    public async Task The_rate_limit_counts_mints_not_tunnels_that_opened()
    {
        // Authorization, not measured use — the same choice as the §9.9 ceiling. A grant minted
        // and never presented still spent the Team's allowance, because what Landbridge authorized
        // is knowable and what a peer then did with it is not.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        await WorkingServiceAsync(db, clock, team, "db");
        var grants = new RelayGrantService(db, clock, forwardsPerWindow: 2, forwardWindow: TimeSpan.FromMinutes(1));
        var consumer = new WorkerCaller(team, SessionId.New(), WorkerInstanceId.New());

        // Two mints, neither ever validated into a tunnel.
        Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));
        Assert.IsType<RelayGrantResult.Issued>(await grants.IssueAsync(consumer, "db"));

        Assert.IsType<RelayGrantResult.Refused>(await grants.IssueAsync(consumer, "db"));
    }
}
