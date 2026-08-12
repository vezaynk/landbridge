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
public sealed class TaskStore(
    DocketDbContext db,
    TimeProvider clock,
    TeamBudgetService? budgets = null,
    TaskStorePolicy? policy = null)
{
    public async Task<StoreResult> CreateAsync(CreateTask command, CancellationToken ct = default)
    {
        var id = TaskId.New();
        var ns = $"team-{command.Team}/task-{id}";
        var result = TaskStateMachine.Create(command, id, ns);
        if (result is TransitionResult.Rejected r)
            return new StoreResult.Rejected(r.Rule, r.Reason);

        // §9 check 7: the task's infrastructure requeue cap is fixed here, from
        // control-plane config, exactly as VerificationRetryLimit is fixed by the engine's
        // default — a task carries its own terms, so changing the configured cap never
        // moves the goalposts under work already in flight. Applied to the record too, so
        // what the caller gets back and what the row holds cannot disagree.
        var task = ((TransitionResult.Transitioned)result).Task with
        {
            InfrastructureRequeueLimit =
                policy?.InfrastructureRequeueLimit ?? TaskRecord.DefaultInfrastructureRequeueLimit,
        };
        // §11 continuation, directory half: resolve which task's work dir holds the session
        // this one will resume, transitively — the source's own answer if it had one (a
        // chain: the session has lived in the root task's dir all along and B never had a
        // dir), else the source itself. Read here rather than carried on the engine's
        // Continuation command for the same reason TraceContext is: it is storage
        // bookkeeping the engine never sees (§7 content-free). One extra read, on the
        // create path only, and only for a continuation.
        Guid? workDirTask = null;
        if (command.Continues is { } continues)
        {
            var source = continues.ContinuedTask.Value;
            workDirTask = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == source)
                .Select(t => t.WorkDirTaskId)
                .FirstOrDefaultAsync(ct) ?? source;
        }

        db.Tasks.Add(new TaskRow
        {
            Id = id.Value,
            TeamId = task.Team.Value,
            Namespace = task.Namespace,
            CompletionMode = task.CompletionMode,
            State = task.State,
            Profile = task.Profile,
            VerificationRetryLimit = task.VerificationRetryLimit,
            InfrastructureRequeueLimit = task.InfrastructureRequeueLimit,
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
            WorkDirTaskId = workDirTask,
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
        // §11: the live request kind rides along so the engine can refuse this path on a
        // permission request, whose worker is still alive and waiting on a verdict rather
        // than gone and waiting to be redispatched.
        return await RunTransition(row, new AnswerInput(lead, park, answer, row.InputKind), ct);
    }

    /// <summary>
    /// Decide a pending permission request (§11 permission bridge): blocked_on_input →
    /// working, with the still-live worker's own instance carried through, so the harness
    /// resumes inside the tool call it blocked in. The counterpart to
    /// <see cref="AnswerOrWakeAsync"/> for the one input kind that is answered by a verdict
    /// rather than by prose, and the reason the two are separate methods rather than one:
    /// they lead to opposite outcomes (resume in place vs. requeue for redispatch), so
    /// picking the wrong one is a bug the engine refuses (§11) instead of a difference the
    /// caller has to know about.
    ///
    /// <para>The escalation state and the request's kind are read off the row here and
    /// handed to the engine as facts, which is what makes escalation enforceable: a Lead
    /// answering an escalated request is refused by
    /// <see cref="Rule.EscalatedPermissionIsHumanOnly"/> on the row's own record of the
    /// escalation, not on anything the caller says about itself. A human is admitted either
    /// way. The Team check for a Lead is the engine's (<c>IsLeadOrHuman</c>); a human is
    /// unscoped, exactly as on every other §12 write.</para>
    /// </summary>
    public async Task<StoreResult> AnswerPermissionAsync(
        Actor actor, TaskId id, PermissionVerdict verdict, string? message = null,
        CancellationToken ct = default)
    {
        var row = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        return await RunTransition(
            row,
            new AnswerPermission(
                actor, row.InputKind, row.PermissionEscalatedAt is not null, verdict, message),
            ct);
    }

    /// <summary>
    /// Mark a pending permission request human-only (§11 permission bridge). Not a state
    /// change — the worker is still blocked on the same request — but from here a Lead is
    /// refused and the request waits for a person, with <paramref name="reason"/> rendered
    /// beside it on the §12 inbox so the human inherits the concern along with the
    /// decision. The wait deadline is deliberately not reset (see
    /// <see cref="TaskRow.PermissionEscalatedAt"/>): an escalation nobody picks up parks on
    /// the same schedule the Lead's own wait would have.
    /// </summary>
    public async Task<StoreResult> EscalatePermissionAsync(
        Actor actor, TaskId id, string reason, CancellationToken ct = default)
    {
        var row = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        return await RunTransition(row, new EscalatePermission(actor, row.InputKind, reason), ct);
    }

    /// <summary>
    /// The relaying worker tool's wait (§11 permission bridge): block until this task's
    /// pending permission request is decided, then hand back the verdict and its message.
    /// The one live wait in Docket, and the harness contract is why — a permission prompt
    /// has nowhere to deliver an answer to a process that has exited, so the asking process
    /// stays up inside its tool call and this method is what holds it there.
    ///
    /// <para>Returns null when the wait ended without a verdict: the wait-TTL sweeper parked
    /// the task, a requeue took it, or the caller stopped being the incumbent. The caller
    /// turns that into a denial with an explanation rather than hanging, because the harness
    /// side never times out on its own (a permission prompt waits forever), so an
    /// unanswered request would otherwise wedge the process until something killed it.</para>
    ///
    /// <para>Polling, not listening: the row is the authority on the verdict and on whether
    /// this caller is still the incumbent, and both have to be re-read anyway to answer
    /// honestly. <paramref name="pollInterval"/> is a parameter so tests run this at
    /// millisecond granularity against a real clock instead of advancing a fake one through
    /// a delay.</para>
    /// </summary>
    public async Task<PermissionOutcome?> AwaitPermissionVerdictAsync(
        WorkerCaller caller, TimeSpan pollInterval, TimeProvider clock, CancellationToken ct = default)
    {
        while (true)
        {
            var seen = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == caller.Task.Value)
                .Select(t => new
                {
                    t.State,
                    t.CurrentInstanceId,
                    t.InputKind,
                    t.PermissionVerdict,
                    t.InputAnswer,
                })
                .FirstOrDefaultAsync(ct);

            // Gone, requeued out from under this worker, or handed to a successor: this
            // caller is no longer the one whose tool call is owed an answer.
            if (seen is null || seen.CurrentInstanceId != caller.Instance.Value)
                return null;

            if (seen.PermissionVerdict is { } verdict && seen.InputKind == InputRequestKind.Permission)
                return new PermissionOutcome(verdict, seen.InputAnswer);

            // Parked by the sweeper, or moved on for any other reason, with no verdict.
            if (seen.State != TaskState.BlockedOnInput)
                return null;

            await Task.Delay(pollInterval, clock, ct);
        }
    }

    /// <summary>
    /// One pending permission request as an answerer reads it (§11/§12) — the Lead through
    /// its per-task fetch, a human through the inbox. Team-scoped for a Lead by the caller.
    /// </summary>
    public async Task<PermissionRequestView?> GetPermissionRequestAsync(
        TaskId task, CancellationToken ct = default) =>
        await db.Tasks.AsNoTracking()
            .Where(t => t.Id == task.Value)
            .Select(t => new PermissionRequestView(
                t.Id, t.Namespace, t.TeamId, t.State, t.BlockedAt, t.PermissionTool,
                t.InputQuestion, t.PermissionVerdict, t.InputAnswer,
                t.PermissionEscalatedAt, t.PermissionEscalationReason))
            .FirstOrDefaultAsync(ct);

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
                // WorkDirTaskId is deliberately NOT cleared. Directory inheritance is a
                // property of continuation, not of resume (§7, §11): this task still works
                // where its predecessor worked, and keeping it means the task's directory is
                // the same on every attempt instead of moving when a session is abandoned.
                // On this machine that directory starts empty — the predecessor's artifacts
                // went with the machine that vanished — which is what the memory-lost event
                // below is telling the Lead.
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
                // §7/§11: which task's work dir the harness runs in. Deliberately NOT
                // suppressed with the session ref — a continuation works where its
                // predecessor worked whether or not it resumes the transcript, so this rides
                // every dispatch of a continuation, cold starts included.
                WorkDirTask = claimed.WorkDirTaskId is { } dir ? new TaskId(dir) : null,
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
                // §6/§9 check 7: the infrastructure story is structure, so it rides the
                // bulk read — the count and why the last requeue happened. On a canceled
                // task the reason is how a Lead tells the cap from a deliberate cancel.
                t.InfrastructureRequeues,
                t.LastRequeueReason,
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
                t.InputKind, t.HasQuestion,
                t.InfrastructureRequeues, t.LastRequeueReason))
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
            // §8.1: the artifact pointer rides along with the prose because this is the
            // adjudication read (§7) and the two are asymmetric — §6 REQUIRES the
            // reference for working → verifying while the report is optional, so a task
            // whose worker wrote no prose still has a reference, and it is then the only
            // thing the worker said. Verbatim, never dereferenced (#81).
            // §6/§9 check 7: the infrastructure account rides the per-task fetch in full —
            // count, cap, and the last reason — because this is where a Lead asks "what
            // happened to this task", and for a task the cap abandoned it is the ONLY
            // answer: there is no worker report to read when nothing ever finished.
            .Select(t => new TaskReportView(
                t.Id, t.Namespace, t.WorkerReport, t.ResultReference,
                t.InfrastructureRequeues, t.InfrastructureRequeueLimit, t.LastRequeueReason))
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
            .Select(t => new TaskQuestionView(
                t.Id, t.Namespace, t.State, t.InputKind, t.InputQuestion, t.InputAnswer,
                t.PermissionTool, t.PermissionVerdict, t.PermissionEscalationReason))
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
    /// The dispatches a machine still holds according to committed state (§10, #86) —
    /// what <see cref="DispatchService.RehydrateMachineAsync"/> re-adopts when that
    /// machine reconnects, so a plane restart no longer strands in-flight work.
    ///
    /// <para>Both live states are included: <see cref="TaskState.Working"/>, and
    /// <see cref="TaskState.BlockedOnInput"/> — a blocked task's harness process is
    /// expected to be gone but the machine still holds its lease until the wait-TTL
    /// sweeper parks it or the machine dies (§11), and the sweeper resolves that machine
    /// through the registry, so it has to be re-adopted too or a blocked task outlives a
    /// restart with nothing able to park or requeue it.</para>
    ///
    /// <para><b>Fenced on the current worker instance</b> (§9.14), which is what keeps
    /// re-adoption from resurrecting the wrong dispatch. The row's
    /// <see cref="TaskRow.CurrentInstanceId"/> names the one incumbent attempt, and the
    /// instance row carries the machine it was minted for — so a task is re-adopted only
    /// by the machine its live incumbent instance actually runs on. The two exclusions
    /// that matters for: a requeue nulls <c>CurrentInstanceId</c> and revokes the
    /// instance, so a task already freed by a disconnect is never re-adopted (which is
    /// what makes a flapping machine cost exactly one requeue per disconnect, #87); and
    /// a task whose incumbent runs on a different machine is never adopted by this one,
    /// so a stale instance's events keep landing on an untracked task and are still
    /// refused rather than reviving a dispatch that has moved on.</para>
    /// </summary>
    public async Task<IReadOnlyList<TaskId>> HeldDispatchesOnAsync(
        string machineId, CancellationToken ct = default)
    {
        var ids = await db.Tasks.AsNoTracking()
            .Where(t => (t.State == TaskState.Working || t.State == TaskState.BlockedOnInput)
                && db.WorkerInstances.Any(w =>
                    w.Id == t.CurrentInstanceId && !w.Revoked && w.MachineId == machineId))
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);
        return ids.Select(id => new TaskId(id)).ToArray();
    }

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
        // ReportResult is the one place the row's ResultReference is written. It is
        // read back by the Lead's per-task get_task_report fetch and the §12
        // dashboard (#81) — the §7 adjudication read — alongside the worker's
        // optional in-band report below.
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
            // §11 permission bridge: the tool this request is about, and a clean slate for
            // the decision. Retiring the previous verdict and escalation matters more here
            // than retiring an answer does: a stale verdict is what the relaying worker tool
            // polls for, so leaving one behind would let a second request read the first
            // request's answer and return it as this call's.
            row.PermissionTool = ri.PermissionTool;
            row.PermissionVerdict = null;
            row.PermissionEscalatedAt = null;
            row.PermissionEscalationReason = null;
        }
        else if (before == TaskState.BlockedOnInput && row.State != TaskState.BlockedOnInput)
            row.BlockedAt = null;

        // §11 permission bridge, the two transitions that decide and re-route a permission
        // request. Captured here beside BlockedAt for the same reason: opaque content and
        // plane plumbing the engine gated (kind, authority, length) but never landed on the
        // pure record. The verdict is what the still-blocked worker's tool call is polling
        // for, so it and its message commit in the same transaction as the transition —
        // there is no window where the task is working but the verdict has not landed.
        if (command is AnswerPermission decided)
        {
            row.PermissionVerdict = decided.Verdict;
            row.InputAnswer = decided.Message;
        }
        else if (command is EscalatePermission escalated)
        {
            row.PermissionEscalatedAt = clock.GetUtcNow();
            row.PermissionEscalationReason = escalated.Reason;
        }

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
        // §6/§9 check 7 (#73): the requeue's reason onto its own event row, the same way
        // the input-request kind rides its transition. The row's from/to states already
        // say which outcome the requeue took, so reason + to-state is the whole story an
        // operator needs — where before every requeue row was identical.
        // §11/§12 permission audit: a permission decision's own row carries the verdict and
        // the answerer's class, and its Detail is the message rather than the (empty) effect
        // list — for these two transitions the words ARE what happened, where for every
        // other transition the effects are. An escalation's Detail is its required reason,
        // so the trail says who narrowed the request and why even though the state did not
        // move.
        var detail = command switch
        {
            AnswerPermission decision => decision.Message,
            EscalatePermission escalation => $"escalated to human: {escalation.Reason}",
            _ => DescribeEffects(ok.Effects),
        };
        AppendEvent(row.Id, row.TeamId, command.GetType().Name, before, row.State,
            detail: detail, inputKind: inputKind,
            livenessReason: (command as LivenessLost)?.Reason,
            permissionVerdict: (command as AnswerPermission)?.Verdict,
            permissionAnswerer: command is AnswerPermission byWhom ? AnswererOf(byWhom.Actor) : null);

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
                // DiscardWorkspace / DeferWorkspaceDiscardUntilVerdict would be docketd's to
                // enact, and nothing does (§11: "nothing enacts workspace discard today").
                // No §10 command carries a workspace discard and docketd reads no event
                // details, so this arm is where the intent stops — a discard cancel and a
                // preserve cancel leave identical rows.
                case WriteParkRecord:
                case DiscardWorkspace:
                case DeferWorkspaceDiscardUntilVerdict:
                    break;
            }
        }
    }

    private void AppendEvent(
        Guid taskId, Guid teamId, string kind, TaskState? from, TaskState to, string? detail,
        InputRequestKind? inputKind = null, LivenessLossReason? livenessReason = null,
        PermissionVerdict? permissionVerdict = null, PermissionAnswerer? permissionAnswerer = null)
        => db.TaskEvents.Add(new TaskEventRow
        {
            TaskId = taskId,
            TeamId = teamId,
            Kind = kind,
            FromState = from,
            ToState = to,
            Detail = detail,
            InputKind = inputKind,
            LivenessReason = livenessReason,
            PermissionVerdict = permissionVerdict,
            PermissionAnswerer = permissionAnswerer,
            OccurredAt = clock.GetUtcNow(),
        });

    /// <summary>
    /// Which class of answerer a permission decision came from (§11/§12), derived from the
    /// deciding actor rather than supplied — the same discipline as §9 check 4's verdict
    /// provenance, so a caller cannot claim to be a human. A <see cref="HumanSession"/> is
    /// the only thing that reads as <see cref="PermissionAnswerer.Human"/>; the engine has
    /// already refused anything that is neither a human nor the owning Team's Lead by the
    /// time this runs.
    /// </summary>
    private static PermissionAnswerer AnswererOf(Actor actor) =>
        actor is HumanSession ? PermissionAnswerer.Human : PermissionAnswerer.Lead;

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
