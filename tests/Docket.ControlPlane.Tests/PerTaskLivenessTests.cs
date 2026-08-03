using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// §10 per-task liveness, on its two clocks. The pair that matters is
/// <see cref="Idle_but_alive_worker_survives_past_the_aliveness_window"/> and
/// <see cref="Wedged_worker_is_still_requeued_at_the_no_progress_ceiling"/>: the
/// whole point of the design is that an idle-but-alive worker is left alone while a
/// genuinely stuck one is still caught. Get only the first and every long task is
/// requeued forever; get only the second and a wedged agent is immortal.
///
/// The <c>alive</c> events these tests feed the registry are what docketd emits on
/// its heartbeat timer for each supervised live process (see the daemon's
/// EmitAliveEvents, and RunnerDaemonTests for the emitter itself). Here they arrive
/// pre-decoded so the policy can be driven on a FakeTimeProvider.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PerTaskLivenessTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(30);

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Idle_but_alive_worker_survives_past_the_aliveness_window()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await SeedWorkingTaskAsync(clock, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // Ten minutes of a long tool call: no progress, but docketd keeps saying the
        // process is there. Under the old single-clock rule this requeued at 60s.
        StayAliveFor(clock, registry, id, TimeSpan.FromMinutes(10));
        await NewDispatch(clock, registry).CheckLivenessAsync(CancellationToken.None);

        Assert.Equal(TaskState.Working, await StateAsync(clock, id));
        Assert.Contains(id, registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task Wedged_worker_is_still_requeued_at_the_no_progress_ceiling()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await SeedWorkingTaskAsync(clock, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // Alive the whole time — the process never dies — but nothing ever progresses.
        // Past the ceiling that is a hang, and aliveness must not shield it.
        StayAliveFor(clock, registry, id, Ceiling + TimeSpan.FromMinutes(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(CancellationToken.None);

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, id));
        Assert.DoesNotContain(id, registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task A_machine_that_stops_asserting_aliveness_requeues_at_the_window()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await SeedWorkingTaskAsync(clock, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // No alive events at all: the process died without an `exited`, or docketd is
        // wedged. This is the fast path and it must stay fast.
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(CancellationToken.None);

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, id));
        Assert.DoesNotContain(id, registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task A_progress_signal_refreshes_both_clocks()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await SeedWorkingTaskAsync(clock, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // A worker that is actually getting somewhere runs indefinitely: each
        // tool-call resets the ceiling as well as the window.
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(Ceiling - TimeSpan.FromMinutes(1));
            registry.RecordProgress(id);
        }

        await NewDispatch(clock, registry).CheckLivenessAsync(CancellationToken.None);

        Assert.Equal(TaskState.Working, await StateAsync(clock, id));
    }

    [SkippableFact]
    public async Task A_service_bearing_task_is_exempt_from_the_no_progress_ceiling()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await SeedWorkingTaskAsync(clock, "m1", registerService: true);
        var registry = LiveMachine(clock, "m1", id);

        // The babysitting shape: it started a service, said so (§8.2), and now has
        // nothing to do but stay up. Hours of no progress is success, not a hang.
        StayAliveFor(clock, registry, id, TimeSpan.FromHours(4));
        await NewDispatch(clock, registry).CheckLivenessAsync(CancellationToken.None);

        Assert.Equal(TaskState.Working, await StateAsync(clock, id));
        Assert.Contains(id, registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task A_service_bearing_task_still_requeues_when_aliveness_stops()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await SeedWorkingTaskAsync(clock, "m1", registerService: true);
        var registry = LiveMachine(clock, "m1", id);

        // The exemption removes the hang detector, never the liveness floor: once the
        // process stops being reported alive, a service-bearing task requeues like
        // any other — otherwise a dead service would be indistinguishable from a
        // healthy idle one.
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(CancellationToken.None);

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, id));
    }

    [SkippableFact]
    public async Task A_configured_window_and_ceiling_are_honoured()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await SeedWorkingTaskAsync(clock, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // Both clocks are configuration, not constants (the window used to be
        // hardcoded). A deployment that wants a tighter hang detector gets one.
        var dispatch = NewDispatch(clock, registry,
            window: TimeSpan.FromSeconds(10), ceiling: TimeSpan.FromMinutes(2));

        StayAliveFor(clock, registry, id, TimeSpan.FromMinutes(1), step: TimeSpan.FromSeconds(5));
        await dispatch.CheckLivenessAsync(CancellationToken.None);
        Assert.Equal(TaskState.Working, await StateAsync(clock, id));

        StayAliveFor(clock, registry, id, TimeSpan.FromMinutes(2), step: TimeSpan.FromSeconds(5));
        await dispatch.CheckLivenessAsync(CancellationToken.None);
        Assert.Equal(TaskState.Submitted, await StateAsync(clock, id));
    }

    [SkippableFact]
    public async Task Aliveness_does_not_revive_a_blocked_or_verifying_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await SeedWorkingTaskAsync(clock, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // §11: liveness is suspended for blocked_on_input — the worker has exited by
        // design, so no alive events arrive and none are needed. The sweep must leave
        // it alone rather than requeue it on either clock.
        await BlockOnInputAsync(clock, id);

        clock.Advance(Ceiling * 2);
        await NewDispatch(clock, registry).CheckLivenessAsync(CancellationToken.None);

        Assert.Equal(TaskState.BlockedOnInput, await StateAsync(clock, id));
        Assert.Contains(id, registry.TasksOn("m1"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks the clock forward in <paramref name="step"/> increments, recording an
    /// <c>alive</c> at each one — docketd's heartbeat-cadence emission, replayed. The
    /// step must stay under the aliveness window or the task dies of silence between
    /// beats, which is the very thing these tests are distinguishing.
    /// </summary>
    private static void StayAliveFor(
        FakeTimeProvider clock, RunnerConnectionRegistry registry, TaskId task,
        TimeSpan total, TimeSpan? step = null)
    {
        var beat = step ?? TimeSpan.FromSeconds(15);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += beat)
        {
            clock.Advance(beat);
            registry.RecordAlive(task);
        }
    }

    private DispatchService NewDispatch(
        TimeProvider clock, RunnerConnectionRegistry registry,
        TimeSpan? window = null, TimeSpan? ceiling = null) =>
        new(ScopeFactory(clock), registry, clock, NullLogger<DispatchService>.Instance,
            listener: null,
            livenessWindow: window ?? Window,
            publicMcpUrl: null,
            noProgressCeiling: ceiling ?? Ceiling);

    private static RunnerConnectionRegistry LiveMachine(TimeProvider clock, string machineId, TaskId task)
    {
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register(machineId, Set("default"), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(machineId, Heartbeat(machineId, "default"));
        registry.TrackDispatch(machineId, task);
        return registry;
    }

    /// <summary>Create → dispatch, leaving the task in <c>working</c> on the machine.</summary>
    private async Task<TaskId> SeedWorkingTaskAsync(
        TimeProvider clock, string machineId, bool registerService = false)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        var team = TeamId.New();
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "completion criteria", CompletionMode.Lead, null,
                TeamBudgetRemains: true));
        var id = created.Task.Id;
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot(machineId, Ready: true, UnderBackPressure: false, Set("default")), instance);
        if (registerService)
            Assert.IsType<StoreResult.Applied>(
                await store.RegisterServiceAsync(new WorkerCaller(team, id, instance), "api", 5001));
        return id;
    }

    /// <summary>Takes a working task into blocked_on_input as its own incumbent worker.</summary>
    private async Task BlockOnInputAsync(TimeProvider clock, TaskId id)
    {
        await using var db = pg.NewContext();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        var caller = new WorkerCaller(
            new TeamId(row.TeamId), id, new WorkerInstanceId(row.CurrentInstanceId!.Value));
        await new TaskStore(db, clock).ApplyAsync(id, new RequestInput(caller, InputRequestKind.Question));
    }

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddScoped<TaskStore>();
        services.AddScoped<TokenService>();
        // §9.9: CheckLivenessAsync also sweeps exhausted budgets, which resolves this
        // from the scope — the same registration the other sweeper fixtures make.
        services.AddScoped<TeamBudgetService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private async Task<TaskState?> StateAsync(TimeProvider clock, TaskId id)
    {
        await using var db = pg.NewContext();
        return await new TaskStore(db, clock).GetStateAsync(id);
    }

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static MachineHeartbeat Heartbeat(string machineId, params string[] profiles) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningTasks: 0, profiles, DateTimeOffset.UtcNow);
}
