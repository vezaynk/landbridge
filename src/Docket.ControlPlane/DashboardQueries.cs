using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane;

/// <summary>
/// The read side of the §12 observability dashboard. Pure reads —
/// <c>AsNoTracking</c> over the store, plus the in-memory
/// <see cref="RunnerConnectionRegistry"/> for live machine state — returning small
/// view records that both the HTML renderer and its JSON twin serialize (§4/§12:
/// every view is also consumable as structured data by a Lead). Kept deliberately
/// out of <see cref="TaskStore"/>: the store is the write path (§15), this is a
/// bystander that only observes.
///
/// Several §12 data points still have no source in the schema; rather than invent a
/// column, the query surfaces an honest absence and the renderer shows an empty
/// state (see the field comments): permission requests and the subagent tree nested
/// under a machine. A Team's budget and its relay byte burn are no longer among them,
/// but the two numbers mean very different things and the views say so: the budget is
/// authorization COMMITTED at dispatch, never measured spend, which Docket does not
/// ingest (§9.9); the byte figure IS measured, but best-effort, reported
/// asynchronously by a relay that may die holding an unsent tail (§9.10) — which is
/// why it travels with the timestamp of its last report. The derived-telemetry events — auth
/// failures, subagent spawns, and the typed input-request kind — are persisted as
/// task event rows (#50) and surface structured in the event log; a view that still
/// renders one as an empty slot is a rendering gap that view owns, not a missing
/// source.
/// </summary>
public sealed class DashboardQueries(DocketDbContext db, RunnerConnectionRegistry registry)
{
    // The task states that mean a Team is still doing something (§12: idle Teams
    // drift to the bottom). Everything else is terminal or empty.
    private static readonly TaskState[] ActiveStates =
    [
        TaskState.Submitted, TaskState.Working, TaskState.Verifying,
        TaskState.BlockedOnInput, TaskState.Parked,
    ];

    // ── Machine Group view (§12) ──────────────────────────────────────────────

    /// <summary>
    /// Live machines with their running tasks (each tagged with its owning Team), and
    /// whether a human has bound the machine as their own (§8.3 human path — a bound
    /// machine is a forward target, which an operator should be able to see).
    /// Machines come from the connection registry's full enumeration
    /// (<see cref="RunnerConnectionRegistry.MachineIds"/>), so a machine that is
    /// connected, under back-pressure, and holding no dispatched task still appears
    /// — exactly the operator signal this view exists to surface (§12). A machine that
    /// is bound but currently disconnected is deliberately absent here, like every other
    /// disconnected machine; its Lead sees the binding in <c>get_team_state</c>. The
    /// subagent tree is a documented empty state: subagent events reach the plane only
    /// as liveness pings (§10), nothing is persisted, so there is nothing to nest.
    /// </summary>
    public async Task<IReadOnlyList<MachineView>> GetMachinesAsync(CancellationToken ct = default)
    {
        var ids = registry.MachineIds();

        // Live lead↔machine bindings, one read for the whole view (§8.3).
        var boundBy = (await db.LeadMachineBindings.AsNoTracking()
                .Where(b => !b.Revoked)
                .Select(b => new { b.MachineId, b.HumanId, b.BoundAt })
                .ToListAsync(ct))
            .ToDictionary(b => b.MachineId.ToString(), b => (b.HumanId, b.BoundAt), StringComparer.Ordinal);

        // Resolve owning Team + namespace + state for every tracked task in one read.
        var taskIds = ids.SelectMany(id => registry.TasksOn(id).Select(t => t.Value)).Distinct().ToArray();
        var taskInfo = taskIds.Length == 0
            ? new Dictionary<Guid, (Guid Team, string Namespace, TaskState State)>()
            : await db.Tasks.AsNoTracking()
                .Where(t => taskIds.Contains(t.Id))
                .Select(t => new { t.Id, t.TeamId, t.Namespace, t.State })
                .ToDictionaryAsync(t => t.Id, t => (Team: t.TeamId, Namespace: t.Namespace, State: t.State), ct);

        var machines = new List<MachineView>();
        foreach (var id in ids)
        {
            var snapshot = registry.SnapshotFor(id);
            if (snapshot is null)
                continue; // raced away between enumeration and read
            var tasks = registry.TasksOn(id)
                .Select(t => taskInfo.TryGetValue(t.Value, out var info)
                    ? new MachineTaskView(t.Value, info.Team, info.Namespace, info.State)
                    : new MachineTaskView(t.Value, Guid.Empty, "(unknown)", TaskState.Working))
                .OrderBy(t => t.Namespace, StringComparer.Ordinal)
                .ToList();
            var isBound = boundBy.TryGetValue(id, out var bound);
            machines.Add(new MachineView(
                id,
                snapshot.Ready,
                snapshot.UnderBackPressure,
                registry.LastHeartbeatFor(id),
                snapshot.DeclaredProfiles.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                tasks,
                isBound ? bound.HumanId : null,
                isBound ? bound.BoundAt : null));
        }

        return machines.OrderBy(m => m.MachineId, StringComparer.Ordinal).ToList();
    }

