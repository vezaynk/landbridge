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
/// state (see the field comments): budget/byte burn, permission requests, and the
/// subagent tree nested under a machine. The derived-telemetry events — auth
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
    /// Live machines with their running tasks (each tagged with its owning Team).
    /// Machines come from the connection registry's full enumeration
    /// (<see cref="RunnerConnectionRegistry.MachineIds"/>), so a machine that is
    /// connected, under back-pressure, and holding no dispatched task still appears
    /// — exactly the operator signal this view exists to surface (§12). The subagent
    /// tree is a documented empty state: subagent events reach the plane only as
    /// liveness pings (§10), nothing is persisted, so there is nothing to nest.
    /// </summary>
    public async Task<IReadOnlyList<MachineView>> GetMachinesAsync(CancellationToken ct = default)
    {
        var ids = registry.MachineIds();

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
            machines.Add(new MachineView(
                id,
                snapshot.Ready,
                snapshot.UnderBackPressure,
                registry.LastHeartbeatFor(id),
                snapshot.DeclaredProfiles.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                tasks));
        }

        return machines.OrderBy(m => m.MachineId, StringComparer.Ordinal).ToList();
    }

    // ── Team view (§12) ───────────────────────────────────────────────────────

    /// <summary>
    /// Every Team as a one-line overview, sorted so idle Teams drift to the bottom
    /// (§12). A Team exists if it owns any task or holds a live Lead claim. Budget
    /// and byte burn are labelled "not tracked" here because nothing in the schema
    /// records them yet (§12 lists them, so the slot is kept, not the number).
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

        var teamIds = new HashSet<Guid>(stateCounts.Select(s => s.TeamId));
        foreach (var l in leads)
            teamIds.Add(l.TeamId);

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
                isIdle));
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
    /// services, open input requests with their blocked age, the attached Lead,
    /// and last activity. Never prose (§10): only counts, states, and identifiers.
    /// Null when the Team owns no task and holds no Lead.
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

        var taskRows = tasks
            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
            .Select(t => new TeamTaskView(
                t.Id, t.Namespace, t.State, t.CompletionMode, t.Attempt,
                parksByTask.GetValueOrDefault(t.Id),
                t.Parked ? t.ParkMachine : null,
                t.State == TaskState.BlockedOnInput ? t.BlockedAt : null,
                t.ContinuesTaskId,
                t.State == TaskState.Completed ? t.CompletionProvenance : null,
                t.WorkerReport))
            .ToList();

        var counts = tasks
            .GroupBy(t => t.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var inputRequests = taskRows
            .Where(t => t.State == TaskState.BlockedOnInput)
            .Select(t => new InputRequestView(t.TaskId, t.Namespace, teamId, t.BlockedAt))
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
            lastActivity);
    }

    // ── Human inbox (§12) ─────────────────────────────────────────────────────

    /// <summary>
    /// Everything waiting on a person across every Team (§12): open questions
    /// (blocked_on_input), tasks awaiting review (verifying + review mode, §7), and
    /// parked tasks awaiting an answer (§11). Three §12 rows are structural empty
    /// states, not omissions: auth failures (the runner reports them but the sink
    /// only logs them — §11, no event row is written), permission requests (not
    /// built), and — implicitly — the typed input-request kind, which the state
    /// machine requires but the store does not persist, so a question shows its age
    /// but not its kind.
    /// </summary>
    public async Task<InboxView> GetInboxAsync(CancellationToken ct = default)
    {
        var questions = await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.BlockedOnInput)
            .OrderBy(t => t.BlockedAt)
            .Select(t => new InputRequestView(t.Id, t.Namespace, t.TeamId, t.BlockedAt))
            .ToListAsync(ct);

        var awaitingReview = await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.Verifying && t.CompletionMode == CompletionMode.Review)
            .OrderBy(t => t.Namespace)
            .Select(t => new ReviewItemView(t.Id, t.Namespace, t.TeamId))
            .ToListAsync(ct);

        var parked = await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.Parked)
            .OrderBy(t => t.Namespace)
            .Select(t => new ParkedItemView(t.Id, t.Namespace, t.TeamId, t.ParkMachine))
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

/// <summary>A live machine and the tasks it is running (§12 Machine Group view).</summary>
public sealed record MachineView(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    DateTimeOffset? LastHeartbeat,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<MachineTaskView> RunningTasks);

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
    bool IsIdle);

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
    DateTimeOffset? LastActivity);

/// <summary>One task in a Team, with its park count (§12 "parks per task"); for a
/// continuation task, the prior task it resumed (§6/§11 Y-continues-X lineage); and,
/// for a completed task, who adjudicated it (§9 check 4 provenance).</summary>
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
    string? Report);

/// <summary>A live registered service on a Team (§8.2, §12).</summary>
public sealed record ServiceView(string Name, int Port, Guid TaskId, DateTimeOffset CreatedAt);

/// <summary>A blocked_on_input task — an open question (§12). The typed request kind
/// is not persisted by the store, so only the blocked age is available.</summary>
public sealed record InputRequestView(Guid TaskId, string Namespace, Guid TeamId, DateTimeOffset? BlockedAt);

/// <summary>A verifying task in review mode, awaiting a human verdict (§7, §12).</summary>
public sealed record ReviewItemView(Guid TaskId, string Namespace, Guid TeamId);

/// <summary>A parked task awaiting an answer (§11, §12).</summary>
public sealed record ParkedItemView(Guid TaskId, string Namespace, Guid TeamId, string? ParkMachine);

/// <summary>The Human inbox across all Teams (§12).</summary>
public sealed record InboxView(
    IReadOnlyList<InputRequestView> Questions,
    IReadOnlyList<ReviewItemView> AwaitingReview,
    IReadOnlyList<ParkedItemView> Parked);

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
