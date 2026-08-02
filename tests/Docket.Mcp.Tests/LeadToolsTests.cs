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
        new(new TaskStore(pg.NewContext(), _clock), registry ?? new RunnerConnectionRegistry(_clock), AccessorFor(principal));

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

        var msg = await tools.AnswerInputRequest(taskId.ToString(), CancellationToken.None);
        Assert.Contains("Submitted", msg);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Null(row.ParkMachine); // no machine to prefer; cold start
    }

    [SkippableFact]
    public async Task Answer_input_request_requeues_a_blocked_task_for_redispatch_with_resume()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register("m1", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", taskId);
        var tools = LeadFor(new Principal.Lead(Team), registry);

        // m1 still holds the lease, so the park record prefers it — but the task
        // still requeues for redispatch (→ submitted), never resumes in place: the
        // worker process is gone and resume must go back through dispatch (§11).
        var msg = await tools.AnswerInputRequest(taskId.ToString(), CancellationToken.None);
        Assert.Contains("Submitted", msg);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal("m1", row.ParkMachine); // held-lease machine preferred (§11)
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
        // first — one call, correct outcome either way (§11). It wakes and requeues.
        var tools = LeadFor(new Principal.Lead(Team), registry);
        var msg = await tools.AnswerInputRequest(taskId.ToString(), CancellationToken.None);
        Assert.Contains("Submitted", msg); // requeued for redispatch, not resumed in place

        // Redispatch the woken task and confirm a fresh worker instance reads its
        // assignment via the same read get_task delegates to. There is no separate
        // "answer" field to carry (the blocked path persists none either — §11's
        // resume-with-answer-as-prompt path is runner-side and not yet built); the
        // channel the woken worker learns through is the redispatch itself, with the
        // attempt incremented so the successor knows it inherited a workspace.
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var successor = WorkerInstanceId.New();
        var dispatched = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(Machine(), successor));
        Assert.Equal(taskId, dispatched.Task.Id);

        var assignment = await store.GetAssignmentAsync(new WorkerCaller(Team, taskId, successor));
        Assert.NotNull(assignment);
        Assert.Equal(2, assignment!.Attempt);
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

    /// <summary>Drives a task to blocked_on_input via dispatch + a worker's request.</summary>
    private async Task<TaskId> SeedBlockedOnInputTask()
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "needs input", CompletionMode.Lead, null, TeamBudgetRemains: true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new RequestInput(new WorkerCaller(Team, created.Task.Id, instance), InputRequestKind.Question));
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
        services.AddScoped<TaskStore>();
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

    /// <summary>Drives a task to verifying with an optional in-band report, in the
    /// given Team (used for both same-Team and cross-Team cases).</summary>
    private async Task<TaskId> SeedReportedTask(TeamId team, string? report)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null, TeamBudgetRemains: true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new ReportResult(new WorkerCaller(team, created.Task.Id, instance), "git:ref", report));
        return created.Task.Id;
    }

    /// <summary>Drives a review-mode task all the way to verifying via the store.</summary>
    private Task<TaskId> SeedReviewTaskInVerifying() => SeedTaskInVerifying(CompletionMode.Review);

    private async Task<TaskId> SeedTaskInVerifying(CompletionMode mode)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "adjudicate this", mode, null, TeamBudgetRemains: true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new ReportResult(new WorkerCaller(Team, created.Task.Id, instance), "git:ref"));
        return created.Task.Id;
    }
}