    // ── Team view (§12) ───────────────────────────────────────────────────────

    /// <summary>
    /// Every Team as a one-line overview, sorted so idle Teams drift to the bottom
    /// (§12). A Team exists if it owns any task, holds a live Lead claim, or has a
    /// budget configured — a ceiling set before the Lead is provisioned is the intended
    /// order. Carries the Team's budget (§9.9, committed authorization) and its reported
    /// relay bytes (§9.10, measured but best-effort).
    /// </summary>
    public async Task<IReadOnlyList<TeamOverview>> GetTeamsAsync(CancellationToken ct = default)
    {
        var stateCounts = await db.Tasks.AsNoTracking()
            .GroupBy(t => new { t.TeamId, t.State })
            .Select(g => new { g.Key.TeamId, g.Key.State, Count = g.Count() })
            .ToListAsync(ct);

        var parksByTeam = await db.TaskEvents.AsNoTracking()
            .Where(e => e.ToState == TaskState.Parked)
            .GroupBy(e => e.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);

        var lastActivity = await db.TaskEvents.AsNoTracking()
            .GroupBy(e => e.TeamId)
            .Select(g => new { TeamId = g.Key, Last = g.Max(e => e.OccurredAt) })
            .ToDictionaryAsync(x => x.TeamId, x => x.Last, ct);

        var serviceCounts = await db.RegisteredServices.AsNoTracking()
            .GroupBy(s => s.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);

        var leads = await db.Credentials.AsNoTracking()
            .Where(c => c.Kind == CredentialKind.Lead && !c.Revoked && c.TeamId != null)
            .Select(c => new { TeamId = c.TeamId!.Value, c.HumanId, c.CreatedAt })
            .ToListAsync(ct);
        var leadByTeam = leads.ToDictionary(l => l.TeamId, l => (l.HumanId, l.CreatedAt));

        var budgets = await db.TeamBudgets.AsNoTracking().ToDictionaryAsync(t => t.TeamId, ct);
        var forwardUsage = await db.TeamForwardUsage.AsNoTracking().ToDictionaryAsync(u => u.TeamId, ct);

        var teamIds = new HashSet<Guid>(stateCounts.Select(s => s.TeamId));
        foreach (var l in leads)
            teamIds.Add(l.TeamId);
        // A Team can exist as a budget alone — a human setting a ceiling before the Lead is
        // provisioned is the intended order, so it must appear in the list rather than only
        // once it has tasks.
        foreach (var teamId in budgets.Keys)
            teamIds.Add(teamId);

        var overviews = new List<TeamOverview>();
        foreach (var teamId in teamIds)
        {
            var counts = stateCounts
                .Where(s => s.TeamId == teamId)
                .ToDictionary(s => s.State, s => s.Count);
            var total = counts.Values.Sum();
            var open = counts.GetValueOrDefault(TaskState.BlockedOnInput);
            var isIdle = !ActiveStates.Any(s => counts.GetValueOrDefault(s) > 0);
            leadByTeam.TryGetValue(teamId, out var lead);
            overviews.Add(new TeamOverview(
                teamId,
                total,
                counts,
                parksByTeam.GetValueOrDefault(teamId),
                serviceCounts.GetValueOrDefault(teamId),
                open,
                leadByTeam.ContainsKey(teamId) ? lead.HumanId : null,
                leadByTeam.ContainsKey(teamId) ? lead.CreatedAt : null,
                lastActivity.GetValueOrDefault(teamId) is var la && la == default ? null : la,
                isIdle,
                TeamBudgetView.From(budgets.GetValueOrDefault(teamId)),
                TeamForwardUsageView.From(forwardUsage.GetValueOrDefault(teamId))));
        }

        // Idle Teams to the bottom (§12), otherwise most-recently-active first.
        return overviews
            .OrderBy(t => t.IsIdle)
            .ThenByDescending(t => t.LastActivity ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>
    /// One Team in full (§12 + §4 reattachment surface): tasks by state, parks per
    /// task (each park is a kill-and-resume of harness context — the number that
    /// says whether decomposition is starving on human attention), registered
    /// services, open input requests with their blocked age, kind, and question text,
    /// the attached Lead, and last activity. A §12 <em>human</em> surface, so unlike
    /// the agent-facing <c>get_team_state</c> it does carry the task's prose — the
    /// worker's report and its input exchange — because a person reading this page is
    /// the one who answers. Null when the Team owns no task and holds no Lead.
    /// </summary>
    public async Task<TeamDetail?> GetTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.TeamId == teamId)
            .Select(t => new
            {
                t.Id,
                t.Namespace,
                t.State,
                t.CompletionMode,
                t.Attempt,
                t.BlockedAt,
                Parked = t.ParkMachine != null,
                t.ParkMachine,
                t.ContinuesTaskId,
                t.CompletionProvenance,
                t.WorkerReport,
                t.InputKind,
                t.InputQuestion,
                t.InputAnswer,
            })
            .ToListAsync(ct);

        var lead = await db.Credentials.AsNoTracking()
            .Where(c => c.Kind == CredentialKind.Lead && !c.Revoked && c.TeamId == teamId)
            .Select(c => new { c.HumanId, c.CreatedAt })
            .FirstOrDefaultAsync(ct);

        if (tasks.Count == 0 && lead is null)
            return null;

        var parksByTask = await db.TaskEvents.AsNoTracking()
            .Where(e => e.TeamId == teamId && e.ToState == TaskState.Parked)
            .GroupBy(e => e.TaskId)
            .Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

        var services = await db.RegisteredServices.AsNoTracking()
            .Where(s => s.TeamId == teamId)
            .OrderBy(s => s.Name)
            .Select(s => new ServiceView(s.Name, s.Port, s.TaskId, s.CreatedAt))
            .ToListAsync(ct);

        var lastActivity = await db.TaskEvents.AsNoTracking()
            .Where(e => e.TeamId == teamId)
            .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct);

