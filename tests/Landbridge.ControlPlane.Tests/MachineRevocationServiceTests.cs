using Landbridge.Contracts;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// Un-trusting a machine, whole (§5: it must take seconds; §13). Revoking credentials
/// alone was the bug: it flips the machine's own token rows and leaves the box holding an
/// authenticated <c>/runner</c> socket (auth runs once, at upgrade) plus live worker
/// tokens, which carry no machine id for a credential sweep to match. A revoked machine
/// could keep dispatching, reporting, and reading.
///
/// The socket itself is a transport concern and lives in
/// <c>RunnerSpineEndToEndTests</c>; here the connection is a fake whose close delegate
/// records that the plane hung up, which is what the registry promises the endpoint.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MachineRevocationServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly MachineDeclaration Decl = new("box-1", "macos");

    [SkippableFact]
    public async Task Revoke_closes_the_channel_requeues_the_work_and_kills_every_credential_on_the_box()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var team = TeamId.New();

        var (machineId, creds) = await EnrollAsync(clock);
        var registry = new RunnerConnectionRegistry(clock);
        var hungUp = false;
        registry.Register(
            machineId, Profiles(), (_, _) => Task.CompletedTask,
            close: _ => { hungUp = true; return Task.CompletedTask; });
        registry.ApplyHeartbeat(machineId, Ready(machineId));

        // A task dispatched to this machine, exactly as the dispatch loop would leave it:
        // working, tracked on the connection, with a live worker token in the box's hands.
        var (task, workerToken) = await DispatchOntoAsync(clock, team, machineId, registry);

        await using (var db = pg.NewContext())
            Assert.IsType<Principal.Worker>(await new TokenService(db, clock).ValidateAsync(workerToken));

        var revoked = await RevokeAsync(clock, registry, machineId);

        // The command channel: hung up on and out of the registry, so nothing dispatches or
        // sends here again.
        Assert.True(hungUp);
        Assert.True(revoked.ChannelClosed);
        Assert.Null(registry.SnapshotFor(machineId));
        Assert.Empty(registry.MachineIds());

        // Its work went back in the queue rather than being abandoned on a box no one
        // trusts — the same fact, and the same reason, as the socket dying on its own.
        Assert.Equal(1, revoked.SessionsRequeued);
        Assert.Equal(SessionState.Failed, await StateAsync(clock, task));

        // The worker token stops authenticating, which is the half the credential sweep
        // cannot reach: a worker credential carries no machine id, only {team, task,
        // instance}, so this has to come from the instance row. Counted here rather than
        // left to the requeue's own per-instance revoke, which runs afterwards and finds
        // nothing to do — the reported number is then the harnesses on the box whose token
        // just died, not a leftover.
        Assert.Equal(1, revoked.WorkersRevoked);
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, clock);
            Assert.Null(await tokens.ValidateAsync(workerToken));
            // And the machine's own pair, plus any future access it could mint.
            Assert.Null(await tokens.ValidateAsync(creds.Access.Token));
            Assert.Null(await tokens.RefreshMachineAccessAsync(creds.Refresh.Token));
        }
    }

    /// <summary>
    /// The case the requeue could not cover even if it ran first. A worker instance is
    /// revoked by the transition that ends its dispatch, so requeueing a machine's
    /// <em>tracked</em> tasks would kill those tokens on the way through — but an instance
    /// the plane is not tracking (the machine dropped before the revoke, or the plane
    /// restarted since) has no transition coming, and its token is sitting in a config file
    /// on the box, live. The sweep is keyed on where the dispatch ran, so it reaches those,
    /// and this is why it cannot be left to the requeue.
    /// </summary>
    [SkippableFact]
    public async Task Revoke_kills_worker_tokens_on_the_machine_that_no_requeue_would_have_touched()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var team = TeamId.New();
        var (machineId, _) = await EnrollAsync(clock);

        var registry = new RunnerConnectionRegistry(clock);
        registry.Register(machineId, Profiles(), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(machineId, Ready(machineId));
        var (task, workerToken) = await DispatchOntoAsync(clock, team, machineId, registry);

        // The plane forgets the dispatch — a machine that dropped, or a plane that restarted.
        // The task is still working and the token on the box is still live, so there is
        // nothing here for a requeue to find.
        registry.Untrack(task);

        var revoked = await RevokeAsync(clock, registry, machineId);

        Assert.Equal(0, revoked.SessionsRequeued);
        Assert.Equal(1, revoked.WorkersRevoked);
        await using var db = pg.NewContext();
        Assert.Null(await new TokenService(db, clock).ValidateAsync(workerToken));
    }

    /// <summary>
    /// A revoke reaches only the box it names. The fleet shares a control plane, so a
    /// revocation that swept by task or by Team would take other machines' live workers
    /// with it — an incident on one box becoming an outage across the fleet.
    /// </summary>
    [SkippableFact]
    public async Task Revoke_leaves_another_machines_connection_and_workers_alone()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var team = TeamId.New();
        var (doomed, _) = await EnrollAsync(clock);
        var (spared, sparedCreds) = await EnrollAsync(clock);

        var registry = new RunnerConnectionRegistry(clock);
        registry.Register(doomed, Profiles(), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(doomed, Ready(doomed));
        var (_, doomedWorker) = await DispatchOntoAsync(clock, team, doomed, registry);

        registry.Register(spared, Profiles(), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(spared, Ready(spared));
        var (sparedTask, sparedWorker) = await DispatchOntoAsync(clock, team, spared, registry);

        await RevokeAsync(clock, registry, doomed);

        Assert.Null(registry.SnapshotFor(doomed));
        Assert.NotNull(registry.SnapshotFor(spared));
        Assert.Contains(sparedTask, registry.SessionsOn(spared));
        Assert.Equal(SessionState.Working, await StateAsync(clock, sparedTask));

        await using var db = pg.NewContext();
        var tokens = new TokenService(db, clock);
        Assert.Null(await tokens.ValidateAsync(doomedWorker));
        Assert.IsType<Principal.Worker>(await tokens.ValidateAsync(sparedWorker));
        Assert.IsType<Principal.Machine>(await tokens.ValidateAsync(sparedCreds.Access.Token));
    }

    /// <summary>
    /// Revoking an offline or already-revoked machine reports nothing taken away rather than
    /// failing. An operator reaching for this in an incident should not have to establish the
    /// machine's state first, and a second click must not be an error.
    /// </summary>
    [SkippableFact]
    public async Task Revoking_an_offline_or_already_revoked_machine_is_a_no_op()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var (machineId, _) = await EnrollAsync(clock);
        var registry = new RunnerConnectionRegistry(clock); // never dialed in

        var first = await RevokeAsync(clock, registry, machineId);
        Assert.False(first.ChannelClosed);
        Assert.Equal(0, first.SessionsRequeued);
        Assert.Equal(0, first.WorkersRevoked);

        var again = await RevokeAsync(clock, registry, machineId);
        Assert.False(again.ChannelClosed);
        Assert.Equal(0, again.WorkersRevoked);

        // Unknown machines too — a stale id from a page left open is not an error.
        Assert.False((await RevokeAsync(clock, registry, Guid.NewGuid().ToString())).ChannelClosed);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task<MachineRevocation> RevokeAsync(
        TimeProvider clock, RunnerConnectionRegistry registry, string machineId)
    {
        var scopes = ScopeFactory(clock);
        var sink = new RunnerEventSink(
            scopes, registry, new ForwardWaiters(), new TranscriptWaiters(),
            new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await using var db = pg.NewContext();
        var service = new MachineRevocationService(
            db, new TokenService(db, clock), registry, sink, clock,
            NullLogger<MachineRevocationService>.Instance);
        return await service.RevokeAsync(Guid.Parse(machineId));
    }

    private async Task<(string MachineId, MachineCredentials Credentials)> EnrollAsync(TimeProvider clock)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, clock);
        var creds = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync()).Token, Decl);
        // The registry keys machines by the authenticated id's string form (§13).
        return (creds!.MachineId.ToString(), creds);
    }

    /// <summary>
    /// One task dispatched onto <paramref name="machineId"/> through the real store, so the
    /// worker instance row records the machine and the worker token is the one that dispatch
    /// would have shipped down the socket.
    /// </summary>
    private async Task<(SessionId Session, string WorkerToken)> DispatchOntoAsync(
        TimeProvider clock, TeamId team, string machineId, RunnerConnectionRegistry registry)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, clock);
        Assert.IsType<StoreResult.Applied>(await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "the work on the box", "default")));

        // The claim picks the task, exactly as the dispatch loop's does, so the id comes back
        // from the store rather than being assumed.
        var instance = WorkerInstanceId.New();
        // SnapshotFor is the socket (Ready=false). Dispatch reads the claim from
        // a ready snapshot the same way MachineLive hands one to DispatchNext.
        var dispatched = Assert.IsType<StoreResult.Applied>(
            await store.DispatchNextAsync(
                new MachineSnapshot(machineId, Ready: true, UnderBackPressure: false, Profiles()),
                instance));
        registry.TrackDispatch(machineId, dispatched.Session.Id);
        var token = await new TokenService(db, clock)
            .MintWorkerTokenAsync(team, dispatched.Session.Id, instance);
        return (dispatched.Session.Id, token.Token);
    }

    private async Task<SessionState?> StateAsync(TimeProvider clock, SessionId task)
    {
        await using var db = pg.NewContext();
        return await new SessionStore(db, clock).GetStateAsync(task);
    }

    private static IReadOnlySet<string> Profiles() => new HashSet<string>(StringComparer.Ordinal) { "default" };

    private static MachineHeartbeat Ready(string machineId) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], DateTimeOffset.UtcNow);

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddLandbridgeStore();
        services.AddScoped<TokenService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
