using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// LeadTools driven directly against a Postgres-backed <see cref="TaskStore"/>
/// with a lead principal on HttpContext.User — the precise-assertion layer for
/// the §7 human-confirmation gate, the §10 no-prose read, and the §4 eviction
/// reason. The over-the-wire path is covered by
/// <see cref="LeadWorkerEndToEndTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LeadToolsTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private readonly FakeTimeProvider _clock = new();

    private static IHttpContextAccessor AccessorFor(Principal principal) =>
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = DocketClaims.ToClaimsPrincipal(principal) } };

    private LeadTools LeadFor(Principal principal, RunnerConnectionRegistry? registry = null) =>
        RelayGrantTestKit.LeadToolsFor(
            pg.NewContext(), _clock, registry ?? new RunnerConnectionRegistry(_clock), AccessorFor(principal));

    private static MachineSnapshot Machine() =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    [SkippableFact]
    public async Task Create_task_via_the_tool_persists_a_submitted_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        var idText = await tools.CreateTask("build the thing", "ship it", "lead", null, "ws:main", CancellationToken.None);

        var id = Guid.Parse(idText);
        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal(Team.Value, row.TeamId);
        // Description/workspace are persisted verbatim as opaque content (§7).
        Assert.Equal("build the thing", row.Description);
        Assert.Equal("ws:main", row.Workspace);
    }

    [SkippableFact]
    public async Task Create_task_rejects_an_empty_description()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        // The description is the worker's instructions; the tool refuses an empty
        // one before the command ever reaches the store.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateTask("   ", "ship it", "lead", null, null, CancellationToken.None));
        Assert.Contains("description", ex.Message);
    }

    [SkippableFact]
    public async Task Create_task_surfaces_the_engine_rejection_for_empty_criteria()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateTask("build the thing", "   ", "lead", null, null, CancellationToken.None));
        Assert.Contains(nameof(Rule.CompletionCriteriaNonEmpty), ex.Message);
    }

    [SkippableFact]
    public async Task Create_task_rejects_an_unknown_completion_mode()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        await Assert.ThrowsAsync<McpException>(
            () => tools.CreateTask("build the thing", "ship it", "eventually", null, null, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Submit_review_needs_human_confirmation_a_lead_claim_alone_cannot_complete()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedReviewTaskInVerifying();
        var tools = LeadFor(new Principal.Lead(Team));

        // §7: an unattended lead turn cannot complete a review task.
        var refused = await Assert.ThrowsAsync<McpException>(
            () => tools.SubmitReview(taskId.ToString(), "accept", humanConfirmed: false, CancellationToken.None));
        Assert.Contains(nameof(Rule.CompletionByLeadOrHuman), refused.Message);

        await using (var v = pg.NewContext())
            Assert.Equal(TaskState.Verifying, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value)).State);

        // With human confirmation the same verdict completes the task.
        var ok = await tools.SubmitReview(taskId.ToString(), "accept", humanConfirmed: true, CancellationToken.None);
        Assert.Contains("Completed", ok);
        await using (var v = pg.NewContext())
            Assert.Equal(TaskState.Completed, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value)).State);
    }

    [SkippableFact]
    public async Task Create_task_defaults_to_lead_mode_when_mode_is_omitted()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        // §7: mode omitted (null) defaults to lead — the Claude Code default.
        var idText = await tools.CreateTask("build the thing", "ship it", mode: null, null, null, CancellationToken.None);

        var id = Guid.Parse(idText);
        await using var v = pg.NewContext();
        Assert.Equal(CompletionMode.Lead, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id)).CompletionMode);
    }

    [SkippableFact]
    public async Task Submit_review_in_lead_mode_completes_without_human_confirmation_and_records_provenance()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedTaskInVerifying(CompletionMode.Lead);
        var tools = LeadFor(new Principal.Lead(Team));

        // §9 check 4: in lead mode the Lead's own verdict completes the task — no
        // humanConfirmed — and the completion records lead-session provenance.
        var ok = await tools.SubmitReview(taskId.ToString(), "accept", humanConfirmed: false, CancellationToken.None);
        Assert.Contains("Completed", ok);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value);
        Assert.Equal(TaskState.Completed, row.State);
        Assert.Equal(VerdictProvenance.LeadSession, row.CompletionProvenance);
    }

    [SkippableFact]
    public async Task Get_team_state_returns_counts_and_states_scoped_to_the_lead_team()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));
        await tools.CreateTask("first", "a", "lead", null, null, CancellationToken.None);
        await tools.CreateTask("second", "b", "review", null, null, CancellationToken.None);

        var view = await tools.GetTeamState(CancellationToken.None);

        Assert.Equal(Team.Value, view.TeamId);
        Assert.Equal(2, view.TotalTasks);
        Assert.Equal(2, view.CountsByState[TaskState.Submitted]);
        Assert.All(view.Tasks, t => Assert.StartsWith($"team-{Team}/task-", t.Namespace));
    }

    [SkippableFact]
    public async Task Cancel_task_via_the_tool_moves_it_to_canceled()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));
        var idText = await tools.CreateTask("build the thing", "a", "lead", null, null, CancellationToken.None);

        var msg = await tools.CancelTask(idText, "preserve", CancellationToken.None);

        Assert.Contains("Canceled", msg);
    }

    [SkippableFact]
    public async Task Answer_input_request_requeues_for_a_cold_start_when_the_dispatched_machine_is_gone()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        // An empty registry: the dispatched machine's connection is gone, so there
        // is no held-lease machine (§10). The worker process is gone the moment the
        // task blocked (§11), so the answer cannot resume in place regardless — it
        // requeues the task (→ submitted) and redispatch cold-starts it elsewhere
        // from the workspace (§6, §11), rather than refusing.
        var tools = LeadFor(new Principal.Lead(Team));

        var msg = await tools.AnswerInputRequest(taskId.ToString(), "use the staging DB", CancellationToken.None);
        Assert.Contains("Submitted", msg);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Null(row.ParkMachine); // no machine to prefer; cold start
        // §11: the cold-start path is exactly where the answer matters most — the
        // transcript that held the question is on a machine that is gone, so the row
        // is the only thing carrying it into the next attempt.
        Assert.Equal("use the staging DB", row.InputAnswer);
        Assert.Equal(SeededQuestion, row.InputQuestion);
    }

    [SkippableFact]
    public async Task Answer_input_request_requeues_a_blocked_task_for_redispatch_with_resume()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register("m1", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", taskId);
        registry.MarkProcessGone(taskId);
        var tools = LeadFor(new Principal.Lead(Team), registry);

        // m1 still holds the lease, so the park record prefers it — but the process
        // is gone, so the answer redispatches (→ submitted) rather than prompting
        // a dead session. Resume goes back through dispatch (§11).
        var msg = await tools.AnswerInputRequest(taskId.ToString(), "staging-pg, not docker", CancellationToken.None);
        Assert.Contains("Submitted", msg);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal("m1", row.ParkMachine); // held-lease machine preferred (§11)
        Assert.Equal("staging-pg, not docker", row.InputAnswer);
    }

    [SkippableFact]
    public async Task Answer_input_request_continues_a_live_session_and_sends_prompt()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        var sent = new List<RunnerCommand>();
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register("m1", new HashSet<string> { "default" }, (cmd, _) =>
        {
            sent.Add(cmd);
            return Task.CompletedTask;
        });
        registry.TrackDispatch("m1", taskId);
        var tools = LeadFor(new Principal.Lead(Team), registry);

        var msg = await tools.AnswerInputRequest(taskId.ToString(), "use staging-pg", CancellationToken.None);
        Assert.Contains("Working", msg);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value);
        Assert.Equal(TaskState.Working, row.State);
        Assert.NotNull(row.CurrentInstanceId);
        Assert.Equal("use staging-pg", row.InputAnswer);
        var prompt = Assert.IsType<PromptCommand>(Assert.Single(sent));
        Assert.Equal(taskId, prompt.Task);
    }

    [SkippableFact]
    public async Task Answering_a_task_the_sweeper_already_parked_wakes_it_and_it_redispatches()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        // ask: a dispatched worker on m1 requests input → blocked_on_input.
        var taskId = await SeedBlockedOnInputTask();

        // The registry dispatch set up: m1 live, heartbeating on the fake clock,
        // tracking the task. The sweep is about to park it and untrack the machine.
        var registry = LiveMachine("m1", taskId);

        // sweeper parks it once the wait TTL elapses — its own seam, FakeTimeProvider.
        var sweeper = NewSweeper(registry, waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromHours(2));
        _clock.Advance(TimeSpan.FromMinutes(31));
        await sweeper.SweepAsync(CancellationToken.None);
        await using (var v = pg.NewContext())
            Assert.Equal(TaskState.Parked, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value)).State);

        // The Lead answers through the SAME tool, unaware the sweeper got there
        // first — one call, correct outcome either way (§11). It wakes and requeues,
        // and the answer text lands on this branch exactly as on the blocked one:
        // the Lead does not know which branch it took, so neither may lose words.
        var tools = LeadFor(new Principal.Lead(Team), registry);
        var msg = await tools.AnswerInputRequest(
            taskId.ToString(), "use staging-pg; docker has no seed data", CancellationToken.None);
        Assert.Contains("Submitted", msg); // requeued for redispatch, not resumed in place

        // Redispatch the woken task and confirm a fresh worker instance reads its
        // assignment via the same read get_task delegates to — carrying the answer it
        // was woken for, plus the question that makes the answer legible after a cold
        // start, and the incremented attempt that warns it inherited a workspace.
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var successor = WorkerInstanceId.New();
        var dispatched = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(Machine(), successor));
        Assert.Equal(taskId, dispatched.Task.Id);

        var assignment = await store.GetAssignmentAsync(new WorkerCaller(Team, taskId, successor));
        Assert.NotNull(assignment);
        Assert.Equal(2, assignment!.Attempt);
        Assert.Equal(SeededQuestion, assignment.Question);
        Assert.Equal("use staging-pg; docker has no seed data", assignment.Answer);
    }

    [SkippableFact]
    public async Task An_evicted_lead_is_refused_every_tool_with_an_explicit_reason()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var evictedBy = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var tools = LeadFor(new Principal.EvictedLead(Team, evictedBy, at));

        // §4: not a bare authorization error — the reason names who and when.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateTask("build the thing", "a", "lead", null, null, CancellationToken.None));
        Assert.Contains("taken over", ex.Message);
        Assert.Contains(evictedBy.ToString("N"), ex.Message);

        // Reads are refused for the same reason.
        await Assert.ThrowsAsync<McpException>(() => tools.GetTeamState(CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_non_lead_principal_cannot_reach_lead_tools()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var worker = new Principal.Worker(new WorkerCaller(Team, TaskId.New(), WorkerInstanceId.New()));
        var tools = LeadFor(worker);

        await Assert.ThrowsAsync<McpException>(
            () => tools.CreateTask("build the thing", "a", "lead", null, null, CancellationToken.None));
    }

    [SkippableFact]
    public void List_profiles_shows_a_lead_the_declared_profiles_the_machines_offering_them_and_liveness()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // §7/§10: the routing read. A Lead about to set create_task(profile:) needs the
        // names that exist, where each can run, and whether it can run there now — exact
        // match means a guessed name is a task nothing ever claims.
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register("m1", new HashSet<string> { "default", "gpu" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("m1", Heartbeat("m1", "default", "gpu"));
        registry.Register("m2", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("m2", Heartbeat("m2", "default"));
        var tools = LeadFor(new Principal.Lead(Team), registry);

        var view = tools.ListProfiles();

        Assert.Equal(new[] { "default", "gpu" }, view.Profiles.Select(p => p.Profile));
        Assert.Equal(2, view.ConnectedMachines);
        Assert.Equal(MachineSnapshot.DefaultProfile, view.DefaultProfile);

        var shared = view.Profiles.Single(p => p.Profile == "default");
        Assert.Equal(new[] { "m1", "m2" }, shared.Machines.Select(m => m.MachineId));
        Assert.True(shared.Dispatchable);
        // Liveness per machine, so "this profile is reachable NOW" is answerable rather
        // than inferred from the mere fact that a machine once declared it.
        Assert.All(shared.Machines, m =>
        {
            Assert.True(m.Ready);
            Assert.False(m.UnderBackPressure);
            Assert.NotNull(m.LastHeartbeat);
        });

        // The narrower profile carries only the machine that declares it (§7 exact match).
        Assert.Equal(new[] { "m1" }, view.Profiles.Single(p => p.Profile == "gpu").Machines
            .Select(m => m.MachineId));
    }

    [SkippableFact]
    public void List_profiles_refuses_a_worker_and_tells_it_nothing_about_the_fleet()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // Lead-only through the real authority path: the principal is rebuilt from the
        // claims on HttpContext.User, so a worker's credential is refused by the same
        // LeadPrincipal check every other tool here makes — no test-only seam.
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register("secret-machine", new HashSet<string> { "restricted" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("secret-machine", Heartbeat("secret-machine", "restricted"));
        var worker = new Principal.Worker(new WorkerCaller(Team, TaskId.New(), WorkerInstanceId.New()));

        var refused = Assert.Throws<McpException>(() => LeadFor(worker, registry).ListProfiles());

        // The refusal must not leak the answer it withheld — this read is fleet-wide, so a
        // message naming a machine or a profile would hand a worker exactly the enumeration
        // it was denied.
        Assert.DoesNotContain("secret-machine", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("restricted", refused.Message, StringComparison.Ordinal);
        Assert.Contains("lead claim", refused.Message, StringComparison.Ordinal);

        // An evicted Lead is refused too, with its own §4 reason rather than this one.
        var evicted = new Principal.EvictedLead(Team, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Contains(
            "taken over",
            Assert.Throws<McpException>(() => LeadFor(evicted, registry).ListProfiles()).Message,
            StringComparison.Ordinal);
    }

    /// <summary>The question the seeded worker asks (§11), so the answer round-trip
    /// assertions have both halves of the exchange to check.</summary>
    private const string SeededQuestion = "which database should I target?";

    /// <summary>Drives a task to blocked_on_input via dispatch + a worker's request.</summary>
    private async Task<TaskId> SeedBlockedOnInputTask(
        string? question = SeededQuestion, InputRequestKind kind = InputRequestKind.Question)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "needs input", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new RequestInput(new WorkerCaller(Team, created.Task.Id, instance), kind, question));
        return created.Task.Id;
    }

    /// <summary>A registry with one ready machine heartbeating on the fake clock,
    /// tracking the task — exactly what dispatch would have set up.</summary>
    private RunnerConnectionRegistry LiveMachine(string machineId, TaskId task)
    {
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register(machineId, new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(machineId, Heartbeat(machineId, "default"));
        registry.TrackDispatch(machineId, task);
        return registry;
    }

    private WaitTtlSweeper NewSweeper(
        RunnerConnectionRegistry registry, TimeSpan? waitTtl = null, TimeSpan? machineWindow = null) =>
        new(ScopeFactory(), registry, _clock, NullLogger<WaitTtlSweeper>.Instance,
            waitTtl, machineWindow, sweepInterval: null);

    /// <summary>A scope factory over the same Postgres + fake clock, so the sweeper's
    /// per-pass scoped TaskStore writes to the DB these tools read.</summary>
    private IServiceScopeFactory ScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddDocketStore();
        services.AddScoped<TokenService>();
        services.AddSingleton<TimeProvider>(_clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static MachineHeartbeat Heartbeat(string machineId, params string[] profiles) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningTasks: 0, profiles, DateTimeOffset.UtcNow);

    [SkippableFact]
    public async Task Get_task_report_returns_the_report_delimited_as_untrusted()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        const string report = "ran the suite (green); proposes task Z on profile gpu";
        var taskId = await SeedReportedTask(Team, report);
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskReport(taskId.ToString(), CancellationToken.None);

        Assert.Contains(report, text, StringComparison.Ordinal);       // the report itself
        Assert.Contains("Untrusted", text, StringComparison.Ordinal);  // §13 delimiting
        // #81: the §8.1 artifact pointer rides the same fetch — this is the read surface the
        // column had none of, and it is what §7 has the Lead adjudicating against.
        Assert.Contains("git:ref", text, StringComparison.Ordinal);
        Assert.Contains("RESULT_REFERENCE", text, StringComparison.Ordinal); // delimited, like the prose
    }

    [SkippableFact]
    public async Task Get_task_report_surfaces_the_result_reference_when_there_is_no_report()
    {
        // #81, the case that makes the reference load-bearing rather than redundant: §6
        // requires it to reach verifying while the report is optional, so a worker that
        // left no prose still handed over an artifact — and a Lead told only "no report"
        // would be adjudicating (§7) with nothing at all.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedReportedTask(Team, report: null);
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskReport(taskId.ToString(), CancellationToken.None);

        Assert.Contains("git:ref", text, StringComparison.Ordinal);
        Assert.Contains("no worker report", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_report_says_no_reference_before_the_task_reaches_verifying()
    {
        // A task nobody has reported on has no artifact to point at. Saying that is the
        // honest answer; an empty delimited block would read as "the worker reported ''".
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedTask(Team);
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskReport(taskId.ToString(), CancellationToken.None);

        Assert.Contains("no result reference", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RESULT_REFERENCE", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_report_refuses_a_task_in_another_team()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // A task in a different Team; this Lead may not read its report (§13 scoping).
        var foreign = await SeedReportedTask(TeamId.New(), "secret");
        var tools = LeadFor(new Principal.Lead(Team));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.GetTaskReport(foreign.ToString(), CancellationToken.None));
        Assert.Contains("your Team", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_report_says_so_when_there_is_no_report()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedReportedTask(Team, report: null); // reported a result, no report
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskReport(taskId.ToString(), CancellationToken.None);
        Assert.Contains("no worker report", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_report_carries_the_infrastructure_account_of_a_requeued_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // Two requeues against a cap of five, then a worker that finally reported: the
        // report is worth reading AND the task took three machines to get there (§9
        // check 7), and #91 was that the second fact reached get_team_state but not here.
        const string report = "ran the suite (green) on the third machine";
        var taskId = await SeedRequeuedTask(
            requeueLimit: 5, requeues: 2, LivenessLossReason.MachineReboot, report);
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskReport(taskId.ToString(), CancellationToken.None);

        Assert.Contains("2 of 5 infrastructure requeues", text, StringComparison.Ordinal);
        Assert.Contains(nameof(LivenessLossReason.MachineReboot), text, StringComparison.Ordinal);
        Assert.Contains(report, text, StringComparison.Ordinal); // still the report read it was
        // The account is the plane's own structure, so it sits OUTSIDE the untrusted
        // fences — a Lead that distrusted these numbers would have nothing left to trust.
        Assert.EndsWith("instead of dispatching it again.", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_report_says_the_cap_ended_a_task_it_abandoned()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // A cap of one, so a single wedge abandons it: no result reference, no report, and
        // before #91 nothing but two "there is nothing here" lines. The account is the
        // ONLY answer to "what happened to this task" that exists for it.
        var taskId = await SeedRequeuedTask(
            requeueLimit: 1, requeues: 1, LivenessLossReason.NoProgress);
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskReport(taskId.ToString(), CancellationToken.None);

        Assert.Contains("1 of 1 infrastructure requeues", text, StringComparison.Ordinal);
        Assert.Contains(nameof(LivenessLossReason.NoProgress), text, StringComparison.Ordinal);
        // Legible as the cap rather than as a cancel someone asked for, and legible as
        // NOT a verdict on the work (§6 two counters — canceled, never rejected).
        Assert.Contains("ended by its requeue cap", text, StringComparison.Ordinal);
        Assert.Contains("canceled and never rejected", text, StringComparison.Ordinal);
        // And it does not read as a task still waiting to be adjudicated.
        Assert.Contains("Recovery is a new task", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_report_stays_silent_about_requeues_on_a_task_that_had_none()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // The visibility choice (#91): the account appears only when there is one. A "0 of
        // 5 requeues" line on every report read is noise on a surface §13 keeps
        // deliberately narrow, and the §12 dashboard shows no badge on a clean task either.
        var taskId = await SeedReportedTask(Team, "nothing went wrong");
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskReport(taskId.ToString(), CancellationToken.None);

        Assert.DoesNotContain("requeue", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing went wrong", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_report_is_honest_that_an_uncapped_task_has_no_cap()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // Non-positive limit is the documented opt-out (uncapped, §9 check 7). "3 of 0"
        // would be nonsense, so the count stands alone and the line says the cap is off —
        // the same honesty as "reason not recorded" on a pre-column row. It must also not
        // recite the cap's consequences at a task that has no cap to reach.
        var taskId = await SeedRequeuedTask(
            requeueLimit: 0, requeues: 3, LivenessLossReason.LivenessTimeout);
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskReport(taskId.ToString(), CancellationToken.None);

        Assert.Contains("3 infrastructure requeues", text, StringComparison.Ordinal);
        Assert.Contains("requeue cap is configured off", text, StringComparison.Ordinal);
        Assert.DoesNotContain("of 0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ended by its requeue cap", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_question_returns_the_question_delimited_and_flags_it_unanswered()
    {
        // §11: the Lead's read half. The worker's ask comes back verbatim, delimited as
        // untrusted (§13), with the typed kind and the fact that nobody has answered.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskQuestion(taskId.ToString(), CancellationToken.None);

        Assert.Contains(SeededQuestion, text, StringComparison.Ordinal);
        Assert.Contains("Untrusted", text, StringComparison.Ordinal);
        Assert.Contains("question", text, StringComparison.Ordinal);       // the typed kind
        Assert.Contains("Not yet answered", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_question_shows_the_answer_already_given_so_a_lead_does_not_answer_twice()
    {
        // §4: a Lead that reattached (or took over) needs to tell an open question from
        // a closed one before it answers — so the answer rides the same read.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        var tools = LeadFor(new Principal.Lead(Team));
        await tools.AnswerInputRequest(taskId.ToString(), "target staging-pg", CancellationToken.None);

        var text = await tools.GetTaskQuestion(taskId.ToString(), CancellationToken.None);

        Assert.Contains(SeededQuestion, text, StringComparison.Ordinal);
        Assert.Contains("target staging-pg", text, StringComparison.Ordinal);
        Assert.Contains("Already answered", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Get_task_question_refuses_a_task_in_another_team()
    {
        // §13: Team-scoped like get_task_report — a cross-Team task is refused the same
        // way an absent one is, so nothing leaks about another Team's tasks.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var foreignTeam = TeamId.New();
        var foreign = await SeedBlockedOnInputTaskIn(foreignTeam, "the other Team's secret question");
        var tools = LeadFor(new Principal.Lead(Team));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.GetTaskQuestion(foreign.ToString(), CancellationToken.None));
        Assert.Contains("your Team", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);

        // Indistinguishable from a task that never existed: same sentence, only the id
        // differs, so the refusal never reveals that the other Team's task is real.
        var absentId = Guid.NewGuid();
        var absent = await Assert.ThrowsAsync<McpException>(
            () => tools.GetTaskQuestion(absentId.ToString(), CancellationToken.None));
        Assert.Equal(
            ex.Message.Replace(foreign.ToString(), "<id>", StringComparison.Ordinal),
            absent.Message.Replace(absentId.ToString(), "<id>", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Get_task_question_says_the_lead_is_answering_blind_when_the_worker_left_no_question()
    {
        // A kind with no question is the doorbell case this feature exists to end. The
        // read says so rather than returning an empty fence, so the Lead knows it is
        // guessing and can cancel-and-rebrief instead.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask(question: null, kind: InputRequestKind.AuthHelp);
        var tools = LeadFor(new Principal.Lead(Team));

        var text = await tools.GetTaskQuestion(taskId.ToString(), CancellationToken.None);

        Assert.Contains("no question", text, StringComparison.Ordinal);
        Assert.Contains("authhelp", text, StringComparison.Ordinal); // the kind still routes it
    }

    [SkippableFact]
    public async Task Get_team_state_carries_the_question_flag_and_kind_but_never_the_question_text()
    {
        // §10's never-prose rule, on the question exactly as on the report: the bulk
        // status read gains triage structure (which task, what kind) and nothing a Lead
        // could read as instructions.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        var tools = LeadFor(new Principal.Lead(Team));

        var view = await tools.GetTeamState(CancellationToken.None);

        var summary = view.Tasks.Single(t => t.TaskId == taskId.Value);
        Assert.True(summary.HasQuestion);
        Assert.Equal(InputRequestKind.Question, summary.InputKind);
        // The prose itself appears nowhere in the serialized view.
        var json = System.Text.Json.JsonSerializer.Serialize(view);
        Assert.DoesNotContain(SeededQuestion, json, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task An_over_cap_answer_is_refused_and_the_task_stays_blocked()
    {
        // §10 cap discipline, the answer half: refusing leaves the task waiting, which
        // is recoverable — an unblocked task whose answer was dropped is not.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        var tools = LeadFor(new Principal.Lead(Team));

        var oversized = new string('x', AnswerInput.MaxAnswerBytes + 1);
        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.AnswerInputRequest(taskId.ToString(), oversized, CancellationToken.None));
        Assert.Contains(nameof(Rule.AnswerWithinSizeCap), ex.Message);
        // The refusal says where the detail belongs, so the Lead's next move is obvious.
        Assert.Contains("reference", ex.Message, StringComparison.Ordinal);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value);
        Assert.Equal(TaskState.BlockedOnInput, row.State);
        Assert.Null(row.InputAnswer);
    }

    /// <summary>Drives a task in the given Team to blocked_on_input with a question —
    /// for the cross-Team scoping case.</summary>
    private async Task<TaskId> SeedBlockedOnInputTaskIn(TeamId team, string question)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "needs input", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new RequestInput(new WorkerCaller(team, created.Task.Id, instance), InputRequestKind.Question, question));
        return created.Task.Id;
    }

    /// <summary>Drives a task to verifying with an optional in-band report, in the
    /// given Team (used for both same-Team and cross-Team cases).</summary>
    private async Task<TaskId> SeedReportedTask(TeamId team, string? report)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new ReportResult(new WorkerCaller(team, created.Task.Id, instance), "git:ref", report));
        return created.Task.Id;
    }

    /// <summary>A freshly created task, never dispatched and never reported on — so it
    /// carries neither a result reference nor a report.</summary>
    private async Task<TaskId> SeedTask(TeamId team)
    {
        await using var db = pg.NewContext();
        var created = (StoreResult.Applied)await new TaskStore(db, _clock).CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null));
        return created.Task.Id;
    }

    /// <summary>
    /// Drives a task through <paramref name="requeues"/> infrastructure requeues against a
    /// cap of <paramref name="requeueLimit"/> (§9 check 7), each one a dispatch the plane
    /// then declares dead for <paramref name="reason"/>, and optionally lets one more
    /// worker report <paramref name="report"/> afterwards. The caller picks the count/cap
    /// pair it wants — requeues past the cap are unreachable, since the requeue that
    /// reaches it is the one that abandons the task.
    /// </summary>
    private async Task<TaskId> SeedRequeuedTask(
        int requeueLimit, int requeues, LivenessLossReason reason, string? report = null)
    {
        await using var db = pg.NewContext();
        // The cap is stamped onto the row at creation from this store's policy, so only the
        // creating store needs to know it — nothing on the liveness path reads the config.
        var store = new TaskStore(db, _clock, policy: new TaskStorePolicy(requeueLimit));
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "criteria", CompletionMode.Lead, null));
        var id = created.Task.Id;

        for (var i = 0; i < requeues; i++)
        {
            Assert.IsType<StoreResult.Applied>(
                await store.DispatchNextAsync(Machine(), WorkerInstanceId.New()));
            Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(id, new LivenessLost(reason)));
        }

        if (report is null)
            return id;

        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(Machine(), instance));
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            id, new ReportResult(new WorkerCaller(Team, id, instance), "git:ref", report)));
        return id;
    }

    /// <summary>Drives a review-mode task all the way to verifying via the store.</summary>
    private Task<TaskId> SeedReviewTaskInVerifying() => SeedTaskInVerifying(CompletionMode.Review);

    private async Task<TaskId> SeedTaskInVerifying(CompletionMode mode)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "adjudicate this", mode, null));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new ReportResult(new WorkerCaller(Team, created.Task.Id, instance), "git:ref"));
        return created.Task.Id;
    }
}