        // §9.9. Read here rather than through TeamBudgetService so this class keeps its one
        // dependency shape (db + registry) and cannot be handed a null service that silently
        // renders an unconfigured budget for a Team that has one; the row→view mapping is
        // shared with the service so the two readings cannot disagree.
        var budget = TeamBudgetView.From(
            await db.TeamBudgets.AsNoTracking().FirstOrDefaultAsync(t => t.TeamId == teamId, ct));

        // §9.10. Measured, unlike the budget beside it — and best-effort, which is why the view
        // carries the report timestamp rather than presenting the total as current.
        var forwardUsage = TeamForwardUsageView.From(
            await db.TeamForwardUsage.AsNoTracking().FirstOrDefaultAsync(u => u.TeamId == teamId, ct));

        var taskRows = tasks
            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
            .Select(t => new TeamTaskView(
                t.Id, t.Namespace, t.State, t.CompletionMode, t.Attempt,
                parksByTask.GetValueOrDefault(t.Id),
                t.Parked ? t.ParkMachine : null,
                t.State == TaskState.BlockedOnInput ? t.BlockedAt : null,
                t.ContinuesTaskId,
                t.State == TaskState.Completed ? t.CompletionProvenance : null,
                t.WorkerReport,
                t.InputKind,
                t.InputQuestion,
                t.InputAnswer))
            .ToList();

