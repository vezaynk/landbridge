using System.ComponentModel;
using Docket.ControlPlane;
using Docket.Core;
using Docket.Mcp.Auth;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static Docket.Mcp.Tools.ToolResults;

namespace Docket.Mcp.Tools;

/// <summary>
/// The Lead tool surface (spec §10). A Lead is a harness client a human drives
/// (§4); these tools map onto the engine commands a lead claim authorizes.
///
/// The caller is never a parameter — it comes from the authenticated token
/// (HttpContext.User → lead claim), exactly like <see cref="WorkerTools"/>, so a
/// Lead can only ever act on its own Team. Each tool is a thin adapter over an
/// already-tested <see cref="TaskStore"/> transition; the store and the engine
/// re-check authority (§9 check 3 for creation, the §7 human-confirmation gate
/// for review, disposition for cancel), so nothing here interprets task content.
/// </summary>
[McpServerToolType]
public sealed class LeadTools(TaskStore store, RunnerConnectionRegistry registry, IHttpContextAccessor http)
{
    /// <summary>
    /// The lead claim behind this call. An evicted claim (§4) is refused with an
    /// explicit reason — evicted by whom, when — rather than a bare
    /// authorization error, so the displaced session's harness does not invent
    /// an explanation for the denial.
    /// </summary>
    private LeadClaim Lead
    {
        get
        {
            var user = http.HttpContext?.User ?? throw Unauthorized();
            if (DocketClaims.AsEvictedLead(user) is { } evicted)
                throw new McpException(
                    $"your lead claim on team {evicted.Team.Value:N} was taken over by human " +
                    $"{evicted.EvictedByHuman:N} at {evicted.EvictedAt:O}; reattach to the Team to continue.");
            return DocketClaims.AsLead(user) ?? throw Unauthorized();
        }
    }

    [McpServerTool(Name = "create_task"),
     Description("Create a task for this Team. Only a Lead may create tasks. The completion criteria " +
                 "must be non-empty; the control plane never parses it. Returns the new task id.")]
    public async Task<string> CreateTask(
        [Description("Opaque, non-empty completion criteria. In automated mode a verifier interprets it; " +
                     "in review mode a person reads it. Never parsed by the control plane.")]
        string completionCriteria,
        [Description("Completion mode: 'automated' (verifier credential) or 'review' (human-confirmed).")]
        string mode,
        [Description("Optional runner profile name for exact-match routing. Omit for the default profile.")]
        string? profile,
        CancellationToken ct)
    {
        if (!Enum.TryParse<CompletionMode>(mode, ignoreCase: true, out var parsedMode))
            throw new McpException(
                $"unknown completion mode '{mode}'; expected one of: {string.Join(", ", Enum.GetNames<CompletionMode>())}");

        var lead = Lead;
        // TeamBudgetRemains is the store's to compute from budget accounting (§9
        // check 9); the seam is here — a budget-aware store would supply it.
        var result = await store.CreateAsync(
            new CreateTask(lead, lead.Team, completionCriteria, parsedMode, profile, TeamBudgetRemains: true), ct);

        return result switch
        {
            StoreResult.Applied a => a.Task.Id.ToString(),
            _ => throw Rejection(result),
        };
    }

    [McpServerTool(Name = "cancel_task"),
     Description("Cancel a task with a disposition. 'preserve' keeps the workspace; 'discard' removes this " +
                 "task's workspace instance. TTL=0 (immediate kill) is delivered by the runner, not here.")]
    public async Task<string> CancelTask(
        [Description("The task id to cancel.")] string taskId,
        [Description("Disposition: 'preserve' or 'discard'.")] string disposition,
        CancellationToken ct)
    {
        var id = ParseTaskId(taskId);
        if (!Enum.TryParse<CancelDisposition>(disposition, ignoreCase: true, out var parsed)
            || parsed == CancelDisposition.Budget)
            throw new McpException(
                "disposition must be 'preserve' or 'discard'; budget cancellation is the control plane's alone.");

        return Describe(await store.ApplyAsync(id, new Cancel(Lead, parsed), ct));
    }

    [McpServerTool(Name = "answer_input_request"),
     Description("Answer a task blocked on input, returning it to work. Use for a question or a decision " +
                 "the worker escalated. If the task's wait TTL has already expired it will have parked; " +
                 "answering then wakes it on its next redispatch.")]
    public async Task<string> AnswerInputRequest(
        [Description("The task id that is blocked on input.")] string taskId,
        CancellationToken ct)
    {
        var id = ParseTaskId(taskId);
        // LeaseStillHeld is a control-plane fact (does the dispatched machine
        // still hold the lease?), so it is read from the connection registry —
        // never assumed. If the machine is gone the engine refuses the resume
        // (LeaseNoLongerHeld) and the task parks and wakes instead (§6, §11).
        return Describe(await store.ApplyAsync(id, new AnswerInput(Lead, registry.IsLeaseHeld(id)), ct));
    }

    [McpServerTool(Name = "submit_review"),
     Description("Relay a human's review verdict for a task in verifying (review mode). The verdict MUST " +
                 "carry human confirmation: a Lead is a model, and §7 forbids an unattended lead turn from " +
                 "completing a task. Pass humanConfirmed=true only when a human actually confirmed.")]
    public async Task<string> SubmitReview(
        [Description("The task id in verifying.")] string taskId,
        [Description("The verdict: 'accept' or 'fail'.")] string verdict,
        [Description("Whether a human confirmed this verdict (e.g. via an elicitation prompt). " +
                     "Without it the control plane refuses to complete the task (§7).")]
        bool humanConfirmed,
        CancellationToken ct)
    {
        var id = ParseTaskId(taskId);
        var lead = Lead;
        TaskCommand command = verdict.ToLowerInvariant() switch
        {
            "accept" => new VerdictAccept(lead, humanConfirmed),
            "fail" => new VerdictFail(lead, humanConfirmed),
            _ => throw new McpException("verdict must be 'accept' or 'fail'."),
        };
        return Describe(await store.ApplyAsync(id, command, ct));
    }

    [McpServerTool(Name = "get_team_state"),
     Description("Read this Team's state: task counts by state and a per-task structural summary. " +
                 "Counts and states only, never prose — fetch a task's free text deliberately, one item " +
                 "at a time. This is the reattachment surface after a session ends or a takeover.")]
    public async Task<TeamStateView> GetTeamState(CancellationToken ct) =>
        await store.GetTeamStateAsync(Lead.Team, ct);

    private static TaskId ParseTaskId(string taskId) =>
        Guid.TryParse(taskId, out var g)
            ? new TaskId(g)
            : throw new McpException($"'{taskId}' is not a valid task id.");

    private static McpException Unauthorized() =>
        new("this tool requires a live lead claim; claim the Team first.");
}
