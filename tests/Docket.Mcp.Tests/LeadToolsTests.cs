using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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

        var idText = await tools.CreateTask("build the thing", "ship it", "automated", null, "ws:main", CancellationToken.None);

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
            () => tools.CreateTask("   ", "ship it", "automated", null, null, CancellationToken.None));
        Assert.Contains("description", ex.Message);
    }

    [SkippableFact]
    public async Task Create_task_surfaces_the_engine_rejection_for_empty_criteria()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateTask("build the thing", "   ", "automated", null, null, CancellationToken.None));
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
        Assert.Contains(nameof(Rule.CompletionRequiresNonAgentVerdict), refused.Message);

        await using (var v = pg.NewContext())
            Assert.Equal(TaskState.Verifying, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value)).State);

        // With human confirmation the same verdict completes the task.
        var ok = await tools.SubmitReview(taskId.ToString(), "accept", humanConfirmed: true, CancellationToken.None);
        Assert.Contains("Completed", ok);
        await using (var v = pg.NewContext())
            Assert.Equal(TaskState.Completed, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value)).State);
    }

    [SkippableFact]
    public async Task Get_team_state_returns_counts_and_states_scoped_to_the_lead_team()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));
        await tools.CreateTask("first", "a", "automated", null, null, CancellationToken.None);
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
        var idText = await tools.CreateTask("build the thing", "a", "automated", null, null, CancellationToken.None);

        var msg = await tools.CancelTask(idText, "preserve", CancellationToken.None);

        Assert.Contains("Canceled", msg);
    }

    [SkippableFact]
    public async Task Answer_input_request_refuses_when_the_dispatched_machine_is_gone()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        // An empty registry: the dispatched machine's connection is gone, so
        // the lease is not held (§10) — the answer must not resume the task
        // onto a dead machine; it parks and wakes instead (§6, §11).
        var tools = LeadFor(new Principal.Lead(Team));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.AnswerInputRequest(taskId.ToString(), CancellationToken.None));
        Assert.Contains(nameof(Rule.LeaseNoLongerHeld), ex.Message);

        await using var v = pg.NewContext();
        Assert.Equal(TaskState.BlockedOnInput,
            (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value)).State);
    }

    [SkippableFact]
    public async Task Answer_input_request_resumes_the_task_while_its_machine_holds_the_lease()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var taskId = await SeedBlockedOnInputTask();
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register("m1", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.TrackDispatch("m1", taskId);
        var tools = LeadFor(new Principal.Lead(Team), registry);

        var msg = await tools.AnswerInputRequest(taskId.ToString(), CancellationToken.None);

        Assert.Contains("Working", msg);
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
            () => tools.CreateTask("build the thing", "a", "automated", null, null, CancellationToken.None));
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
            () => tools.CreateTask("build the thing", "a", "automated", null, null, CancellationToken.None));
    }

    /// <summary>Drives a task to blocked_on_input via dispatch + a worker's request.</summary>
    private async Task<TaskId> SeedBlockedOnInputTask()
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "needs input", CompletionMode.Automated, null, TeamBudgetRemains: true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new RequestInput(new WorkerCaller(Team, created.Task.Id, instance), InputRequestKind.Question));
        return created.Task.Id;
    }

    /// <summary>Drives a review-mode task all the way to verifying via the store.</summary>
    private async Task<TaskId> SeedReviewTaskInVerifying()
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "review this", CompletionMode.Review, null, TeamBudgetRemains: true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new ReportResult(new WorkerCaller(Team, created.Task.Id, instance), "git:ref"));
        return created.Task.Id;
    }
}