        var counts = tasks
            .GroupBy(t => t.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var inputRequests = taskRows
            .Where(t => t.State == TaskState.BlockedOnInput)
            .Select(t => new InputRequestView(
                t.TaskId, t.Namespace, teamId, t.BlockedAt, t.InputKind, t.Question))
            .ToList();

        return new TeamDetail(
            teamId,
            tasks.Count,
            counts,
            taskRows,
            services,
            inputRequests,
            lead is null ? null : lead.HumanId,
            lead?.CreatedAt,
            lastActivity,
            budget,
            forwardUsage);
    }

    // ── Human inbox (§12) ─────────────────────────────────────────────────────

    // ── Transcripts (§12 serving) ─────────────────────────────────────────────

    /// <summary>
    /// Where a task's transcripts could be — one entry per dispatch, newest first, with the
    /// machine that ran it and whether that machine is connected right now. The plane stores
    /// no transcript bytes, so this is only a set of addresses to ask (§12); a machine that
    /// is offline holds bytes nobody can read until it returns, which is why connectedness
    /// is part of the answer rather than something the page discovers by failing.
    ///
    /// <para>Instance rows written before the machine column existed have no machine and are
    /// returned with a null one, so the page can say "attempt 2's machine was not recorded"
    /// instead of silently listing fewer attempts than the task had.</para>
    /// </summary>
    public async Task<IReadOnlyList<TranscriptLocationView>> GetTranscriptLocationsAsync(
        Guid taskId, CancellationToken ct = default)
    {
        var instances = await db.WorkerInstances.AsNoTracking()
            .Where(w => w.TaskId == taskId)
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new { w.Id, w.MachineId, w.CreatedAt })
            .ToListAsync(ct);

        return instances
            .Select(w => new TranscriptLocationView(
                w.Id,
                w.MachineId,
                w.CreatedAt,
                Connected: w.MachineId is { } m && registry.SnapshotFor(m) is not null))
            .ToList();
    }

    /// <summary>
    /// Everything waiting on a person across every Team (§12): open questions
    /// (blocked_on_input) with the typed kind and the worker's own question text,
    /// tasks awaiting review (verifying + review mode, §7), and parked tasks awaiting
    /// an answer (§11) with the same question. This is where a person answers, so it is
    /// the one place the question's prose has to be legible verbatim — a §12 human
    /// surface, not a §10 agent read. Two §12 rows remain structural empty states
    /// rather than omissions: auth failures (the runner reports them but the sink only
    /// logs them — §11, no event row is written) and permission requests (not built).
    /// </summary>
    public async Task<InboxView> GetInboxAsync(CancellationToken ct = default)
    {
        var questions = await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.BlockedOnInput)
            .OrderBy(t => t.BlockedAt)
            .Select(t => new InputRequestView(
                t.Id, t.Namespace, t.TeamId, t.BlockedAt, t.InputKind, t.InputQuestion))
            .ToListAsync(ct);

        var awaitingReview = await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.Verifying && t.CompletionMode == CompletionMode.Review)
            .OrderBy(t => t.Namespace)
            .Select(t => new ReviewItemView(t.Id, t.Namespace, t.TeamId))
            .ToListAsync(ct);

        var parked = await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.Parked)
            .OrderBy(t => t.Namespace)
            .Select(t => new ParkedItemView(
                t.Id, t.Namespace, t.TeamId, t.ParkMachine, t.InputKind, t.InputQuestion))
            .ToListAsync(ct);

        return new InboxView(questions, awaitingReview, parked);
    }

    // ── Event log (§12) ───────────────────────────────────────────────────────

    /// <summary>
    /// Recent task transitions and Lead events interleaved, newest first, bounded.
    /// Lead takeovers surface as <c>lead</c> events (§4); machine reboots and
    /// evictions surface as the <c>LivenessLost</c> task transitions they drive
    /// (§10/§12). Each event carries only structure — kind, from/to state,
    /// identifiers, the store's own effect-name detail — never prose.
    /// </summary>
    public async Task<IReadOnlyList<DashboardEvent>> GetEventsAsync(int limit = 200, CancellationToken ct = default)
    {
        var rawTaskEvents = await db.TaskEvents.AsNoTracking()
            .OrderByDescending(e => e.Seq)
            .Take(limit)
            .Select(e => new
            {
                e.OccurredAt, e.Kind, e.FromState, e.ToState, e.Detail, e.TeamId, e.TaskId,
                // Derived-telemetry columns (#50) — carried to the JSON twin as
                // structured fields, unset off their own kind.
                e.InputKind,
                e.AuthOperation, e.AuthTarget, e.AuthErrorCode, e.AuthMissingScope,
                e.SubagentId, e.SubagentParentId,
            })
            .ToListAsync(ct);

        // Resolve namespaces for the events in view in a single follow-up read,
        // rather than a join EF might struggle to translate through the Take.
        var eventTaskIds = rawTaskEvents.Select(e => e.TaskId).Distinct().ToArray();
        var namespaceById = eventTaskIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Tasks.AsNoTracking()
                .Where(t => eventTaskIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Namespace })
                .ToDictionaryAsync(t => t.Id, t => t.Namespace, ct);

        var taskEvents = rawTaskEvents.Select(e => new DashboardEvent(
            e.OccurredAt,
            "task",
            e.Kind,
            e.FromState,
            e.ToState,
            e.Detail,
            e.TeamId,
            e.TaskId,
            namespaceById.GetValueOrDefault(e.TaskId),
            null,
            null,
            e.InputKind,
            e.AuthOperation,
            e.AuthTarget,
            e.AuthErrorCode,
            e.AuthMissingScope,
            e.SubagentId,
            e.SubagentParentId));

        var leadEvents = await db.LeadEvents.AsNoTracking()
            .OrderByDescending(e => e.Seq)
            .Take(limit)
            .Select(e => new DashboardEvent(
                e.OccurredAt,
                "lead",
                e.Kind.ToString(),
                null,
                null,
                null,
                e.TeamId,
                null,
                null,
                e.HumanId,
                e.PriorHumanId))
            .ToListAsync(ct);

        return taskEvents
            .Concat(leadEvents)
            .OrderByDescending(e => e.OccurredAt)
            .Take(limit)
            .ToList();
    }
}

