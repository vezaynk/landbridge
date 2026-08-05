using System.Diagnostics;
using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane;

/// <summary>
/// The only write path to task state (spec §15: the control plane is the only
/// path to the state machine). Every mutation runs a transition through
/// <see cref="TaskStateMachine"/>, then persists the resulting record, its
/// effects, and an event row in one transaction — so a transition and its
/// consequences (token mint/revoke, service clearing, park record) are
/// atomic, and a NOTIFY fires only if the write commits.
/// </summary>
public sealed class TaskStore(DocketDbContext db, TimeProvider clock, TeamBudgetService? budgets = null)
{
    public async Task<StoreResult> CreateAsync(CreateTask command, CancellationToken ct = default)
    {
        var id = TaskId.New();
        var ns = $"team-{command.Team}/task-{id}";
        var result = TaskStateMachine.Create(command, id, ns);
        if (result is TransitionResult.Rejected r)
            return new StoreResult.Rejected(r.Rule, r.Reason);

        var task = ((TransitionResult.Transitioned)result).Task;
        db.Tasks.Add(new TaskRow
        {
            Id = id.Value,
            TeamId = task.Team.Value,
            Namespace = task.Namespace,
            CompletionMode = task.CompletionMode,
            State = task.State,
            Profile = task.Profile,
            VerificationRetryLimit = task.VerificationRetryLimit,
            // Opaque content the engine never interpreted (§7): persisted verbatim
            // straight off the command, alongside the criteria.
            CompletionCriteria = command.CompletionCriteria,
            Description = command.Description,
            Workspace = command.Workspace,
            // Opaque transport metadata: the ambient W3C traceparent at creation,
            // captured so dispatch can continue the Lead's trace over the wire.
            // Read straight off the ambient Activity here, never through a command
            // field — the engine stays content-free (§7). Null when nothing samples.
            TraceContext = Activity.Current?.Id,
            // §6/§11 continuation targeting: seed the park-record-style affinity from
            // the resolved facts the command carries (all opaque; the engine validated
            // team + profile above but never landed them on the record). The inherited
            // session ref goes straight onto the same HarnessSessionRef column the §11
            // resume path already reads at dispatch, so the FIRST dispatch to the
            // preferred machine hands the runner --resume with no new machinery. Null
            // Continues leaves every field default, i.e. an ordinary profile-targeted
            // task.
            ContinuesTaskId = command.Continues?.ContinuedTask.Value,
            PreferredMachine = command.Continues?.PreferredMachine,
            OnMachineGone = command.Continues?.OnMachineGone,
            HarnessSessionRef = command.Continues?.InheritedSessionRef,
        });
        AppendEvent(id.Value, task.Team.Value, "created", from: null, to: task.State, detail: null);
        await CommitAsync(id.Value, ct);
        return new StoreResult.Applied(task, []);
    }

    /// <summary>Apply a command to an existing task, addressed by id.</summary>
    public async Task<StoreResult> ApplyAsync(TaskId id, TaskCommand command, CancellationToken ct = default)
    {
        var row = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        return await RunTransition(row, command, ct);
    }

