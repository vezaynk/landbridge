using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The §11 wait-TTL sweeper: the control-plane driver that ends a task's stay in
/// blocked_on_input. A blocked task whose Lead never answers must not hold its
/// lease forever — the sweeper parks it when the wait TTL elapses (§6:
/// blocked_on_input → parked) and requeues it when its machine goes silent (§6:
/// blocked_on_input → submitted). FakeTimeProvider drives every deadline; the
/// final case starts the real hosted loop to prove the timer fires end to end.
/// A driven RunnerConnectionRegistry stands in for live machine connections.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WaitTtlSweeperTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Wait_ttl_not_yet_expired_leaves_the_task_blocked()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (id, _) = await SeedBlockedTaskAsync(clock, team, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // Comfortably under the 30-minute TTL, machine still live.
        var sweeper = NewSweeper(clock, registry,
            waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromMinutes(29));
        await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(TaskState.BlockedOnInput, await StateAsync(clock, id));
        Assert.Contains(id, registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task Expired_wait_ttl_parks_the_task_writes_a_park_record_and_revokes_the_worker_token()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (id, instance) = await SeedBlockedTaskAsync(clock, team, "m1", registerService: true);
        var registry = LiveMachine(clock, "m1", id);

        // Machine window > wait TTL, so the machine stays live as the TTL elapses:
        // the task parks, it is not requeued.
        var sweeper = NewSweeper(clock, registry,
            waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromHours(2));
        clock.Advance(TimeSpan.FromMinutes(31));
        await sweeper.SweepAsync(CancellationToken.None);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Parked, row.State);
        // Park record (§11): the machine and attempt the plane knows…
        Assert.Equal("m1", row.ParkMachine);
        Assert.Equal(1, row.ParkAttempt);
        // …and null for the runner-side facts the plane does not hold yet (§11
        // resume seam): a wait-TTL park cold-starts from the workspace.
        Assert.Null(row.ParkDirectory);
        Assert.Null(row.ParkSessionRef);
        Assert.Null(row.CurrentInstanceId);
        Assert.Null(row.BlockedAt); // cleared on the way out of blocked_on_input

        // §5/§11: the predecessor worker-instance token is revoked before any resume.
        Assert.True((await v.WorkerInstances.AsNoTracking().SingleAsync(w => w.Id == instance.Value)).Revoked);

        // Services were cleared on the working→blocked edge (ClearServicesAndForwards);
        // a parked task has none, which the sweep preserves.
        Assert.Empty(await v.RegisteredServices.AsNoTracking().Where(s => s.TaskId == id.Value).ToListAsync());

        // The old dispatch is untracked so a later wake/redispatch starts clean.
        Assert.Empty(registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task Expired_wait_ttl_park_record_carries_the_harness_session_ref()
    {
        // §11 resume: unlike the null case above, a task whose work session reported
        // its ref (stamped on the row by the SessionStartedEvent sink) parks with
        // that ref in the park record — so redispatch resumes the transcript.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (id, _) = await SeedBlockedTaskAsync(clock, team, "m1");
        await using (var db = pg.NewContext())
            await new TaskStore(db, clock).StampHarnessSessionRefAsync(id, "sess-park");
        var registry = LiveMachine(clock, "m1", id);

        var sweeper = NewSweeper(clock, registry,
            waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromHours(2));
        clock.Advance(TimeSpan.FromMinutes(31));
        await sweeper.SweepAsync(CancellationToken.None);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Parked, row.State);
        Assert.Equal("m1", row.ParkMachine);
        // The park record's session ref is the ref stamped from the work session.
        Assert.Equal("sess-park", row.ParkSessionRef);
    }

    [SkippableFact]
    public async Task Machine_going_silent_while_waiting_requeues_the_task_to_submitted()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (id, instance) = await SeedBlockedTaskAsync(clock, team, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // The machine stops heartbeating; the sweep runs well before the wait TTL
        // but past the 90-second liveness window — the machine is gone (§6).
        var sweeper = NewSweeper(clock, registry,
            waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromSeconds(90));
        clock.Advance(TimeSpan.FromMinutes(2));
        await sweeper.SweepAsync(CancellationToken.None);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal(1, row.InfrastructureRequeues); // infra counter, never verification (§6)
        Assert.Null(row.ParkMachine);                 // requeued, not parked
        Assert.Null(row.CurrentInstanceId);
        Assert.True((await v.WorkerInstances.AsNoTracking().SingleAsync(w => w.Id == instance.Value)).Revoked);
        Assert.Empty(registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task Wait_ttl_expiry_is_rejected_once_a_lead_has_answered()
    {
        // The answer-vs-sweep race at the store: a sweep decided to park a task a
        // Lead answered a beat earlier. WaitTtlExpired lands on a now-working task
        // and the engine rejects it (wrong source state) — nothing is written, the
        // answer stands.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (id, _) = await SeedBlockedTaskAsync(clock, team, "m1");

        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        Assert.IsType<StoreResult.Applied>(
            await store.ApplyAsync(id, new AnswerInput(new LeadClaim(team), LeaseStillHeld: true)));

        var park = new ParkRecord("m1", Directory: null, HarnessSessionRef: null, Attempt: 1);
        var rejected = Assert.IsType<StoreResult.Rejected>(
            await store.ApplyAsync(id, new WaitTtlExpired(park)));
        Assert.Equal(Rule.InvalidSourceState, rejected.Rule);
        Assert.Equal(TaskState.Working, await StateAsync(clock, id));
    }

    [SkippableFact]
    public async Task An_answered_task_is_not_in_the_blocked_backlog_so_the_sweep_no_ops()
    {
        // The same race one layer up: once the Lead's answer lands, the task is no
        // longer blocked, so a full sweep pass finds nothing to park even past the
        // TTL — the task keeps working.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (id, _) = await SeedBlockedTaskAsync(clock, team, "m1");
        var registry = LiveMachine(clock, "m1", id);

        await using (var db = pg.NewContext())
            await new TaskStore(db, clock).ApplyAsync(id, new AnswerInput(new LeadClaim(team), LeaseStillHeld: true));

        var sweeper = NewSweeper(clock, registry,
            waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromHours(2));
        clock.Advance(TimeSpan.FromMinutes(31)); // past what would have been the TTL
        await sweeper.SweepAsync(CancellationToken.None);

        Assert.Equal(TaskState.Working, await StateAsync(clock, id));
    }

    [SkippableFact]
    public async Task The_hosted_sweeper_loop_parks_an_expired_task_end_to_end()
    {
        // The real hosted service on its TimeProvider timer, real clock, short
        // period — proving the loop fires a sweep and drives the park without a
        // manual SweepAsync call.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var team = TeamId.New();
        var (id, _) = await SeedBlockedTaskAsync(clock, team, "m1");
        var registry = LiveMachine(clock, "m1", id);

        var sweeper = NewSweeper(clock, registry,
            waitTtl: TimeSpan.Zero,                    // already expired the instant it blocked
            machineWindow: TimeSpan.FromHours(1),      // machine stays live → parks, not requeues
            sweepInterval: TimeSpan.FromMilliseconds(100));

        await sweeper.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                async () => await StateAsync(clock, id) == TaskState.Parked,
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
        }

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Parked, row.State);
        Assert.Equal("m1", row.ParkMachine);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private WaitTtlSweeper NewSweeper(
        TimeProvider clock, RunnerConnectionRegistry registry,
        TimeSpan? waitTtl = null, TimeSpan? machineWindow = null, TimeSpan? sweepInterval = null) =>
        new(ScopeFactory(clock), registry, clock, NullLogger<WaitTtlSweeper>.Instance,
            waitTtl, machineWindow, sweepInterval);

    /// <summary>A registry with one ready machine heartbeating now, tracking the task —
    /// exactly what DispatchService would have set up at dispatch.</summary>
    private static RunnerConnectionRegistry LiveMachine(TimeProvider clock, string machineId, TaskId task)
    {
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register(machineId, Set("default"), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(machineId, Heartbeat(machineId, "default"));
        registry.TrackDispatch(machineId, task);
        return registry;
    }

    /// <summary>Create → dispatch → block, so the task sits in blocked_on_input with
    /// BlockedAt stamped at the current (fake or real) clock reading.</summary>
    private async Task<(TaskId Id, WorkerInstanceId Instance)> SeedBlockedTaskAsync(
        TimeProvider clock, TeamId team, string machineId, bool registerService = false)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "completion criteria", CompletionMode.Automated, null, TeamBudgetRemains: true));
        var id = created.Task.Id;
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot(machineId, Ready: true, UnderBackPressure: false, Set("default")), instance);
        var caller = new WorkerCaller(team, id, instance);
        if (registerService)
            Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(caller, "api", 5001));
        await store.ApplyAsync(id, new RequestInput(caller, InputRequestKind.Question));
        return (id, instance);
    }

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddScoped<TaskStore>();
        services.AddScoped<TokenService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private async Task<TaskState?> StateAsync(TimeProvider clock, TaskId id)
    {
        await using var db = pg.NewContext();
        return await new TaskStore(db, clock).GetStateAsync(id);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met within the timeout");
    }

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static MachineHeartbeat Heartbeat(string machineId, params string[] profiles) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningTasks: 0, profiles, DateTimeOffset.UtcNow);
}