// ── View records (the JSON twin's wire shape) ──────────────────────────────────

/// <summary>A live machine and the tasks it is running (§12 Machine Group view).
/// <see cref="BoundToHuman"/> is the human who claimed this machine as their own
/// (§8.3 human path) — so an operator can see which boxes are Lead-facing forward
/// targets and whose — with <see cref="BoundAt"/> when they did; both null when the
/// machine is bound by nobody.</summary>
public sealed record MachineView(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    DateTimeOffset? LastHeartbeat,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<MachineTaskView> RunningTasks,
    Guid? BoundToHuman = null,
    DateTimeOffset? BoundAt = null);

/// <summary>A task running on a machine, tagged with its owning Team (§12).</summary>
public sealed record MachineTaskView(Guid TaskId, Guid TeamId, string Namespace, TaskState State);

/// <summary>One Team's one-line overview for the sorted Team list (§12).</summary>
public sealed record TeamOverview(
    Guid TeamId,
    int TotalTasks,
    IReadOnlyDictionary<TaskState, int> CountsByState,
    int TotalParks,
    int ServiceCount,
    int OpenInputRequests,
    Guid? LeadHumanId,
    DateTimeOffset? LeadSince,
    DateTimeOffset? LastActivity,
    bool IsIdle,
    TeamBudgetView? Budget = null,
    TeamForwardUsageView? ForwardUsage = null);

/// <summary>One Team in full — the §4 reattachment surface as structured data (§12).</summary>
public sealed record TeamDetail(
    Guid TeamId,
    int TotalTasks,
    IReadOnlyDictionary<TaskState, int> CountsByState,
    IReadOnlyList<TeamTaskView> Tasks,
    IReadOnlyList<ServiceView> Services,
    IReadOnlyList<InputRequestView> OpenInputRequests,
    Guid? LeadHumanId,
    DateTimeOffset? LeadSince,
    DateTimeOffset? LastActivity,
    TeamBudgetView? Budget = null,
    TeamForwardUsageView? ForwardUsage = null);

