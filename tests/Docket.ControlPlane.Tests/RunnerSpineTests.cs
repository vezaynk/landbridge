using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The control-plane half of the integration spine (spec §6/§10): the dispatch
/// loop turns submitted tasks into running dispatches against registered ready
/// machines, and the runner event sink drives liveness/requeue. A fake
/// connection records the commands the registry would ship down a socket, so
/// the loop is exercised without any real transport.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunnerSpineTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Dispatch ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Dispatches_a_submitted_task_to_a_registered_ready_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var taskId = await SeedSubmittedTaskAsync(clock, team, profile: null);

        var registry = new RunnerConnectionRegistry(clock);
        var captured = new List<RunnerCommand>();
        registry.Register("m1", Set("default"), (cmd, _) => { captured.Add(cmd); return Task.CompletedTask; });
        registry.ApplyHeartbeat("m1", Heartbeat("m1", "default"));

        var dispatch = new DispatchService(scopes, registry, clock, NullLogger<DispatchService>.Instance);
        await dispatch.RunDispatchPassAsync(CancellationToken.None);

        // The DispatchCommand for this task was shipped to the machine.
        var command = Assert.IsType<DispatchCommand>(Assert.Single(captured));
        Assert.Equal(taskId, command.Task);
        Assert.Equal("default", command.Profile);
        Assert.NotEqual("", command.WorkerToken);
        // A first dispatch has never parked, so it carries no resume ref (§11).
        Assert.Null(command.ResumeSessionRef);

        // The task moved submitted → working, and it is tracked on the machine.
        Assert.Equal(TaskState.Working, await StateAsync(clock, taskId));
        Assert.Contains(taskId, registry.TasksOn("m1"));

        // The minted worker token validates to a Worker principal for this task.
        await using var db = pg.NewContext();
        var principal = await new TokenService(db, clock).ValidateAsync(command.WorkerToken);
        var worker = Assert.IsType<Principal.Worker>(principal);
        Assert.Equal(taskId, worker.Caller.Task);
        Assert.Equal(team, worker.Caller.Team);
    }

    [SkippableFact]
    public async Task Does_not_dispatch_a_task_whose_profile_no_machine_declares()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var taskId = await SeedSubmittedTaskAsync(clock, team, profile: "gpu");

        var registry = new RunnerConnectionRegistry(clock);
        var captured = new List<RunnerCommand>();
        registry.Register("m1", Set("default"), (cmd, _) => { captured.Add(cmd); return Task.CompletedTask; });
        registry.ApplyHeartbeat("m1", Heartbeat("m1", "default")); // declares only "default"

        var dispatch = new DispatchService(scopes, registry, clock, NullLogger<DispatchService>.Instance);
        await dispatch.RunDispatchPassAsync(CancellationToken.None);

        Assert.Empty(captured);
        Assert.Equal(TaskState.Submitted, await StateAsync(clock, taskId));
    }

    [SkippableFact]
    public async Task Dispatch_of_a_task_with_a_stored_session_ref_sets_resume_session_ref_on_the_command()
    {
        // §11 resume: a task worked before and requeued/parked carries a harness
        // session ref on its row; (re)dispatch surfaces it (opaque, via the store)
        // and DispatchService rides it back on the DispatchCommand so docketd can
        // continue the transcript.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var taskId = await SeedSubmittedTaskAsync(clock, team, profile: null);

        // Stamp the prior work session's ref exactly as the SessionStartedEvent sink
        // would have, before this dispatch.
        await using (var db = pg.NewContext())
            await new TaskStore(db, clock).StampHarnessSessionRefAsync(taskId, "sess-prior");

        var registry = new RunnerConnectionRegistry(clock);
        var captured = new List<RunnerCommand>();
        registry.Register("m1", Set("default"), (cmd, _) => { captured.Add(cmd); return Task.CompletedTask; });
        registry.ApplyHeartbeat("m1", Heartbeat("m1", "default"));

        var dispatch = new DispatchService(scopes, registry, clock, NullLogger<DispatchService>.Instance);
        await dispatch.RunDispatchPassAsync(CancellationToken.None);

        var command = Assert.IsType<DispatchCommand>(Assert.Single(captured));
        Assert.Equal(taskId, command.Task);
        Assert.Equal("sess-prior", command.ResumeSessionRef);
    }

    // ── Lease liveness ──────────────────────────────────────────────────────────

    [Fact]
    public void Lease_is_held_while_tracked_on_a_connected_machine_and_lost_when_it_goes()
    {
        var clock = new FakeTimeProvider();
        var registry = new RunnerConnectionRegistry(clock);
        var task = TaskId.New();

        // Never dispatched: no lease.
        Assert.False(registry.IsLeaseHeld(task));

        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", task);
        Assert.True(registry.IsLeaseHeld(task));

        // Socket closed: the machine is gone, and its lease with it (§10) — an
        // answered input must not resume the task onto a dead machine.
        registry.Unregister("m1");
        Assert.False(registry.IsLeaseHeld(task));
    }

    [Fact]
    public void Lease_is_lost_when_the_task_is_untracked()
    {
        var registry = new RunnerConnectionRegistry(new FakeTimeProvider());
        var task = TaskId.New();
        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", task);

        registry.Untrack(task); // exit / requeue / reboot

        Assert.False(registry.IsLeaseHeld(task));
    }

    // ── Event sink: liveness / requeue ──────────────────────────────────────────

    [SkippableFact]
    public async Task Started_event_refreshes_task_activity()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        var task = TaskId.New();
        registry.TrackDispatch("m1", task); // stamped at t0

        clock.Advance(TimeSpan.FromSeconds(30));
        var sink = new RunnerEventSink(ScopeFactory(clock), registry, new ForwardWaiters(), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new StartedEvent(task, clock.GetUtcNow()));

        // Activity advanced to t0+30 and the task stays tracked (started confirms
        // the harness is up; requeue-on-disconnect still applies).
        var tracked = Assert.Single(registry.AllTracked());
        Assert.Equal(task, tracked.Task);
        Assert.Equal(clock.GetUtcNow(), tracked.LastActivity);
    }

    [SkippableFact]
    public async Task Session_started_event_stamps_the_harness_session_ref_on_the_row()
    {
        // §11 resume: the sink writes the opaque ref verbatim onto the task row
        // (never interpreted, like ResultReference) and refreshes activity like any
        // other liveness signal. Later a park copies it and redispatch resumes.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var taskId = await SeedWorkingTaskAsync(clock, team, "m1");

        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", taskId); // stamped at t0
        clock.Advance(TimeSpan.FromSeconds(30));

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new SessionStartedEvent(taskId, "sess-xyz", clock.GetUtcNow()));

        await using var db = pg.NewContext();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value);
        Assert.Equal("sess-xyz", row.HarnessSessionRef);
        // The task stays working and tracked; activity advanced to t0+30.
        Assert.Equal(TaskState.Working, row.State);
        var tracked = Assert.Single(registry.AllTracked());
        Assert.Equal(clock.GetUtcNow(), tracked.LastActivity);
    }

    [SkippableFact]
    public async Task Exited_while_working_requeues_the_task_to_submitted()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var taskId = await SeedWorkingTaskAsync(clock, team, "m1");

        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", taskId);

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new ExitedEvent(taskId, ExitCode: 0, clock.GetUtcNow()));

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, taskId));
        Assert.Empty(registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task Rebooted_requeues_every_task_the_machine_held()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var first = await SeedWorkingTaskAsync(clock, team, "m1");
        var second = await SeedWorkingTaskAsync(clock, team, "m1");

        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", first);
        registry.TrackDispatch("m1", second);

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new RebootedEvent("m1", clock.GetUtcNow()));

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, first));
        Assert.Equal(TaskState.Submitted, await StateAsync(clock, second));
        Assert.Empty(registry.TasksOn("m1"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

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

    private async Task<TaskId> SeedSubmittedTaskAsync(TimeProvider clock, TeamId team, string? profile)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "completion criteria", CompletionMode.Automated, profile, TeamBudgetRemains: true));
        return created.Task.Id;
    }

    private async Task<TaskId> SeedWorkingTaskAsync(TimeProvider clock, TeamId team, string machineId)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "completion criteria", CompletionMode.Automated, null, TeamBudgetRemains: true));
        var applied = (StoreResult.Applied)await store.DispatchNextAsync(
            new MachineSnapshot(machineId, Ready: true, UnderBackPressure: false, Set("default")), WorkerInstanceId.New());
        return applied.Task.Id;
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