    /// <summary>
    /// A Lead's answer to a task awaiting input, routed to whichever §6
    /// transition the task's current state needs — so a Lead answering never has
    /// to know whether the wait-TTL sweeper (§11) parked the task first. One
    /// call, correct outcome either way, and both outcomes requeue for redispatch
    /// rather than resuming in place (§11: "waiting is always the park shape"):
    ///
    ///   blocked_on_input → submitted (<see cref="AnswerInput"/>): the task is
    ///     still waiting; the answer requeues it with a park record built from the
    ///     held-lease machine (<paramref name="leaseMachine"/>) and the row's
    ///     stamped harness session ref, so redispatch resumes the transcript. When
    ///     the machine is gone (<paramref name="leaseMachine"/> is null) the task
    ///     still requeues and redispatch cold-starts elsewhere (§6, §11). This does
    ///     not touch the infrastructure counter (§6, two counters).
    ///   parked → submitted           (<see cref="WakeParked"/>): the sweeper
    ///     already parked it; the answer landing wakes it so redispatch requeues
    ///     it with the park record's machine/directory affinity (§6, §11 — "the
    ///     awaited answer … landed"). parked → submitted is the control plane's
    ///     transition (§6), so the store applies it as the plane once it has
    ///     checked the Lead's Team scope — the same store-level guard
    ///     <see cref="RegisterServiceAsync"/> applies, and exactly the Team check
    ///     the engine enforces on the <see cref="AnswerInput"/> path.
    ///
    /// Any other state falls through to <see cref="AnswerInput"/> and surfaces the
    /// engine's wrong-state rejection unchanged — the behaviour before this method
    /// existed. The answer-vs-sweep race is resolved by the store's optimistic
    /// concurrency exactly as everywhere else: whichever transition commits first
    /// wins, and the loser sees a rejected wrong-state command (the sweeper's
    /// <see cref="WaitTtlExpired"/> on a now-submitted task) or a
    /// <see cref="StoreResult.Conflict"/> — never a lost answer or a double
    /// transition.
    ///
    /// <para><paramref name="answer"/> is the answer's <em>content</em> (§10/§11), and it
    /// rides both branches for the same reason the routing exists: the caller does not
    /// know which one it is taking, so the text must land either way. Both commands cap
    /// it at the engine, and the row keeps it for the redispatched worker's opening
    /// <c>get_task</c>. Null answers nothing in words — the transition still unblocks
    /// the task, which is what an <c>endpoint_wait</c> wake or a bare unblock wants.</para>
    /// </summary>
    public async Task<StoreResult> AnswerOrWakeAsync(
        LeadClaim lead, TaskId id, string? leaseMachine, string? answer = null,
        CancellationToken ct = default)
    {
        var row = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        if (row.State == TaskState.Parked)
        {
            if (row.TeamId != lead.Team.Value)
                return new StoreResult.Rejected(Rule.ActorLacksAuthority,
                    "input requests are answered by the Lead or a human");
            return await RunTransition(row, new WakeParked(answer), ct);
        }

        // §11: build the redispatch park record from what the plane holds — the
        // machine still holding the lease (preferred for transcript resume) and the
        // session ref stamped from this work session's SessionStartedEvent. Same
        // shape the wait-TTL sweeper writes; the working directory originates
        // runner-side and is null here. Null when the machine is gone, so redispatch
        // cold-starts elsewhere from the workspace (§11). Ignored by the engine on
        // any non-blocked state (the fall-through wrong-state rejection).
        var park = leaseMachine is { } machine
            ? new ParkRecord(machine, Directory: null, HarnessSessionRef: row.HarnessSessionRef, row.Attempt)
            : null;
        return await RunTransition(row, new AnswerInput(lead, park, answer), ct);
    }

