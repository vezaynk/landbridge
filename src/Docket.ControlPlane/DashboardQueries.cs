using Docket.Contracts;
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
/// <para><b>Every read here is instance-wide unless the caller scopes it.</b> The §12 views are
/// a human-operator surface and a human sees the whole instance; the same routes also serve a
/// reattaching Lead its own Team as structured data (§4), and a Lead's scope is that Team and
/// nothing else (§10 as-built: no cross-Team or machine views for agents). So the tenant filter
/// is a <c>teamScope</c> parameter on the multi-Team reads rather than an assumption — the
/// endpoint resolves the principal and says what it may see, and <c>null</c> is the deliberate
/// "a human asked" case, not a default that happens to be permissive.</para>
///
/// One §12 data point still has no source in the schema; rather than invent a
/// column, the query surfaces an honest absence and the renderer shows an empty
/// state (see the field comments): the subagent tree <em>nested under a machine</em>.
/// Permission requests are no longer among them — they have their own columns and a real
/// inbox section (§11 permission bridge, #108) — and neither are subagent spawns, which are
/// persisted as task event rows and surface in the event log; what is missing there is only
/// the per-machine nesting, not the data.
/// A Team's relay byte burn is no longer among them either: it IS measured, but
/// best-effort, reported asynchronously by a relay that may die holding an unsent tail
/// (§9.10) — which is why it travels with the timestamp of its last report. The
/// derived-telemetry events — auth
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
        TaskState.BlockedOnInput, TaskState.Parked, TaskState.Failed,
    ];

    // How many distinct auth failures the inbox carries (§12). Smaller than the event
    // log's window on purpose: this panel answers "what needs a person now", and a
    // hundredth-oldest failure on a live task is a history question the log answers.
    private const int AuthFailureLimit = 50;

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
    /// subagent tree is a documented empty state, but the reason is narrower than "no
    /// data": spawns <em>are</em> persisted as task event rows and do reach the §12 event
    /// log (#51). What this view has no source for is the per-machine <em>nesting</em> —
    /// there is nothing that resolves a subagent to the machine row it should hang under.
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
                isBound ? bound.BoundAt : null,
                // §10/§12: passed through from the last heartbeat, unexamined.
                registry.ServicesOn(id),
                registry.ProcessesOn(id)));
        }

        return machines.OrderBy(m => m.MachineId, StringComparer.Ordinal).ToList();
    }

    // ── Team view (§12) ───────────────────────────────────────────────────────

    /// <summary>
    /// Every Team as a one-line overview, sorted so idle Teams drift to the bottom
    /// (§12). A Team exists if it owns any task or holds a live Lead claim. Carries its
    /// reported relay bytes (§9.10, measured but best-effort).
    /// </summary>
    /// <param name="teamScope">The one Team this read may see, or null for the instance-wide
    /// human view (§12 — see <see cref="GetInboxAsync"/> for why the parameter exists). A
    /// scoped call returns at most one overview: the Lead's own Team.</param>
    public async Task<IReadOnlyList<TeamOverview>> GetTeamsAsync(
        Guid? teamScope = null, CancellationToken ct = default)
    {
        // One place says what a scoped read means, so none of the aggregates below can be
        // left unfiltered by accident — each is a whole-instance read otherwise.
        var tasks = db.Tasks.AsNoTracking();
        var events = db.TaskEvents.AsNoTracking();
        var services = db.RegisteredServices.AsNoTracking();
        var credentials = db.Credentials.AsNoTracking();
        var forwards = db.TeamForwardUsage.AsNoTracking();
        if (teamScope is { } only)
        {
            tasks = tasks.Where(t => t.TeamId == only);
            events = events.Where(e => e.TeamId == only);
            services = services.Where(s => s.TeamId == only);
            credentials = credentials.Where(c => c.TeamId == only);
            forwards = forwards.Where(u => u.TeamId == only);
        }

        var stateCounts = await tasks
            .GroupBy(t => new { t.TeamId, t.State })
            .Select(g => new { g.Key.TeamId, g.Key.State, Count = g.Count() })
            .ToListAsync(ct);

        // Permission stays blocked_on_input; a question stays working. Both
        // stamp BlockedAt and clear it when the wait ends.
        var openByTeam = await tasks
            .Where(t => t.BlockedAt != null
                && (t.State == TaskState.BlockedOnInput
                    || (t.State == TaskState.Working && t.InputKind != null
                        && t.InputKind != InputRequestKind.Permission)))
            .GroupBy(t => t.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);

        var parksByTeam = await events
            .Where(e => e.ToState == TaskState.Parked)
            .GroupBy(e => e.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);

        var lastActivity = await events
            .GroupBy(e => e.TeamId)
            .Select(g => new { TeamId = g.Key, Last = g.Max(e => e.OccurredAt) })
            .ToDictionaryAsync(x => x.TeamId, x => x.Last, ct);

        var serviceCounts = await services
            .GroupBy(s => s.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);

        var leads = await credentials
            .Where(c => c.Kind == CredentialKind.Lead && !c.Revoked && c.TeamId != null)
            .Select(c => new { TeamId = c.TeamId!.Value, c.HumanId, c.CreatedAt })
            .ToListAsync(ct);
        var leadByTeam = leads.ToDictionary(l => l.TeamId, l => (l.HumanId, l.CreatedAt));

        var forwardUsage = await forwards.ToDictionaryAsync(u => u.TeamId, ct);

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
            var open = openByTeam.GetValueOrDefault(teamId);
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
                // §8.1 (#81): the artifact pointer the worker handed over, so a human
                // adjudicating in review mode sees the same thing the Lead rules on.
                t.ResultReference,
                t.InputKind,
                t.InputQuestion,
                t.InputAnswer,
                // §6/§9 check 7 (#73): the infrastructure counter and the reason behind
                // its last increment, so a canceled task on this page can say whether the
                // requeue cap ended it and what kept failing.
                t.InfrastructureRequeues,
                t.InfrastructureRequeueLimit,
                t.LastRequeueReason,
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

        // §10/§12 measured view: what this Team's harnesses said they consumed, rolled up per
        // model across every task. Read straight off the denormalized team_id rather than
        // joining through tasks, which is what that index is for.
        var usageRows = await db.TaskUsage.AsNoTracking()
            .Where(u => u.TeamId == teamId)
            .ToListAsync(ct);
        var usage = TeamUsageView.From(usageRows);

        // §9.10. Measured — and best-effort, which is why the view carries the report
        // timestamp rather than presenting the total as current.
        var forwardUsage = TeamForwardUsageView.From(
            await db.TeamForwardUsage.AsNoTracking().FirstOrDefaultAsync(u => u.TeamId == teamId, ct));

        var taskRows = tasks
            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
            .Select(t => new TeamTaskView(
                t.Id, t.Namespace, t.State, t.CompletionMode, t.Attempt,
                parksByTask.GetValueOrDefault(t.Id),
                t.Parked ? t.ParkMachine : null,
                t.BlockedAt is not null
                    && (t.State == TaskState.BlockedOnInput
                        || (t.State == TaskState.Working && t.InputKind != InputRequestKind.Permission))
                    ? t.BlockedAt : null,
                t.ContinuesTaskId,
                t.State == TaskState.Completed ? t.CompletionProvenance : null,
                t.WorkerReport,
                t.ResultReference,
                t.InputKind,
                t.InputQuestion,
                t.InputAnswer,
                t.InfrastructureRequeues,
                t.InfrastructureRequeueLimit,
                t.LastRequeueReason))
            .ToList();

        var counts = tasks
            .GroupBy(t => t.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var inputRequests = taskRows
            .Where(t => t.BlockedAt is not null
                && t.InputKind != InputRequestKind.Permission
                && (t.State == TaskState.BlockedOnInput || t.State == TaskState.Working))
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
            forwardUsage,
            usage);
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
    /// tasks awaiting review (verifying + review mode, §7), parked tasks awaiting
    /// an answer (§11) with the same question, failed attempts the plane parked
    /// (infrastructure gave up — resume is a Lead note), the pending permission requests of §11's
    /// permission bridge, and the auth failures a person could still act on (§11, #50).
    /// This is where a person answers, so it is the one place the question's prose has to be
    /// legible verbatim — a §12 human surface, not a §10 agent read.
    ///
    /// <para>Permission requests are their own section rather than more question rows,
    /// because they are answered by a different act: a verdict on a named tool call, on a
    /// worker that is still running and waiting, where every other blocked task is answered
    /// with prose and redispatched. They are excluded from <see cref="InboxView.Questions"/>
    /// for that reason — one request, one row, in the section whose form can actually decide
    /// it. A human may answer any of them; escalation only removes the <em>Lead's</em>
    /// authority, so this read does not filter on it and instead reports it, which is what
    /// lets the page mark the ones a Lead has handed over.</para>
    /// </summary>
    /// <param name="teamScope">
    /// The one Team this read may see, or null for the instance-wide human view.
    ///
    /// <para>The §12 inbox is "everything waiting on a person <em>across every Team</em>", and
    /// for a human it still is. But the same routes serve a Lead its own Team as structured
    /// data (§4 reattachment, §12), and a Lead's scope is its Team and nothing else (§10
    /// as-built: no cross-Team views for agents) — so the caller passes the Team it is allowed
    /// to see and the filter happens in SQL, not in a renderer that could forget.</para>
    /// </param>
    public async Task<InboxView> GetInboxAsync(Guid? teamScope = null, CancellationToken ct = default)
    {
        var scopedTasks = db.Tasks.AsNoTracking();
        var scopedEvents = db.TaskEvents.AsNoTracking();
        if (teamScope is { } only)
        {
            scopedTasks = scopedTasks.Where(t => t.TeamId == only);
            scopedEvents = scopedEvents.Where(e => e.TeamId == only);
        }

        var questions = await scopedTasks
            .Where(t => t.InputKind != InputRequestKind.Permission
                        && t.InputKind != null
                        && t.BlockedAt != null
                        && (t.State == TaskState.BlockedOnInput
                            || t.State == TaskState.Working))
            .OrderBy(t => t.BlockedAt)
            .Select(t => new InputRequestView(
                t.Id, t.Namespace, t.TeamId, t.BlockedAt, t.InputKind, t.InputQuestion))
            .ToListAsync(ct);

        // Oldest first, like the questions above: age is what matters on a queue whose items
        // each have a worker blocked behind them and a wait TTL running down.
        var permissionRequests = await scopedTasks
            .Where(t => t.State == TaskState.BlockedOnInput
                        && t.InputKind == InputRequestKind.Permission)
            .OrderBy(t => t.BlockedAt)
            .Select(t => new PermissionRequestView(
                t.Id, t.Namespace, t.TeamId, t.State, t.BlockedAt, t.PermissionTool,
                t.InputQuestion, t.PermissionVerdict, t.InputAnswer,
                t.PermissionEscalatedAt, t.PermissionEscalationReason))
            .ToListAsync(ct);

        var awaitingReview = await scopedTasks
            .Where(t => t.State == TaskState.Verifying && t.CompletionMode == CompletionMode.Review)
            .OrderBy(t => t.Namespace)
            .Select(t => new ReviewItemView(t.Id, t.Namespace, t.TeamId))
            .ToListAsync(ct);

        var parked = await scopedTasks
            .Where(t => t.State == TaskState.Parked)
            .OrderBy(t => t.Namespace)
            .Select(t => new ParkedItemView(
                t.Id, t.Namespace, t.TeamId, t.ParkMachine, t.InputKind, t.InputQuestion))
            .ToListAsync(ct);

        var failed = await scopedTasks
            .Where(t => t.State == TaskState.Failed)
            .OrderBy(t => t.Namespace)
            .Select(t => new FailedItemView(
                t.Id, t.Namespace, t.TeamId, t.LastRequeueReason, t.InfrastructureRequeues))
            .ToListAsync(ct);

        // §11/§12 (#50): the auth failures a person could still act on. Nothing marks an
        // individual failure resolved — the §11 remediation menu is not built — so a live
        // task stands in for "unresolved": once a task is terminal, no scope a person
        // grants changes its outcome, and the event log keeps every failure either way.
        //
        // Driven from the live tasks rather than from the event rows. task_events is the
        // plane's busiest table and is indexed by task id, so asking it about a bounded set
        // of live ids stays an index lookup, where filtering its whole history by kind would
        // scan it — on a page that refreshes every 5s. It also means the namespace and state
        // this view labels each failure with come from the same read that chose it.
        var liveTasks = await scopedTasks
            .Where(t => ActiveStates.Contains(t.State))
            .Select(t => new { t.Id, t.Namespace, t.State })
            .ToDictionaryAsync(t => t.Id, t => (t.Namespace, t.State), ct);
        var liveTaskIds = liveTasks.Keys.ToArray();

        // Collapsed by the facts that identify the same problem: a retrying worker writes one
        // row per attempt, and what that repetition is worth to a person is the count and the
        // newest of them, not a wall of identical rows.
        var failureGroups = await scopedEvents
            .Where(e => e.Kind == TaskEventRow.AuthFailedKind && liveTaskIds.Contains(e.TaskId))
            .GroupBy(e => new
            {
                e.TaskId, e.TeamId, e.AuthOperation, e.AuthTarget, e.AuthErrorCode, e.AuthMissingScope,
            })
            .Select(g => new
            {
                g.Key,
                Occurrences = g.Count(),
                LastFailedAt = g.Max(e => e.OccurredAt),
            })
            .OrderByDescending(g => g.LastFailedAt)
            .Take(AuthFailureLimit)
            .ToListAsync(ct);

        var authFailures = failureGroups
            .Select(g => new AuthFailureItemView(
                g.Key.TaskId,
                liveTasks[g.Key.TaskId].Namespace,
                g.Key.TeamId,
                liveTasks[g.Key.TaskId].State,
                g.Key.AuthOperation,
                g.Key.AuthTarget,
                g.Key.AuthErrorCode,
                g.Key.AuthMissingScope,
                g.LastFailedAt,
                g.Occurrences))
            .ToList();

        return new InboxView(questions, awaitingReview, parked, failed, authFailures, permissionRequests);
    }

    // ── Event log (§12) ───────────────────────────────────────────────────────

    /// <summary>
    /// Recent task transitions and Lead events interleaved, newest first, bounded.
    /// Lead takeovers surface as <c>lead</c> events (§4); machine reboots and
    /// evictions surface as the <c>LivenessLost</c> task transitions they drive
    /// (§10/§12). Each event carries only structure — kind, from/to state,
    /// identifiers, the store's own effect-name detail — never prose.
    /// </summary>
    /// <param name="teamScope">See <see cref="GetInboxAsync"/> — a Lead's own Team, or null for
    /// the instance-wide human view. Scoped on both sources, so a Lead's log carries neither
    /// another Team's transitions nor another Team's takeovers.</param>
    public async Task<IReadOnlyList<DashboardEvent>> GetEventsAsync(
        int limit = 200, Guid? teamScope = null, CancellationToken ct = default)
    {
        var scopedTaskEvents = db.TaskEvents.AsNoTracking();
        var scopedLeadEvents = db.LeadEvents.AsNoTracking();
        if (teamScope is { } only)
        {
            scopedTaskEvents = scopedTaskEvents.Where(e => e.TeamId == only);
            scopedLeadEvents = scopedLeadEvents.Where(e => e.TeamId == only);
        }

        var rawTaskEvents = await scopedTaskEvents
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
                // §6/§9 check 7 (#73): why a requeue happened. Without it a requeue loop
                // is N identical rows in this log, which is the state issue #73 describes.
                e.LivenessReason,
                // §11/§12 permission audit: which way a permission decision went and whether
                // a Lead or a person had the authority to send it there.
                e.PermissionVerdict,
                e.PermissionAnswerer,
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
            e.SubagentParentId,
            e.LivenessReason,
            e.PermissionVerdict,
            e.PermissionAnswerer));

        var leadEvents = await scopedLeadEvents
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

    // ── Profile-check dummy tasks (operator conformance stand-in) ─────────────

    /// <summary>
    /// Tasks belonging to a conformance run. A run is a Team created solely to
    /// hold those dummy tasks, so this is a Team-scoped read with no extra table.
    /// </summary>
    public async Task<IReadOnlyList<ConformanceTaskRow>> GetConformanceTasksAsync(
        Guid runId, CancellationToken ct = default)
    {
        return await db.Tasks.AsNoTracking()
            .Where(t => t.TeamId == runId)
            .OrderBy(t => t.Id)
            .Select(t => new ConformanceTaskRow(
                t.Id, t.State, t.Attempt, t.Workspace, t.Profile,
                t.ResultReference, t.LastRequeueReason))
            .ToListAsync(ct);
    }

    /// <summary>The profile the run's tasks were aimed at, or null when the run is empty.</summary>
    public async Task<string?> GetConformanceProfileAsync(Guid runId, CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.TeamId == runId)
            .Select(t => t.Profile)
            .FirstOrDefaultAsync(ct);
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
    DateTimeOffset? BoundAt = null,
    IReadOnlyList<ServiceStatus>? Services = null,
    IReadOnlyList<ProcessStatus>? Processes = null);

