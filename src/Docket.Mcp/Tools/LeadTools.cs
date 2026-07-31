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
     Description("Create a task for this Team. Only a Lead may create tasks. The description (prose " +
                 "instructions) and completion criteria must both be non-empty; the control plane never " +
                 "parses either. Assign a workspace so concurrent tasks don't collide. Pass 'continues' to " +
                 "resume a prior task's agent session (its conversation) under a new task id. Returns the new task id.")]
    public async Task<string> CreateTask(
        [Description("Opaque, non-empty prose instructions for the worker: what to accomplish and the " +
                     "context to meet the criteria. Read by the worker, never parsed by the control plane.")]
        string description,
        [Description("Opaque, non-empty completion criteria. In automated mode a verifier interprets it; " +
                     "in review mode a person reads it. Never parsed by the control plane.")]
        string completionCriteria,
        [Description("Completion mode: 'automated' (verifier credential) or 'review' (human-confirmed).")]
        string mode,
        [Description("Optional runner profile name for exact-match routing. Omit for the default profile. " +
                     "With 'continues', defaults to the continued task's profile.")]
        string? profile,
        [Description("Optional opaque workspace blob: where the work happens, how it is isolated, which " +
                     "ports it may use. Assigned by the Lead so concurrent tasks never collide (§7).")]
        string? workspace,
        CancellationToken ct,
        [Description("Optional: continue a prior task in THIS Team — the new task resumes that task's agent " +
                     "session (its conversation transcript) under a new task id and worker token, on the " +
                     "machine that holds it. Same-Team only. 'talk to the agent that has the context.'")]
        string? continues = null,
        [Description("For a continuation, what to do if the machine holding the session is gone at dispatch: " +
                     "'degrade' (default — cold-start a fresh session on any matching machine, losing the " +
                     "conversation) or 'pin' (wait for that machine to return). Ignored without 'continues'.")]
        string? onMachineGone = null)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new McpException("description must be non-empty; it is the worker's instructions.");

        if (!Enum.TryParse<CompletionMode>(mode, ignoreCase: true, out var parsedMode))
            throw new McpException(
                $"unknown completion mode '{mode}'; expected one of: {string.Join(", ", Enum.GetNames<CompletionMode>())}");

        var lead = Lead;

        // §6/§11 continuation targeting. The tool resolves the runtime facts the
        // engine can't reach — the continued task's Team/profile/session ref (a store
        // read) and the machine that last held it (the live connection registry) — and
        // rides them on the command as opaque content. The engine re-checks the
        // same-Team and profile gates (defense in depth); everything else is seeded
        // verbatim and never interpreted (§7).
        Continuation? continuation = null;
        var effectiveProfile = profile;
        if (!string.IsNullOrWhiteSpace(continues))
        {
            var continuedId = ParseTaskId(continues);
            var source = await store.ReadContinuationSourceAsync(continuedId, ct)
                ?? throw new McpException($"cannot continue task {continues}: no such task.");
            if (source.Team != lead.Team)
                throw new McpException($"cannot continue task {continues}: it belongs to another Team.");

            // The machine that last held/ran the continued task: the live registry
            // for a currently-tracked task, else the park record for a parked one.
            // Without it the machine-local transcript can't be located, so there is
            // nothing to resume — say so rather than silently cold-starting.
            var preferredMachine = registry.MachineFor(continuedId) ?? source.ParkMachine;
            if (preferredMachine is null)
                throw new McpException(
                    $"cannot continue task {continues}: the machine that last ran it is no longer " +
                    "connected, so its session can't be located. Create a new task instead.");

            var policy = MachineGonePolicy.Degrade;
            if (!string.IsNullOrWhiteSpace(onMachineGone))
            {
                if (!Enum.TryParse<MachineGonePolicy>(onMachineGone, ignoreCase: true, out var parsedPolicy))
                    throw new McpException(
                        $"unknown on_machine_gone '{onMachineGone}'; expected one of: " +
                        string.Join(", ", Enum.GetNames<MachineGonePolicy>()));
                policy = parsedPolicy;
            }

            effectiveProfile = string.IsNullOrWhiteSpace(profile) ? source.Profile : profile;
            // The preferred machine's declared profiles when it is connected, so the
            // engine can refuse a profile it could never honour; null when it is gone.
            var declaredProfiles = registry.SnapshotFor(preferredMachine)?.DeclaredProfiles;
            continuation = new Continuation(
                continuedId, source.Team, preferredMachine, source.HarnessSessionRef, policy, declaredProfiles);
        }
        else if (!string.IsNullOrWhiteSpace(onMachineGone))
        {
            throw new McpException("on_machine_gone only applies together with continues.");
        }

        // TeamBudgetRemains is the store's to compute from budget accounting (§9
        // check 9); the seam is here — a budget-aware store would supply it.
        // Description/workspace ride the command as opaque content the store
        // persists and the engine never reads (§7).
        var result = await store.CreateAsync(
            new CreateTask(lead, lead.Team, completionCriteria, parsedMode, effectiveProfile, TeamBudgetRemains: true,
                Description: description, Workspace: workspace, Continues: continuation), ct);

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
     Description("Answer a task blocked on input, returning it to the dispatch queue. Use for a question " +
                 "or a decision the worker escalated. The answered task is redispatched with its transcript " +
                 "resumed; if its wait TTL already expired it will have parked, and answering wakes it the " +
                 "same way.")]
    public async Task<string> AnswerInputRequest(
        [Description("The task id that is blocked on input (or already parked, if its wait TTL expired first).")]
        string taskId,
        CancellationToken ct)
    {
        var id = ParseTaskId(taskId);
        // The store routes on the task's current state so this one call is correct
        // whether or not the wait-TTL sweeper (§11) parked the task first: a task
        // still blocked_on_input is requeued for redispatch-with-resume, a task
        // already parked is woken the same way (§6, §11). The worker process is gone
        // the moment the task blocked (§11), so there is no in-place resume — the
        // machine still holding the lease is a control-plane fact read from the
        // connection registry (null if it is gone) and becomes the park record's
        // preferred machine; redispatch cold-starts elsewhere when it is null.
        return Describe(await store.AnswerOrWakeAsync(Lead, id, registry.MachineFor(id), ct));
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
