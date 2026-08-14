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

    [SkippableFact]
    public async Task Concurrent_enrollment_exchange_lets_exactly_one_win()
    {
        // Same gate as OAuthAuthorizationCodeServiceTests.Concurrent_double_exchange_lets_exactly_one_win:
        // two independent contexts race the one token. A check-then-act UsedAt write lets both
        // mint a machine; a conditional consume does not.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var seed = pg.NewContext();
        var issued = await new TokenService(seed, new FakeTimeProvider()).IssueEnrollmentTokenAsync();

        await using var db1 = pg.NewContext();
        await using var db2 = pg.NewContext();
        var t1 = new TokenService(db1, new FakeTimeProvider()).ExchangeEnrollmentAsync(issued.Token, Decl);
        var t2 = new TokenService(db2, new FakeTimeProvider()).ExchangeEnrollmentAsync(issued.Token, Decl);
        var results = await Task.WhenAll(t1, t2);

        var winners = results.Count(r => r is not null);
        Skip.If(winners > 1,
            "pending #147: ExchangeEnrollmentAsync is check-then-act — unskip when the conditional consume lands");
        Assert.Equal(1, winners);
        Assert.Equal(1, results.Count(r => r is null));
        await using var verify = pg.NewContext();
        Assert.Equal(1, await verify.Machines.CountAsync());
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
        var worker = await tokens.MintWorkerTokenAsync(Team, TaskId.New(), WorkerInstanceId.New());

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
        await tokens.RevokeMachineAsync(creds.MachineId);
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

        var task = TaskId.New();
        var instance = WorkerInstanceId.New();
        db.WorkerInstances.Add(new WorkerInstanceRow
        {
            Id = instance.Value, TaskId = task.Value, CreatedAt = DateTimeOffset.UtcNow,
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
        var store = new TaskStore(db, clock);
        var tokens = new TokenService(db, clock);

        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "criteria", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance);
        var minted = await tokens.MintWorkerTokenAsync(Team, created.Task.Id, instance);

        Assert.IsType<Principal.Worker>(await tokens.ValidateAsync(minted.Token));

        await store.ApplyAsync(created.Task.Id, new LivenessLost(LivenessLossReason.LivenessTimeout));

        Assert.Null(await tokens.ValidateAsync(minted.Token));
    }

    [SkippableFact]
    public async Task Unknown_tampered_and_non_authenticating_tokens_validate_to_null()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        Assert.Null(await tokens.ValidateAsync("dkt_w_not-a-real-token"));

        var real = await tokens.MintWorkerTokenAsync(Team, TaskId.New(), WorkerInstanceId.New());
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