/// <summary>A task running on a machine, tagged with its owning Team (§12).</summary>
public sealed record MachineTaskView(Guid TaskId, Guid TeamId, string Namespace, TaskState State);

/// <summary>One dummy task in an operator profile-check run.</summary>
public sealed record ConformanceTaskRow(
    Guid Id, TaskState State, int Attempt, string? Workspace, string? Profile,
    string? ResultReference, LivenessLossReason? LastRequeueReason);

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
    TeamForwardUsageView? ForwardUsage = null,
    TeamUsageView? Usage = null);

/// <summary>One task in a Team, with its park count (§12 "parks per task"); for a
/// continuation task, the prior task it resumed (§6/§11 Y-continues-X lineage); and,
/// for a completed task, who adjudicated it (§9 check 4 provenance).
/// <see cref="ResultReference"/> is the §8.1 artifact pointer its worker handed over on
/// working → verifying — where the finished work is said to live — shown here because a
/// human adjudicating in <c>review</c> mode, or auditing a completed task afterwards,
/// needs the same pointer the Lead rules on (§7, #81); null until the task reaches
/// verifying. Then this task's input exchange (§11): the typed <see cref="InputKind"/>,
/// the worker's <see cref="Question"/>, and the <see cref="Answer"/> given. Unlike the
/// agent-facing views this is a human surface, so it carries the prose itself (§12) —
/// a person cannot answer a question they cannot read.
/// <para>The last three are §6's infrastructure counter, the cap it is judged against
/// (§9 check 7), and the reason behind the last requeue (#73): a canceled task whose
/// count reached its cap was abandoned by the plane, not called off by a person, and
/// this is where that difference becomes visible.</para></summary>
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
    string? ResultReference,
    InputRequestKind? InputKind,
    string? Question,
    string? Answer,
    int InfrastructureRequeues = 0,
    int InfrastructureRequeueLimit = TaskRecord.DefaultInfrastructureRequeueLimit,
    LivenessLossReason? LastRequeueReason = null);

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