    /// <summary>
    /// The dispatch transaction and the one raw-SQL path (§3.1). Selects a
    /// single eligible submitted task with FOR UPDATE SKIP LOCKED so
    /// concurrent dispatchers never pick the same row (§9 check 5), then runs
    /// the Dispatch transition — which re-checks readiness, back-pressure, and
    /// profile as defense in depth.
    ///
    /// <para>Continuation targeting (§6/§11) adds a preferred-machine clause to the
    /// SQL half of check 5. A row with no <c>preferred_machine</c> (an ordinary
    /// profile-targeted task, or a parked task whose affinity lives in the park
    /// record) is claimable by any profile-matching machine, unchanged. A
    /// continuation row is claimable only by its preferred machine — so the first
    /// dispatch prefers it and resumes the transcript there — <em>unless</em> that
    /// machine is gone (absent from <paramref name="connectedMachines"/>) and its
    /// policy is <see cref="MachineGonePolicy.Degrade"/>, in which case any
    /// profile-matching machine may claim it and cold-start. <see cref="MachineGonePolicy.Pin"/>
    /// with a gone machine matches no machine — the task waits in submitted until the
    /// machine returns. <paramref name="connectedMachines"/> defaults to just the
    /// asking machine when a caller supplies none (the pure store tests), which is
    /// the honest "any other preferred machine is unknown to me" reading.</para>
    /// </summary>
    public async Task<StoreResult> DispatchNextAsync(
        MachineSnapshot machine, WorkerInstanceId newInstance, CancellationToken ct = default,
        IReadOnlyCollection<string>? connectedMachines = null)
    {
        if (!machine.Ready || machine.UnderBackPressure)
            return new StoreResult.NotFound($"machine {machine.MachineId} is not accepting dispatch");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock a single eligible row's id with SKIP LOCKED, then load the
        // entity by id inside the same transaction. Selecting only the id
        // (not SELECT *) keeps the xmin concurrency token out of the raw query;
        // the row lock is held to end of transaction, so concurrent dispatchers
        // skip it. Profile match is the SQL half of check 5: a task with no
        // profile runs anywhere; a task with one runs only where the machine
        // declares it. The preferred-machine clause is the §6/§11 continuation half
        // (see the method summary): NOT (preferred_machine = ANY(connected)) reads as
        // "preferred machine gone" and is true for an empty connected set too.
        var profiles = machine.DeclaredProfiles.ToArray();
        var connected = (connectedMachines is { Count: > 0 } c ? c : [machine.MachineId]).ToArray();
        var claimedId = await db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT id AS "Value" FROM tasks
                 WHERE state = 'Submitted'
                   AND (profile IS NULL OR profile = ANY({profiles}))
                   AND (
                         preferred_machine IS NULL
                      OR preferred_machine = {machine.MachineId}
                      OR (on_machine_gone = 'Degrade' AND NOT (preferred_machine = ANY({connected})))
                   )
                 ORDER BY id
                 FOR UPDATE SKIP LOCKED
                 LIMIT 1
                 """)
            .FirstOrDefaultAsync(ct);

        if (claimedId == Guid.Empty)
            return new StoreResult.NotFound("no eligible submitted task");

        var claimed = await db.Tasks.FirstAsync(t => t.Id == claimedId, ct);

        // §6/§11 degrade cold-start: the SQL invariant means a claimed continuation
        // row whose preferred machine is not the asking machine can only be a Degrade
        // task whose machine went away — so this claim abandons the (now unreachable,
        // machine-local) transcript. Detected before the transition; enacted after it
        // commits-in-transaction below.
        var degradeColdStart =
            claimed.PreferredMachine is { } preferred && preferred != machine.MachineId;
        var gonePreferred = claimed.PreferredMachine;

        var result = await RunTransition(claimed, new Dispatch(machine, newInstance), ct, tx);
        if (result is StoreResult.Applied applied)
        {
            // §9.9: pay for this dispatch inside the same transaction as the transition, so a
            // dispatch that happens is always committed against the Team's ceiling and one
            // that is refused commits nothing. Per DISPATCH, not per task — each attempt is a
            // fresh process that can burn the whole cap, so a requeued task commits twice.
            // An exhausted ceiling rolls the whole claim back: the task stays submitted and
            // is picked up again only once a human raises the ceiling.
            decimal? budgetCap = null;
            if (budgets is not null)
            {
                var commit = await budgets.TryCommitDispatchAsync(new TeamId(claimed.TeamId), ct);
                if (commit is BudgetCommit.Exhausted exhausted)
                {
                    await tx.RollbackAsync(ct);
                    return new StoreResult.Rejected(
                        Rule.TeamBudgetCeiling,
                        $"Team budget ceiling reached (committed ${exhausted.CommittedUsd:N2} of " +
                        $"${exhausted.CeilingUsd:N2}); a human must raise it before more work is dispatched");
                }
                budgetCap = ((BudgetCommit.Allowed)commit).CapUsd;
            }

            if (degradeColdStart)
            {
                // Abandon the transcript: suppress the resume ref for this dispatch,
                // and clear the continuation affinity so a later requeue treats this
                // as an ordinary task on the machine it actually cold-started on (the
                // new session re-stamps HarnessSessionRef on its own SessionStartedEvent).
                // Record the memory-lost event in the same transaction so the Lead can
                // see the conversational memory was dropped (§11). No extra NOTIFY: the
                // dispatch transition's own pg_notify already fired in this tx.
                claimed.HarnessSessionRef = null;
                claimed.PreferredMachine = null;
                claimed.OnMachineGone = null;
                db.TaskEvents.Add(new TaskEventRow
                {
                    TaskId = claimed.Id,
                    TeamId = claimed.TeamId,
                    Kind = TaskEventRow.ContinuationMemoryLostKind,
                    Detail = $"preferred machine '{gonePreferred}' gone; cold-started on " +
                             $"'{machine.MachineId}' — conversational memory lost",
                    OccurredAt = clock.GetUtcNow(),
                });
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
            // Surface the row's opaque transport metadata so DispatchService can act
            // on it (neither reaches the engine): the trace context parents the
            // dispatch span on the Lead's create_task trace, and the harness session
            // ref — present when the task was worked before and parked/requeued, or
            // seeded from a continuation's inherited session — rides the
            // DispatchCommand back so the runner can resume the transcript (§11). Null
            // on a first-ever dispatch, and deliberately suppressed on a degrade
            // cold-start so the runner starts fresh rather than --resume a session
            // that lives on the gone machine.
            return applied with
            {
                TraceContext = claimed.TraceContext,
                HarnessSessionRef = degradeColdStart ? null : claimed.HarnessSessionRef,
                // §9.9: the committed cap rides back so DispatchService can hand it to the
                // harness as its own hard limit.
                BudgetCapUsd = budgetCap,
            };
        }
        return result;
    }

    /// <summary>
    /// The seed facts a <c>create_task(continues:)</c> reads off the continued task's
    /// row (§6/§11): its owning Team (the same-Team gate), its profile (the default
    /// when the caller omits one), the opaque harness session ref to resume, and the
    /// park machine (a fallback preferred machine when the task is parked and no
    /// longer tracked in the live registry). A pure read; null when the continued
    /// task does not exist. Team-scoping is deliberately not applied here so the tool
    /// can surface a precise cross-Team rejection rather than an indistinguishable
    /// not-found (the engine enforces the same-Team gate on the resulting command).
    /// </summary>
    public async Task<ContinuationSource?> ReadContinuationSourceAsync(TaskId continued, CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.Id == continued.Value)
            .Select(t => new ContinuationSource(
                new TeamId(t.TeamId), t.Profile, t.HarnessSessionRef, t.ParkMachine))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Records a live endpoint for a working task (§8.2). Only the incumbent
    /// worker of a task that is currently working may register — the same
    /// authority check as a state transition, though registration is not one.
    /// "Register after a successful bind" is the worker's discipline (§8.2);
    /// the store only records what it's told.
    /// </summary>
    public async Task<StoreResult> RegisterServiceAsync(
        WorkerCaller caller, string name, int port, CancellationToken ct = default)
    {
        var row = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == caller.Task.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {caller.Task}");
        if (row.State != TaskState.Working)
            return new StoreResult.Rejected(Rule.InvalidSourceState,
                $"services register only while working, not {row.State}");
        if (row.TeamId != caller.Team.Value || row.CurrentInstanceId != caller.Instance.Value)
            return new StoreResult.Rejected(Rule.IncumbentInstanceOnly,
                "only the incumbent worker of this task may register a service");

        db.RegisteredServices.Add(new RegisteredServiceRow
        {
            TaskId = caller.Task.Value,
            TeamId = caller.Team.Value,
            Name = name,
            Port = port,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return new StoreResult.Applied(row.ToDomain(), []);
    }

    /// <summary>
    /// The worker's own assignment (§7, worker-skill.md): the prose description,
    /// completion criteria, workspace, namespace, and attempt count a dispatched
    /// worker reads before starting. A pure read gated by the same authority as a
    /// worker transition — returned <b>only</b> for the caller's own task and
    /// <b>only</b> while the caller is that task's incumbent instance (the
    /// RegisterServiceAsync gate, §9 check 14). Anything else returns null, so a
    /// zombie or a cross-task token learns nothing — never another task's content.
    /// </summary>
    public async Task<WorkerAssignment?> GetAssignmentAsync(WorkerCaller caller, CancellationToken ct = default)
    {
        var row = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == caller.Task.Value, ct);
        if (row is null)
            return null;
        if (row.TeamId != caller.Team.Value || row.CurrentInstanceId != caller.Instance.Value)
            return null;

        return new WorkerAssignment(
            row.Namespace, row.Description, row.CompletionCriteria, row.Workspace, row.Attempt,
            row.WorkerReport, row.InputQuestion, row.InputAnswer);
    }

    /// <summary>
    /// The Team view read (§10, §12): task counts by state plus a per-task
    /// structural summary, scoped to one Team. A pure read — it runs no
    /// transition and returns no prose (§10). The caller's Team comes from its
    /// lead claim, never a parameter, so a Lead only ever sees its own Team.
    /// </summary>
    public async Task<TeamStateView> GetTeamStateAsync(TeamId team, CancellationToken ct = default)
    {
        var rows = await db.Tasks.AsNoTracking()
            .Where(t => t.TeamId == team.Value)
            .Select(t => new
            {
                t.Id,
                t.Namespace,
                t.State,
                t.CompletionMode,
                t.Attempt,
                Parked = t.ParkMachine != null,
                t.ContinuesTaskId,
                t.CompletionProvenance,
                // §10: the bulk read carries only a flag that a report exists, never
                // the prose — the Lead fetches the text per task via get_task_report.
                HasReport = t.WorkerReport != null,
                // §10/§11 the same way for the worker's question: the KIND is typed
                // structure and rides along (it tells a Lead who can answer, which is
                // triage), but the question text does not — get_task_question pulls it.
                t.InputKind,
                HasQuestion = t.InputQuestion != null,
            })
            .ToListAsync(ct);

        var counts = rows
            .GroupBy(t => t.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var summaries = rows
            .Select(t => new TeamTaskSummary(
                t.Id, t.Namespace, t.State, t.CompletionMode, t.Attempt, t.Parked,
                t.ContinuesTaskId, t.CompletionProvenance, t.HasReport,
                t.InputKind, t.HasQuestion))
            .ToList();

        return new TeamStateView(team.Value, rows.Count, counts, summaries);
    }

    /// <summary>
    /// The Lead's deliberate per-task report fetch (§10, §13): the worker's opaque
    /// in-band report for one task, pulled one item at a time rather than riding the
    /// bulk <see cref="GetTeamStateAsync"/> read (which carries only a flag). Scoped
    /// to the caller's own Team in the query, so a task in another Team — or no task
    /// at all — returns null, indistinguishable and leaking nothing (§13). A non-null
    /// view with a null <see cref="TaskReportView.Report"/> means the task is the
    /// Lead's but the worker left no report. A pure read; no transition.
    /// </summary>
    public async Task<TaskReportView?> GetTaskReportAsync(TeamId team, TaskId task, CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.Id == task.Value && t.TeamId == team.Value)
            .Select(t => new TaskReportView(t.Id, t.Namespace, t.WorkerReport))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The Lead's deliberate per-task question fetch (§10/§11, §13) — the read half of
    /// the human-in-the-loop channel, shaped exactly like
    /// <see cref="GetTaskReportAsync"/>: the worker's opaque question for one task,
    /// pulled one item at a time rather than riding the bulk
    /// <see cref="GetTeamStateAsync"/> read (which carries the typed kind and a flag,
    /// never the prose). It returns the answer already given alongside, so a Lead — or
    /// a fresh one after a takeover (§4) — can see whether the question is still open
    /// before answering it twice. Team-scoped in the query, so a task in another Team,
    /// or no task at all, returns null: indistinguishable, leaking nothing (§13). A
    /// non-null view with a null question means the task is the Lead's but nothing was
    /// asked. A pure read; no transition.
    /// </summary>
    public async Task<TaskQuestionView?> GetTaskQuestionAsync(TeamId team, TaskId task, CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.Id == task.Value && t.TeamId == team.Value)
            .Select(t => new TaskQuestionView(t.Id, t.Namespace, t.State, t.InputKind, t.InputQuestion, t.InputAnswer))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The wait-TTL sweeper's poll (§11): every task in
    /// <see cref="TaskState.BlockedOnInput"/> with the timestamp it entered that
    /// state (<see cref="TaskRow.BlockedAt"/>) and its current attempt. A pure
    /// read — no transition, no prose. The sweeper ages <c>BlockedAt</c> against
    /// the configured wait TTL and cross-references the connection registry for
    /// the dispatched machine's liveness, then expresses the outcome as a
    /// <see cref="WaitTtlExpired"/> or <see cref="LivenessLost"/> command through
    /// the store — the engine, never raw SQL, owns every transition (§15).
    /// </summary>
    public async Task<IReadOnlyList<BlockedTaskView>> ListBlockedAsync(CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.State == TaskState.BlockedOnInput)
            .OrderBy(t => t.BlockedAt)
            .Select(t => new BlockedTaskView(t.Id, t.BlockedAt, t.Attempt, t.HarnessSessionRef))
            .ToListAsync(ct);

    /// <summary>
    /// A pure read of a task's current state, or null if it does not exist. The
    /// dispatch loop and the runner event sink read state to decide whether an
    /// inbound runner event still bears on a working task — e.g. an
    /// <c>exited</c> after the worker already reported result is moot (§10).
    /// </summary>
    public async Task<TaskState?> GetStateAsync(TaskId id, CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.Id == id.Value)
            .Select(t => (TaskState?)t.State)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Whether this task has registered a service (§8.2) — i.e. declared that
    /// something it started is meant to stay reachable. Read by the §10 per-task
    /// liveness scan: a service-bearing task is exempt from the no-progress ceiling,
    /// because sitting idle while others use its service is the job, not a hang. It
    /// is deliberately this fact and not a flag on <c>create_task</c>: the worker
    /// earns the exemption by a deliberate, observable protocol act at the moment it
    /// becomes true, rather than the Lead predicting it before any work has happened
    /// (§2 principle 2 — derive, do not ask).
    /// </summary>
    public Task<bool> HasRegisteredServiceAsync(TaskId id, CancellationToken ct = default) =>
        db.RegisteredServices.AsNoTracking().AnyAsync(s => s.TaskId == id.Value, ct);

    /// <summary>
    /// Stamps the opaque harness session ref onto a task row (§11 resume), from a
    /// <see cref="Docket.Contracts.SessionStartedEvent"/> the runner event sink
    /// received. Not a state transition — this is transport metadata the plane
    /// never interprets (like <see cref="TaskRow.ResultReference"/>/<c>TraceContext</c>),
    /// so it is a targeted set-based write that runs no engine transition, takes no
    /// xmin token, and fires no NOTIFY — exactly the shape of the token-revoke
    /// effect. Latest write wins: a resumed or restarted session overwrites the ref
    /// so a subsequent park carries the current session. A no-op row count when the
    /// task no longer exists, which is fine — the ref is only ever read on dispatch.
    /// </summary>
    public async Task StampHarnessSessionRefAsync(TaskId id, string sessionRef, CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => t.Id == id.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.HarnessSessionRef, sessionRef), ct);

    /// <summary>
    /// Records a runner <c>auth-failed</c> event (§11) as a first-class task event
    /// row — the structured operation/target/error-code/missing-scope facts the
    /// runner reported — which the §12 event log renders as its own detail line
    /// (#50). The actionable remediation menu §11 describes is still not built.
    /// Not a state transition: an auth failure does not move the task through §6,
    /// so this appends an event row with no from/to state, runs no engine
    /// transition, and takes no xmin token — but fires the same NOTIFY a transition
    /// does, so a listening dashboard wakes on it. The event carries only a task id
    /// (§10), so the owning Team is resolved from the row; a no-op when the task no
    /// longer exists, which is fine — the row is only ever read by the dashboard.
    /// </summary>
    public async Task RecordAuthFailureAsync(
        TaskId task, string operation, string target, string errorCode, string? missingScope,
        CancellationToken ct = default)
    {
        var teamId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == task.Value)
            .Select(t => (Guid?)t.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is null)
            return;

        db.TaskEvents.Add(new TaskEventRow
        {
            TaskId = task.Value,
            TeamId = teamId.Value,
            Kind = TaskEventRow.AuthFailedKind,
            AuthOperation = operation,
            AuthTarget = target,
            AuthErrorCode = errorCode,
            AuthMissingScope = missingScope,
            OccurredAt = clock.GetUtcNow(),
        });
        await CommitAsync(task.Value, ct);
    }

    /// <summary>
    /// Records a runner <c>subagent-spawned</c> event (§10/§12) as a first-class
    /// task event row so the dashboard shows it as a progress signal (subagent
    /// lineage). Same out-of-band shape as <see cref="RecordAuthFailureAsync"/>:
    /// no transition, no xmin token, one NOTIFY. The agent ids are progressive
    /// enhancement — both null when the harness reports no lineage (§10) — so they
    /// are stored verbatim, nulls and all. A no-op when the task is gone.
    /// </summary>
    public async Task RecordSubagentSpawnAsync(
        TaskId task, string? agentId, string? parentAgentId, CancellationToken ct = default)
    {
        var teamId = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == task.Value)
            .Select(t => (Guid?)t.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is null)
            return;

        db.TaskEvents.Add(new TaskEventRow
        {
            TaskId = task.Value,
            TeamId = teamId.Value,
            Kind = TaskEventRow.SubagentSpawnedKind,
            SubagentId = agentId,
            SubagentParentId = parentAgentId,
            OccurredAt = clock.GetUtcNow(),
        });
        await CommitAsync(task.Value, ct);
    }

    /// <summary>
    /// The working tasks the §9.9 containment sweep still owes a <c>stop</c>: those in
    /// <paramref name="teams"/> (the Teams over their ceiling) that are working right now
    /// and do not already carry a <see cref="TaskEventRow.BudgetExhaustedStopKind"/> row.
    ///
    /// <para>The event row is the idempotency record, which is why the exclusion is part of
    /// this query rather than sweeper state: the sweep runs on the liveness timer, and a
    /// stopped task stays <c>working</c> for its whole wind-down window — comfortably longer
    /// than one tick — so without it every pass would re-stop the same task and write another
    /// row. Durable across a plane restart for free, unlike an in-memory set.</para>
    ///
    /// <para>Empty when <paramref name="teams"/> is, so the sweep costs one cheap query on
    /// the overwhelmingly common no-Team-exhausted tick.</para>
    /// </summary>
    public async Task<IReadOnlyList<BudgetStopCandidate>> WorkingTasksAwaitingBudgetStopAsync(
        IReadOnlyList<TeamId> teams, CancellationToken ct = default)
    {
        if (teams.Count == 0)
            return [];

        var teamIds = teams.Select(t => t.Value).ToList();
        var rows = await db.Tasks.AsNoTracking()
            .Where(t => teamIds.Contains(t.TeamId)
                        && t.State == TaskState.Working
                        && !db.TaskEvents.Any(e =>
                            e.TaskId == t.Id && e.Kind == TaskEventRow.BudgetExhaustedStopKind))
            .Select(t => new { t.Id, t.TeamId })
            .ToListAsync(ct);

        return rows
            .Select(r => new BudgetStopCandidate(new TaskId(r.Id), new TeamId(r.TeamId)))
            .ToList();
    }

    /// <summary>
    /// Records the §9.9 containment sweep's <c>stop</c> against a task — same out-of-band
    /// shape as <see cref="RecordAuthFailureAsync"/>: no transition, no xmin token, one
    /// NOTIFY so a listening dashboard wakes on it. <paramref name="detail"/> carries the
    /// ceiling facts as prose for the operator, because this row's whole job is to explain a
    /// silence.
    ///
    /// <para>Called only after the stop was actually delivered to the machine, so the row
    /// never claims a stop that was not sent — and an undelivered one is left for the next
    /// sweep to retry rather than papered over.</para>
    /// </summary>
    public async Task RecordBudgetExhaustedStopAsync(
        TaskId task, TeamId team, string detail, CancellationToken ct = default)
    {
        db.TaskEvents.Add(new TaskEventRow
        {
            TaskId = task.Value,
            TeamId = team.Value,
            Kind = TaskEventRow.BudgetExhaustedStopKind,
            Detail = detail,
            OccurredAt = clock.GetUtcNow(),
        });
        await CommitAsync(task.Value, ct);
    }

    private async Task<StoreResult> RunTransition(
        TaskRow row, TaskCommand command, CancellationToken ct,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? outerTx = null)
    {
        var before = row.State;
        var result = TaskStateMachine.Apply(row.ToDomain(), command);
        if (result is TransitionResult.Rejected r)
            return new StoreResult.Rejected(r.Rule, r.Reason);

        var ok = (TransitionResult.Transitioned)result;
        row.CopyFrom(ok.Task);
        // #23, §7: the reported result reference is opaque content the store
        // captures on the working → verifying transition. The engine and
        // TaskRecord stay content-free (the reference never lands on the pure
        // state), and CopyFrom deliberately does not carry it — so a succeeding
        // ReportResult is the one place the row's ResultReference is written. The
        // verifier read scope this was written for is gone (§7: completion is
        // Lead-adjudicated), and no read surface exposes the column today; the
        // worker's in-band report below is what a Lead actually reads.
        if (command is ReportResult reported)
        {
            row.ResultReference = reported.ResultReference;
            // §10: the worker's in-band report rides the same transition, opaque and
            // verbatim (the engine already size-capped it). Null leaves the column null.
            row.WorkerReport = reported.Report;
        }
        // §11 wait-TTL: stamp when the task entered blocked_on_input so the sweeper
        // can age its wait deadline, and clear it on the way out. Opaque plane
        // plumbing captured here (like ResultReference above), never an engine field —
        // the pure state machine holds no clock (§6: timers live in the control plane).
        // The same working → blocked_on_input transition also carries the typed
        // request kind onto its event row (§10/§12, #50) so the dashboard can render
        // what kind of attention the task needs — the kind is command content the
        // engine already requires, threaded through here, not onto the pure state.
        InputRequestKind? inputKind = null;
        if (command is RequestInput ri && row.State == TaskState.BlockedOnInput)
        {
            row.BlockedAt = clock.GetUtcNow();
            inputKind = ri.Kind;
            // §10/§11: the ask itself — the live kind and the opaque question text —
            // lands on the row so the surfaces that answer it (the §12 inbox, the
            // Lead's per-task fetch) can show WHAT is being asked, not just that
            // something is. A new question retires the previous exchange's answer, or
            // the worker would resume seeing this question paired with the last
            // answer. Engine-capped above; stored verbatim, never parsed.
            row.InputKind = ri.Kind;
            row.InputQuestion = ri.Question;
            row.InputAnswer = null;
        }
        else if (before == TaskState.BlockedOnInput && row.State != TaskState.BlockedOnInput)
            row.BlockedAt = null;

        // §11: the answer's text, on whichever half of the one-call answer path ran —
        // AnswerInput for a still-blocked task, WakeParked for one the sweeper parked
        // first. Captured here beside BlockedAt for the same reason ResultReference is:
        // opaque content the engine validated (length only) but never landed on the
        // pure record. The redispatched worker reads it back on get_task. Only a
        // command that actually carries words writes: clearing is the asking side's
        // job, so a wordless wake (an endpoint_wait's service appearing, a Lead
        // resuming a task it parked itself) never erases a live exchange.
        var answerText = command switch
        {
            AnswerInput answered => answered.Answer,
            WakeParked woken => woken.Answer,
            _ => null,
        };
        if (answerText is not null)
            row.InputAnswer = answerText;
        ApplyEffects(row, ok.Effects);
        AppendEvent(row.Id, row.TeamId, command.GetType().Name, before, row.State,
            detail: DescribeEffects(ok.Effects), inputKind: inputKind);

        try
        {
            await CommitAsync(row.Id, ct, outerTx);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new StoreResult.Conflict($"task {row.Id} moved concurrently; re-read and retry");
        }

        return new StoreResult.Applied(ok.Task, ok.Effects);
    }

    private void ApplyEffects(TaskRow row, IReadOnlyList<Effect> effects)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case MintWorkerInstanceToken mint:
                    db.WorkerInstances.Add(new WorkerInstanceRow
                    {
                        Id = mint.Instance.Value,
                        TaskId = row.Id,
                        Revoked = false,
                        CreatedAt = clock.GetUtcNow(),
                        // §12: the durable record of where this dispatch ran, so a terminal
                        // task's machine-local transcript can still be found.
                        MachineId = mint.Machine,
                    });
                    break;

                case RevokeWorkerInstanceToken revoke:
                    // Set-based: no need to materialize the row to revoke it.
                    db.WorkerInstances
                        .Where(w => w.Id == revoke.Instance.Value && !w.Revoked)
                        .ExecuteUpdate(s => s
                            .SetProperty(w => w.Revoked, true)
                            .SetProperty(w => w.RevokedAt, clock.GetUtcNow()));
                    break;

                case ClearServicesAndForwards:
                    db.RegisteredServices.Where(s => s.TaskId == row.Id).ExecuteDelete();
                    // §8.3: leaving working also releases the task's relay forwards.
                    // Revoke every live grant this task produced, so a grant issued
                    // against a now-gone service can never open a tunnel — the same
                    // moment its registered services are cleared, no schema churn.
                    // Established splices are untouched; a grant only gates open.
                    db.RelayGrants
                        .Where(g => g.ProducerTaskId == row.Id && !g.Revoked)
                        .ExecuteUpdate(s => s.SetProperty(g => g.Revoked, true));
                    break;

                // WriteParkRecord is already reflected by CopyFrom (row park columns).
                // DiscardWorkspace / DeferWorkspaceDiscardUntilVerdict are docketd's
                // to enact; recorded in the event detail for the daemon to observe.
                case WriteParkRecord:
                case DiscardWorkspace:
                case DeferWorkspaceDiscardUntilVerdict:
                    break;
            }
        }
    }

    private void AppendEvent(
        Guid taskId, Guid teamId, string kind, TaskState? from, TaskState to, string? detail,
        InputRequestKind? inputKind = null)
        => db.TaskEvents.Add(new TaskEventRow
        {
            TaskId = taskId,
            TeamId = teamId,
            Kind = kind,
            FromState = from,
            ToState = to,
            Detail = detail,
            InputKind = inputKind,
            OccurredAt = clock.GetUtcNow(),
        });

    private async Task CommitAsync(
        Guid taskId, CancellationToken ct,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? outerTx = null)
    {
        var ownTx = outerTx is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            await db.SaveChangesAsync(ct);
            // NOTIFY in the same transaction: subscribers wake only on committed
            // writes, and never on a rolled-back one.
            await db.Database.ExecuteSqlAsync(
                $"SELECT pg_notify({DocketDbContext.EventChannel}, {taskId.ToString()})", ct);
            if (ownTx is not null)
                await ownTx.CommitAsync(ct);
        }
        finally
        {
            if (ownTx is not null)
                await ownTx.DisposeAsync();
        }
    }

    private static string DescribeEffects(IReadOnlyList<Effect> effects) =>
        effects.Count == 0 ? "" : string.Join(",", effects.Select(e => e.GetType().Name));
}