/// <summary>One task in a Team, with its park count (§12 "parks per task"); for a
/// continuation task, the prior task it resumed (§6/§11 Y-continues-X lineage); and,
/// for a completed task, who adjudicated it (§9 check 4 provenance). The last three
/// carry this task's input exchange (§11): the typed <see cref="InputKind"/>, the
/// worker's <see cref="Question"/>, and the <see cref="Answer"/> given. Unlike the
/// agent-facing views this is a human surface, so it carries the prose itself (§12) —
/// a person cannot answer a question they cannot read.</summary>
public sealed record TeamTaskView(
    Guid TaskId,
    string Namespace,
    TaskState State,
    CompletionMode Mode,
    int Attempt,
    int Parks,
    string? ParkMachine,
    DateTimeOffset? BlockedAt,
    Guid? ContinuesTaskId,
    VerdictProvenance? CompletionProvenance,
    string? Report,
    InputRequestKind? InputKind,
    string? Question,
    string? Answer);

/// <summary>A live registered service on a Team (§8.2, §12).</summary>
public sealed record ServiceView(string Name, int Port, Guid TaskId, DateTimeOffset CreatedAt);

/// <summary>A blocked_on_input task — an open question (§12) — with the typed
/// <see cref="Kind"/> that says who can answer it and the worker's own
/// <see cref="Question"/>. Both are null for a request that carried neither, and for
/// rows blocked before the columns existed.</summary>
public sealed record InputRequestView(
    Guid TaskId,
    string Namespace,
    Guid TeamId,
    DateTimeOffset? BlockedAt,
    InputRequestKind? Kind,
    string? Question);

/// <summary>A verifying task in review mode, awaiting a human verdict (§7, §12).</summary>
public sealed record ReviewItemView(Guid TaskId, string Namespace, Guid TeamId);

/// <summary>A parked task awaiting an answer (§11, §12), carrying the question it is
/// still waiting on — a park is a question that outlived its lease, so the inbox
/// needs the same text here as in the open-questions list.</summary>
public sealed record ParkedItemView(
    Guid TaskId,
    string Namespace,
    Guid TeamId,
    string? ParkMachine,
    InputRequestKind? Kind,
    string? Question);

/// <summary>The Human inbox across all Teams (§12).</summary>
public sealed record InboxView(
    IReadOnlyList<InputRequestView> Questions,
    IReadOnlyList<ReviewItemView> AwaitingReview,
    IReadOnlyList<ParkedItemView> Parked);

/// <summary>
/// One dispatch of a task and the machine whose disk may hold its transcript (§12
/// serving). <see cref="Machine"/> is null for instance rows predating the column;
/// <see cref="Connected"/> is a snapshot of right now, since transcripts are readable only
/// while their machine is connected.
/// </summary>
public sealed record TranscriptLocationView(
    Guid InstanceId, string? Machine, DateTimeOffset DispatchedAt, bool Connected);

/// <summary>One interleaved event for the event log (§12). <see cref="Source"/> is
/// "task" or "lead"; the state and human fields are populated per source. The
/// derived-telemetry fields (#50) are populated only on their own task-event kind
/// and default to null everywhere else, so the JSON twin stays a clean structured
/// shape: <see cref="InputKind"/> on a <c>RequestInput</c> transition, the four
/// <c>Auth*</c> facts on an <c>auth-failed</c> row, the two <c>Subagent*</c> ids on
/// a <c>subagent-spawned</c> row.</summary>
public sealed record DashboardEvent(
    DateTimeOffset OccurredAt,
    string Source,
    string Kind,
    TaskState? FromState,
    TaskState? ToState,
    string? Detail,
    Guid TeamId,
    Guid? TaskId,
    string? Namespace,
    Guid? HumanId,
    Guid? PriorHumanId,
    InputRequestKind? InputKind = null,
    string? AuthOperation = null,
    string? AuthTarget = null,
    string? AuthErrorCode = null,
    string? AuthMissingScope = null,
    string? SubagentId = null,
    string? SubagentParentId = null);