/// <summary>An attempt the plane parked because infrastructure gave up. Resume
/// is a Lead note — not an automatic requeue.</summary>
public sealed record FailedItemView(
    Guid TaskId,
    string Namespace,
    Guid TeamId,
    LivenessLossReason? Reason,
    int InfrastructureRequeues);

/// <summary>
/// An auth failure a person could still act on (§11, §12): the structured facts the
/// runner reported (#50), collapsed across the repeat attempts that reported the same
/// problem. <see cref="Occurrences"/> is how many rows collapsed into this one and
/// <see cref="LastFailedAt"/> the newest of them, so a worker wedged in a retry loop
/// reads as one entry getting worse rather than a wall of identical ones.
/// <see cref="State"/> is the task's state right now — always non-terminal, since a
/// finished task is not waiting on a credential. Every fact is nullable because the row
/// stores what the runner sent, and a runner may name no scope (or, on rows written
/// before the columns existed, nothing at all).
/// </summary>
public sealed record AuthFailureItemView(
    Guid TaskId,
    string Namespace,
    Guid TeamId,
    TaskState State,
    string? Operation,
    string? Target,
    string? ErrorCode,
    string? MissingScope,
    DateTimeOffset LastFailedAt,
    int Occurrences);

/// <summary>The Human inbox across all Teams (§12).</summary>
/// <param name="PermissionRequests">Pending permission requests (§11 permission bridge),
/// oldest first — the section that replaced the inbox's last structural empty state. Each
/// one has a worker blocked behind it right now, so unlike every other row here these are
/// answered <em>while</em> someone is waiting. Disjoint from
/// <paramref name="Questions"/>.</param>
public sealed record InboxView(
    IReadOnlyList<InputRequestView> Questions,
    IReadOnlyList<ReviewItemView> AwaitingReview,
    IReadOnlyList<ParkedItemView> Parked,
    IReadOnlyList<FailedItemView> Failed,
    IReadOnlyList<AuthFailureItemView> AuthFailures,
    IReadOnlyList<PermissionRequestView> PermissionRequests);

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
/// a <c>subagent-spawned</c> row, and <see cref="LivenessReason"/> on a
/// <c>LivenessLost</c> requeue — whose <see cref="ToState"/> also says whether the
/// requeue redispatched the task (<c>Submitted</c>) or reached its cap and abandoned it
/// (<c>Canceled</c>, §9 check 7).</summary>
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
    string? SubagentParentId = null,
    LivenessLossReason? LivenessReason = null,
    PermissionVerdict? PermissionVerdict = null,
    PermissionAnswerer? PermissionAnswerer = null);

