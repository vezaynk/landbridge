using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TokenServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private static readonly MachineDeclaration Decl = new("mac-studio", "builds", "macos", "standard");

    [SkippableFact]
    public async Task Enrollment_exchanges_once_for_machine_credentials_and_never_twice()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        var enrollment = await tokens.IssueEnrollmentTokenAsync();
        var creds = await tokens.ExchangeEnrollmentAsync(enrollment.Token, Decl);

        Assert.NotNull(creds);
        Assert.StartsWith("dkt_m_", creds!.Access.Token);
        Assert.StartsWith("dkt_r_", creds.Refresh.Token);

        // Single-use (§5): the same enrollment token exchanges nothing again.
        Assert.Null(await tokens.ExchangeEnrollmentAsync(enrollment.Token, Decl));
    }

    /// <summary>
    /// Single-use has to hold under a race, not just under a replay (§5, §11). Sequential
    /// re-exchange was always refused because the first one's <c>UsedAt</c> had landed by
    /// then; two concurrent <c>POST /enroll</c> calls with the same token both read it
    /// unused, both decided, and both minted — one bootstrap secret, two machine identities
    /// in the fleet, one of which nobody enrolled.
    ///
    /// <para>Two independent contexts (so, two connections) is the whole point: the gate is
    /// the conditional update's row lock, which a single shared DbContext would serialize
    /// for the wrong reason and prove nothing about.</para>
    /// </summary>
    [SkippableFact]
    public async Task Concurrent_double_enrollment_mints_exactly_one_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var seed = pg.NewContext();
        var enrollment = await new TokenService(seed, new FakeTimeProvider()).IssueEnrollmentTokenAsync();

        await using var db1 = pg.NewContext();
        await using var db2 = pg.NewContext();
        var t1 = new TokenService(db1, new FakeTimeProvider()).ExchangeEnrollmentAsync(enrollment.Token, Decl);
        var t2 = new TokenService(db2, new FakeTimeProvider()).ExchangeEnrollmentAsync(enrollment.Token, Decl);
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results.Count(r => r is null));
        var winner = Assert.Single(results, r => r is not null)!;

        // And the loser left nothing behind: one machine row, and it is the winner's. A
        // rolled-back mint that still committed its machine would be an unenrolled box
        // holding no credentials — invisible in the fleet and impossible to revoke.
        await using var check = pg.NewContext();
        var machine = Assert.Single(await check.Set<MachineRow>().AsNoTracking().ToListAsync());
        Assert.Equal(winner.MachineId, machine.Id);
    }

    [SkippableFact]
    public async Task Expired_enrollment_tokens_exchange_nothing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var tokens = new TokenService(db, clock);

        var enrollment = await tokens.IssueEnrollmentTokenAsync();
        clock.Advance(TokenService.EnrollmentTtl + TimeSpan.FromSeconds(1));

        Assert.Null(await tokens.ExchangeEnrollmentAsync(enrollment.Token, Decl));
    }

    [SkippableFact]
    public async Task Token_exchange_is_strictly_narrowing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        // §9 check 13: nothing but an enrollment token enters the exchange.
        var creds = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync()).Token, Decl);
        var worker = await tokens.MintWorkerTokenAsync(Team, SessionId.New(), WorkerInstanceId.New());

        Assert.Null(await tokens.ExchangeEnrollmentAsync(creds!.Access.Token, Decl));
        Assert.Null(await tokens.ExchangeEnrollmentAsync(creds.Refresh.Token, Decl));
        Assert.Null(await tokens.ExchangeEnrollmentAsync(worker.Token, Decl));
    }

    [SkippableFact]
    public async Task Machine_access_validates_and_dies_with_machine_revocation()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());
        var creds = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync()).Token, Decl);

        var principal = await tokens.ValidateAsync(creds!.Access.Token);
        var machine = Assert.IsType<Principal.Machine>(principal);
        Assert.Equal(creds.MachineId, machine.MachineId);

        // §5: un-trusting a machine must take seconds — one call kills access,
        // refresh, and future refresh mints alike.
        await tokens.RevokeMachineCredentialsAsync(creds.MachineId);
        Assert.Null(await tokens.ValidateAsync(creds.Access.Token));
        Assert.Null(await tokens.RefreshMachineAccessAsync(creds.Refresh.Token));
    }

    [SkippableFact]
    public async Task Expired_access_refreshes_into_a_fresh_one()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var tokens = new TokenService(db, clock);
        var creds = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync()).Token, Decl);

        clock.Advance(TokenService.MachineAccessTtl + TimeSpan.FromMinutes(1));
        Assert.Null(await tokens.ValidateAsync(creds!.Access.Token));

        var fresh = await tokens.RefreshMachineAccessAsync(creds.Refresh.Token);
        Assert.NotNull(fresh);
        Assert.IsType<Principal.Machine>(await tokens.ValidateAsync(fresh!.Token));
    }

    [SkippableFact]
    public async Task Worker_token_authenticates_as_the_engine_actor_with_its_claims()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        var task = SessionId.New();
        var instance = WorkerInstanceId.New();
        db.WorkerInstances.Add(new WorkerInstanceRow
        {
            Id = instance.Value, SessionId = task.Value, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var minted = await tokens.MintWorkerTokenAsync(Team, task, instance);
        var principal = await tokens.ValidateAsync(minted.Token);

        var worker = Assert.IsType<Principal.Worker>(principal);
        Assert.Equal(new WorkerCaller(Team, task, instance), worker.Caller);
    }

    [SkippableFact]
    public async Task Worker_token_dies_when_the_store_requeues_its_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // The full §9.14 wiring: dispatch mints instance + token; liveness loss
        // revokes the instance row through the store's effect; the token —
        // which has no revocation state of its own — stops authenticating.
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var store = new SessionStore(db, clock);
        var tokens = new TokenService(db, clock);

        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(Team), Team, "criteria", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance);
        var minted = await tokens.MintWorkerTokenAsync(Team, created.Session.Id, instance);

        Assert.IsType<Principal.Worker>(await tokens.ValidateAsync(minted.Token));

        await store.ApplyAsync(created.Session.Id, new LivenessLost(LivenessLossReason.LivenessTimeout));

        Assert.Null(await tokens.ValidateAsync(minted.Token));
    }

    [SkippableFact]
    public async Task Unknown_tampered_and_non_authenticating_tokens_validate_to_null()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        Assert.Null(await tokens.ValidateAsync("dkt_w_not-a-real-token"));

        var real = await tokens.MintWorkerTokenAsync(Team, SessionId.New(), WorkerInstanceId.New());
        var tampered = real.Token[..^1] + (real.Token[^1] == 'A' ? 'B' : 'A');
        Assert.Null(await tokens.ValidateAsync(tampered));

        // Enrollment and refresh tokens are exchanged, never presented as identity.
        var enrollment = await tokens.IssueEnrollmentTokenAsync();
        Assert.Null(await tokens.ValidateAsync(enrollment.Token));
        var creds = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync()).Token, Decl);
        Assert.Null(await tokens.ValidateAsync(creds!.Refresh.Token));
    }
}
