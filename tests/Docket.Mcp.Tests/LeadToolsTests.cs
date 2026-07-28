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

    private LeadTools LeadFor(Principal principal) =>
        new(new TaskStore(pg.NewContext(), _clock), AccessorFor(principal));

    private static MachineSnapshot Machine() =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    [SkippableFact]
    public async Task Create_task_via_the_tool_persists_a_submitted_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        var idText = await tools.CreateTask("ship it", "automated", null, CancellationToken.None);

        var id = Guid.Parse(idText);
        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal(Team.Value, row.TeamId);
    }

    [SkippableFact]
    public async Task Create_task_surfaces_the_engine_rejection_for_empty_criteria()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateTask("   ", "automated", null, CancellationToken.None));
        Assert.Contains(nameof(Rule.CompletionCriteriaNonEmpty), ex.Message);
    }

    [SkippableFact]
    public async Task Create_task_rejects_an_unknown_completion_mode()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(new Principal.Lead(Team));

        await Assert.ThrowsAsync<McpException>(
            () => tools.CreateTask("ship it", "eventually", null, CancellationToken.None));
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
        await tools.CreateTask("a", "automated", null, CancellationToken.None);
        await tools.CreateTask("b", "review", null, CancellationToken.None);

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
        var idText = await tools.CreateTask("a", "automated", null, CancellationToken.None);

        var msg = await tools.CancelTask(idText, "preserve", CancellationToken.None);

        Assert.Contains("Canceled", msg);
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
            () => tools.CreateTask("a", "automated", null, CancellationToken.None));
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
            () => tools.CreateTask("a", "automated", null, CancellationToken.None));
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