/// <summary>
/// One (task, model) usage report as a reader sees it (§10 telemetry ingest, §12 measured
/// view). Every number here is <b>the harness's own claim</b>, relayed verbatim — the plane
/// sums these rows and does nothing else to them.
/// </summary>
/// <param name="Model">
/// The model the harness named, or null when it named none. Null is a real state rather than a
/// missing value, and only the harness may fill it — a plane-asserted model in a section labelled
/// "reported by the harness" would be the mislabelling §2 principle 2 forbids.
/// </param>
/// <param name="ReasoningOutputTokens">
/// A portion OF <paramref name="OutputTokens"/> where the harness breaks one out, null
/// otherwise. Never added to a total — see <see cref="TotalTokens"/>.
/// </param>
public sealed record TaskUsageView(
    Guid TaskId,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long? ReasoningOutputTokens,
    decimal? CostUsd,
    DateTimeOffset ReportedAt)
{
    /// <summary>
    /// The four buckets summed. Sound to add because they are disjoint by the time they are
    /// stored — docketd normalizes a harness that counts cache hits inside its input total
    /// before reporting. <see cref="ReasoningOutputTokens"/> is deliberately absent: it is part
    /// of <see cref="OutputTokens"/> already, and adding it would count those tokens twice.
    /// </summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>Where the dollar figure came from, or that there is none (§12 renders the
    /// three differently — a derived number must never look like a reported one).</summary>
    public UsageCostProvenance CostProvenance =>
        CostUsd is null ? UsageCostProvenance.None : UsageCostProvenance.Reported;

    /// <summary>The one row→view mapping, so the empty-string-is-the-unnamed-model convention
    /// (see <c>DocketDbContext</c>) is undone in exactly one place.</summary>
    public static TaskUsageView From(TaskUsageRow row) => new(
        row.TaskId,
        string.IsNullOrEmpty(row.Model) ? null : row.Model,
        row.InputTokens,
        row.OutputTokens,
        row.CacheReadTokens,
        row.CacheWriteTokens,
        row.ReasoningOutputTokens,
        row.CostUsd,
        row.ReportedAt);
}

