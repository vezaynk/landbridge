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

        var connection = registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", task);
        Assert.True(registry.IsLeaseHeld(task));

        // Socket closed: the machine is gone, and its lease with it (§10) — an
        // answered input must not resume the task onto a dead machine.
        registry.Unregister(connection.Token);
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
        var sink = new RunnerEventSink(ScopeFactory(clock), registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new StartedEvent(task, clock.GetUtcNow()));

        // Activity advanced to t0+30 and the task stays tracked (started confirms
        // the harness is up; requeue-on-disconnect still applies).
        var tracked = Assert.Single(registry.AllTracked());
        Assert.Equal(task, tracked.Task);
        Assert.Equal(clock.GetUtcNow(), tracked.LastActivity);
    }

    [SkippableFact]
    public async Task Usage_reported_event_persists_and_is_not_a_liveness_signal()
    {
        // §10 telemetry ingest: the sink stores the harness's own account of what a dispatch
        // consumed. It deliberately refreshes NEITHER clock — a usage report says what has
        // already been spent, and a worker wedged mid-turn can still emit one, so treating an
        // accounting line as progress would make a hung task undetectable (the exact failure the
        // two clocks exist to catch).
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var taskId = await SeedWorkingTaskAsync(clock, team, "m1");

        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", taskId); // both clocks stamped at t0
        var t0 = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(30));

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new UsageReportedEvent(
            taskId, "claude-sonnet-5[1m]",
            InputTokens: 2, OutputTokens: 4, CacheReadTokens: 18282, CacheWriteTokens: 17178,
            ReasoningOutputTokens: null, CostUsd: 0.1086186m, At: clock.GetUtcNow()));

        await using var db = pg.NewContext();
        var row = await db.TaskUsage.AsNoTracking().SingleAsync(u => u.TaskId == taskId.Value);
        Assert.Equal("claude-sonnet-5[1m]", row.Model);
        Assert.Equal(18282, row.CacheReadTokens);
        Assert.Equal(0.1086186m, row.CostUsd);
        Assert.Equal(team.Value, row.TeamId);

        // Neither clock moved: still t0, thirty seconds after the report was handled.
        var tracked = Assert.Single(registry.AllTracked());
        Assert.Equal(t0, tracked.LastActivity);
        Assert.Equal(t0, tracked.LastProgress);
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

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
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

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
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

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new RebootedEvent("m1", clock.GetUtcNow()));

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, first));
        Assert.Equal(TaskState.Submitted, await StateAsync(clock, second));
        Assert.Empty(registry.TasksOn("m1"));
    }

    // ── Event sink: exit while blocked stays tracked (§6/§11) ────────────────────

    [SkippableFact]
    public async Task Exited_while_blocked_on_input_leaves_the_task_tracked_so_the_sweeper_can_park_it()
    {
        // The live path a store-only test cannot prove: a headless worker asks a
        // question (working → blocked_on_input) and its process exits — the NORMAL
        // claude -p behaviour, and per §6/§11 not a failure. The exit must NOT
        // untrack the task, or the wait-TTL sweeper's MachineFor look-up returns
        // null and the task strands in blocked_on_input forever (never parked on
        // TTL, never requeued on machine death). Here: the exit leaves it tracked,
        // and the sweeper then parks it on TTL expiry — the transition that was
        // unreachable before the fix.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var (id, _) = await SeedBlockedTaskAsync(clock, team, "m1");
        var registry = LiveMachine(clock, "m1", id);

        // Worker exits while blocked — expected, not a disconnect.
        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new ExitedEvent(id, ExitCode: 0, clock.GetUtcNow()));

        // Still blocked, still tracked: the machine keeps the lease so the sweeper
        // can find it (before the fix the exit untracked it here and the sweeper
        // skipped it — MachineFor was null).
        Assert.Equal(TaskState.BlockedOnInput, await StateAsync(clock, id));
        Assert.Equal("m1", registry.MachineFor(id));

        // Wait TTL elapses with the machine still heartbeating → the sweeper parks.
        var sweeper = NewSweeper(clock, registry,
            waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromHours(2));
        clock.Advance(TimeSpan.FromMinutes(31));
        await sweeper.SweepAsync(CancellationToken.None);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Parked, row.State);
        Assert.Equal("m1", row.ParkMachine);
        Assert.Empty(registry.TasksOn("m1")); // sweeper untracks on the park
    }

    [SkippableFact]
    public async Task Exited_while_blocked_on_a_silent_machine_leaves_it_tracked_so_the_sweeper_requeues_it()
    {
        // Same exit-while-blocked, but the machine then goes silent past the
        // liveness window: because the exit left the task tracked, the sweeper
        // still resolves its machine, judges it dead, and requeues the task
        // (blocked_on_input → submitted, infra counter) instead of stranding it.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var (id, instance) = await SeedBlockedTaskAsync(clock, team, "m1");
        var registry = LiveMachine(clock, "m1", id);

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new ExitedEvent(id, ExitCode: 0, clock.GetUtcNow()));
        Assert.Equal("m1", registry.MachineFor(id));

        // Heartbeat goes stale before the wait TTL; the sweep requeues.
        var sweeper = NewSweeper(clock, registry,
            waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromSeconds(90));
        clock.Advance(TimeSpan.FromMinutes(2));
        await sweeper.SweepAsync(CancellationToken.None);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal(1, row.InfrastructureRequeues); // infra counter, never verification (§6)
        Assert.Null(row.ParkMachine);                 // requeued, not parked
        Assert.True((await v.WorkerInstances.AsNoTracking().SingleAsync(w => w.Id == instance.Value)).Revoked);
        Assert.Empty(registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task Exited_while_verifying_untracks_and_is_moot()
    {
        // Regression for the state-aware untrack: a task that reached verifying (the
        // worker reported a result before its process ended) is done on this
        // machine, so the exit still untracks it and changes no state — only
        // blocked_on_input is held tracked across an exit.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var (id, instance) = await SeedWorkingTaskWithInstanceAsync(clock, team, "m1");
        await using (var db = pg.NewContext())
            await new TaskStore(db, clock).ApplyAsync(
                id, new ReportResult(new WorkerCaller(team, id, instance), "git:ref-1"));

        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", id);

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new ExitedEvent(id, ExitCode: 0, clock.GetUtcNow()));

        Assert.Equal(TaskState.Verifying, await StateAsync(clock, id));
        Assert.Empty(registry.TasksOn("m1"));
    }

    // ── Event sink: derived-telemetry persistence (§10/§12, #50) ─────────────────

    [SkippableFact]
    public async Task Auth_failed_event_is_persisted_as_a_structured_task_event_row()
    {
        // §11/§12: an auth failure is no longer log-only — the sink writes the
        // structured operation/target/code/scope as a task event row (no transition),
        // and the dashboard's event query (the JSON twin's model) surfaces it.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var taskId = await SeedWorkingTaskAsync(clock, team, "m1");
        var registry = LiveMachine(clock, "m1", taskId);

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new AuthFailedEvent(
            taskId, Operation: "clone", Target: "github.com/acme/repo",
            ErrorCode: "insufficient_scope", MissingScope: "repo"));

        await using var db = pg.NewContext();
        var row = await db.TaskEvents.AsNoTracking()
            .SingleAsync(e => e.TaskId == taskId.Value && e.Kind == TaskEventRow.AuthFailedKind);
        Assert.Equal("clone", row.AuthOperation);
        Assert.Equal("github.com/acme/repo", row.AuthTarget);
        Assert.Equal("insufficient_scope", row.AuthErrorCode);
        Assert.Equal("repo", row.AuthMissingScope);
        Assert.Null(row.FromState);   // not a transition
        Assert.Null(row.ToState);

        var events = await new DashboardQueries(db, registry).GetEventsAsync();
        var evt = events.Single(e => e.Kind == TaskEventRow.AuthFailedKind);
        Assert.Equal("clone", evt.AuthOperation);
        Assert.Equal("insufficient_scope", evt.AuthErrorCode);
        Assert.Equal("repo", evt.AuthMissingScope);
    }

    [SkippableFact]
    public async Task Subagent_spawned_event_is_persisted_and_still_refreshes_activity()
    {
        // §10/§12: a subagent spawn is now a persisted progress row AND still a
        // liveness signal — it must not lose the activity refresh it had before.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var scopes = ScopeFactory(clock);
        var team = TeamId.New();
        var taskId = await SeedWorkingTaskAsync(clock, team, "m1");

        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", taskId); // stamped at t0
        clock.Advance(TimeSpan.FromSeconds(30));

        var sink = new RunnerEventSink(scopes, registry, new ForwardWaiters(), new TranscriptWaiters(), new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);
        await sink.HandleAsync(new SubagentSpawnedEvent(taskId, AgentId: "sub-1", ParentAgentId: "root", clock.GetUtcNow()));

        await using var db = pg.NewContext();
        var row = await db.TaskEvents.AsNoTracking()
            .SingleAsync(e => e.TaskId == taskId.Value && e.Kind == TaskEventRow.SubagentSpawnedKind);
        Assert.Equal("sub-1", row.SubagentId);
        Assert.Equal("root", row.SubagentParentId);

        // Activity advanced to t0+30 (the liveness half is intact).
        var tracked = Assert.Single(registry.AllTracked());
        Assert.Equal(clock.GetUtcNow(), tracked.LastActivity);

        var evt = (await new DashboardQueries(db, registry).GetEventsAsync())
            .Single(e => e.Kind == TaskEventRow.SubagentSpawnedKind);
        Assert.Equal("sub-1", evt.SubagentId);
        Assert.Equal("root", evt.SubagentParentId);
    }

    [SkippableFact]
    public async Task Request_input_stamps_the_typed_kind_on_the_blocked_event_row()
    {
        // §6/§11: the working → blocked_on_input transition carries the typed
        // request kind onto its event row so the dashboard can render what kind of
        // attention the task needs. SeedBlockedTaskAsync blocks with Question.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (id, _) = await SeedBlockedTaskAsync(clock, team, "m1");

        await using var db = pg.NewContext();
        var row = await db.TaskEvents.AsNoTracking()
            .Where(e => e.TaskId == id.Value && e.ToState == TaskState.BlockedOnInput)
            .OrderByDescending(e => e.Seq)
            .FirstAsync();
        Assert.Equal(nameof(RequestInput), row.Kind);
        Assert.Equal(InputRequestKind.Question, row.InputKind);

        var evt = (await new DashboardQueries(db, new RunnerConnectionRegistry(clock)).GetEventsAsync())
            .Single(e => e.ToState == TaskState.BlockedOnInput && e.Kind == nameof(RequestInput));
        Assert.Equal(InputRequestKind.Question, evt.InputKind);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddDocketStore();
        services.AddScoped<TokenService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private async Task<TaskId> SeedSubmittedTaskAsync(TimeProvider clock, TeamId team, string? profile)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "completion criteria", CompletionMode.Lead, profile));
        return created.Task.Id;
    }

    private async Task<TaskId> SeedWorkingTaskAsync(TimeProvider clock, TeamId team, string machineId)
    {
        var (id, _) = await SeedWorkingTaskWithInstanceAsync(clock, team, machineId);
        return id;
    }

    /// <summary>Create → dispatch, returning both the task and the dispatched
    /// worker instance so a caller can act as that worker (e.g. report a result).</summary>
    private async Task<(TaskId Id, WorkerInstanceId Instance)> SeedWorkingTaskWithInstanceAsync(
        TimeProvider clock, TeamId team, string machineId)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "completion criteria", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        var applied = (StoreResult.Applied)await store.DispatchNextAsync(
            new MachineSnapshot(machineId, Ready: true, UnderBackPressure: false, Set("default")), instance);
        return (applied.Task.Id, instance);
    }

    /// <summary>Create → dispatch → block, so the task sits in blocked_on_input with
    /// BlockedAt stamped at the current clock reading — the §11 wait shape.</summary>
    private async Task<(TaskId Id, WorkerInstanceId Instance)> SeedBlockedTaskAsync(
        TimeProvider clock, TeamId team, string machineId)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "completion criteria", CompletionMode.Lead, null));
        var id = created.Task.Id;
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot(machineId, Ready: true, UnderBackPressure: false, Set("default")), instance);
        await store.ApplyAsync(id, new RequestInput(new WorkerCaller(team, id, instance), InputRequestKind.Question));
        return (id, instance);
    }

    /// <summary>A registry with one ready machine heartbeating now and tracking the
    /// task — what DispatchService would have set up at dispatch.</summary>
    private static RunnerConnectionRegistry LiveMachine(TimeProvider clock, string machineId, TaskId task)
    {
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register(machineId, Set("default"), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(machineId, Heartbeat(machineId, "default"));
        registry.TrackDispatch(machineId, task);
        return registry;
    }

    private WaitTtlSweeper NewSweeper(
        TimeProvider clock, RunnerConnectionRegistry registry,
        TimeSpan? waitTtl = null, TimeSpan? machineWindow = null, TimeSpan? sweepInterval = null) =>
        new(ScopeFactory(clock), registry, clock, NullLogger<WaitTtlSweeper>.Instance,
            waitTtl, machineWindow, sweepInterval);

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