/// <summary>
/// A Team's measured usage (§12), rolled up per model across its tasks — plus
/// <see cref="Measured"/>, which distinguishes a Team no harness has reported for from one
/// measured at zero. The same distinction §9.10 draws for relay bytes, and for the same reason:
/// an absence of measurement is not a measurement of nothing.
/// </summary>
public sealed record TeamUsageView(
    IReadOnlyList<TaskUsageView> ByModel,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal? ReportedCostUsd,
    DateTimeOffset? ReportedAt)
{
    /// <summary>Whether any harness has reported for this Team at all.</summary>
    public bool Measured => ByModel.Count > 0;

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>
    /// Whether the Team's cost total is complete. False when at least one model reported tokens
    /// but no cost — the sum is then a floor rather than a total, and §12 must not present a
    /// partial figure as if it covered everything. This is the Codex case: its tokens are real
    /// and its dollars do not exist.
    /// </summary>
    public bool CostIsPartial => ByModel.Any(m => m.CostUsd is null);

    /// <summary>
    /// Rolls rows up per model, newest report first. Summing costs across models is sound
    /// because each is the harness's own figure for its own tokens; a model that reported none
    /// contributes nothing and flips <see cref="CostIsPartial"/> instead of being guessed at.
    /// </summary>
    public static TeamUsageView From(IReadOnlyList<TaskUsageRow> rows)
    {
        var byModel = rows
            .Select(TaskUsageView.From)
            .OrderByDescending(u => u.TotalTokens)
            .ToList();
        var costs = byModel.Where(m => m.CostUsd is not null).Select(m => m.CostUsd!.Value).ToList();
        return new TeamUsageView(
            byModel,
            byModel.Sum(m => m.InputTokens),
            byModel.Sum(m => m.OutputTokens),
            byModel.Sum(m => m.CacheReadTokens),
            byModel.Sum(m => m.CacheWriteTokens),
            costs.Count > 0 ? costs.Sum() : null,
            byModel.Count > 0 ? byModel.Max(m => m.ReportedAt) : null);
    }
}
