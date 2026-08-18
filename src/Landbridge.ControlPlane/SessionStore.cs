using System.Diagnostics;
using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane;

/// <summary>
/// The only write path to task state (spec §15: the control plane is the only
/// path to the state machine). Every mutation runs a transition through
/// <see cref="SessionStateMachine"/>, then persists the resulting record, its
/// effects, and an event row in one transaction — so a transition and its
/// consequences (token mint/revoke, service clearing, park record) are
/// atomic, and a NOTIFY fires only if the write commits.
///
/// <para>Atomic including the set-based effects, which is why <see cref="RunTransition"/>
/// opens the transaction itself rather than leaving it to <see cref="CommitAsync"/>: an
/// <c>ExecuteUpdate</c> effect issues its SQL where it stands, so a transaction opened after
/// the effects would leave a revoke committed beside a row that never moved.</para>
///
/// <para><paramref name="forwards"/> is the one consequence that cannot be part of that
/// transaction, because it is not a write: closing a live relay splice is a command to two
/// machines (§8.3, <see cref="ForwardTeardownService"/>), so it runs <b>after</b> the commit
/// and only for what the committed effect said. Null when a host wired the store without
/// <see cref="ForwardingServiceCollectionExtensions.AddLandbridgeForwarding"/> and in the pure
/// store tests, where the rows are the subject and there are no machines to tell.</para>
/// </summary>
public sealed class SessionStore(
    LandbridgeDbContext db,
    TimeProvider clock,
    SessionStorePolicy? policy = null,
    ForwardTeardownService? forwards = null)
{
    public async Task<StoreResult> CreateAsync(CreateSession command, CancellationToken ct = default)
    {
        var id = SessionId.New();
        var ns = $"team-{command.Team}/session-{id}";
        var result = SessionStateMachine.Create(command, id, ns);
        if (result is TransitionResult.Rejected r)
            return new StoreResult.Rejected(r.Rule, r.Reason);

        // §9 check 7: the task's infrastructure requeue cap is fixed here, from
        // control-plane config, exactly as VerificationRetryLimit is fixed by the engine's
        // default — a task carries its own terms, so changing the configured cap never
        // moves the goalposts under work already in flight. Applied to the record too, so
        // what the caller gets back and what the row holds cannot disagree.
        var task = ((TransitionResult.Transitioned)result).Session with
        {
            InfrastructureRequeueLimit =
                policy?.InfrastructureRequeueLimit ?? SessionRecord.DefaultInfrastructureRequeueLimit,
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
            var source = continues.ContinuedSession.Value;
            workDirTask = await db.Sessions.AsNoTracking()
                .Where(t => t.Id == source)
                .Select(t => t.WorkDirSessionId)
                .FirstOrDefaultAsync(ct) ?? source;
        }

        db.Sessions.Add(new SessionRow
        {
            Id = id.Value,
            TeamId = task.Team.Value,
            Namespace = task.Namespace,
            CompletionMode = task.CompletionMode,
            State = task.State,
            Profile = task.Profile,
            VerificationRetryLimit = task.VerificationRetryLimit,
            InfrastructureRequeueLimit = task.InfrastructureRequeueLimit,
            // Opaque content the engine never interpreted (§7): persisted verbatim.
            // Completion criteria used to be a sibling field; the description is the brief.
            CompletionCriteria = "",
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
            ContinuesSessionId = command.Continues?.ContinuedSession.Value,
            WorkDirSessionId = workDirTask,
            PreferredMachine = command.Continues?.PreferredMachine,
            OnMachineGone = command.Continues?.OnMachineGone,
            HarnessSessionRef = command.Continues?.InheritedSessionRef,
        });
        AppendEvent(id.Value, task.Team.Value, "created", from: null, to: task.State, detail: null);
        await CommitAsync(id.Value, ct);
        return new StoreResult.Applied(task, []);
    }

    /// <summary>Apply a command to an existing task, addressed by id.</summary>
    public async Task<StoreResult> ApplyAsync(SessionId id, SessionCommand command, CancellationToken ct = default)
    {
        var row = await db.Sessions.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        return await RunTransition(row, command, ct);
    }

    /// <summary>
    /// A Lead's answer to a task awaiting input, routed to whichever §6
    /// transition the task's current state needs — so a Lead answering never has
    /// to know whether the wait-TTL sweeper (§11) parked the task first. One
    /// call, correct outcome either way, and both outcomes requeue for redispatch
    /// rather than resuming in place (§11: the park shape, which every kind a worker
    /// chooses to ask takes — a permission request is the exception, and this method
    /// refuses it rather than handling it, below):
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
    ///   failed → submitted           (<see cref="WakeParked"/>): same wake as a
    ///     park. Infrastructure gave up; the Lead's note is the resume prompt.
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
    /// rides every branch for the same reason the routing exists: the caller does not
    /// know which one it is taking, so the text must land either way. All commands cap
    /// it at the engine, and the row keeps it for the worker's next <c>get_session</c>.
    /// Null answers nothing in words — the transition still unblocks the task, which is
    /// what an <c>endpoint_wait</c> wake or a bare unblock wants.</para>
    ///
    /// <para><paramref name="sessionLive"/> is whether the asking ACP process is still
    /// up on the held-lease machine. True continues the same session
    /// (<see cref="ContinueSession"/> → working, same instance). False is the
    /// process-gone path: park and requeue for redispatch. A parked row still wakes
    /// regardless — the session was already released.</para>
    /// </summary>
    public async Task<StoreResult> AnswerOrWakeAsync(
        LeadClaim lead, SessionId id, string? leaseMachine, string? answer = null,
        bool sessionLive = false,
        CancellationToken ct = default)
    {
        var row = await db.Sessions.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        if (row.State is SessionState.Parked or SessionState.Failed)
        {
            if (row.TeamId != lead.Team.Value)
                return new StoreResult.Rejected(Rule.ActorLacksAuthority,
                    "input requests are answered by the Lead or a human");
            return await RunTransition(row, new WakeParked(answer), ct);
        }

        // A live ACP session takes the in-place path: same instance, same process,
        // a follow-up prompt after this commit. Permission stays on the verdict
        // path — ContinueSession refuses it the same way AnswerInput does.
        if (sessionLive)
        {
            // A live worker the Lead spoke to without being asked — including a
            // reply to a report still sitting in verifying — is a follow-up on
            // the same session, not a continue-from-blocked. Permission is
            // never this path — ContinueSession / LeadMessage both refuse it.
            if (row.State == SessionState.Verifying
                || (row.State == SessionState.Working && row.InputKind is null))
                return await RunTransition(row, new LeadMessage(lead, answer, row.InputKind), ct);
            return await RunTransition(row, new ContinueSession(lead, answer, row.InputKind), ct);
        }

        // §11: the redispatch park record is the machine still holding the lease, preferred
        // for transcript resume. Same shape the wait-TTL sweeper writes. Null when that
        // machine is gone, so redispatch cold-starts elsewhere from the workspace (§11).
        // Ignored by the engine on any non-blocked state (the fall-through wrong-state
        // rejection). The session ref redispatch resumes stays on the row, read live there.
        var park = leaseMachine is { } machine ? new ParkRecord(machine) : null;
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
        Actor actor, SessionId id, PermissionVerdict verdict, string? message = null,
        CancellationToken ct = default)
    {
        var row = await db.Sessions.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
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
    /// <see cref="SessionRow.PermissionEscalatedAt"/>): an escalation nobody picks up parks on
    /// the same schedule the Lead's own wait would have.
    /// </summary>
    public async Task<StoreResult> EscalatePermissionAsync(
        Actor actor, SessionId id, string reason, CancellationToken ct = default)
    {
        var row = await db.Sessions.FirstOrDefaultAsync(t => t.Id == id.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {id}");

        return await RunTransition(row, new EscalatePermission(actor, row.InputKind, reason), ct);
    }

    /// <summary>
    /// The relaying worker tool's wait (§11 permission bridge): block until this task's
    /// pending permission request is decided, then hand back the verdict and its message.
    /// The one live wait in Landbridge, and the harness contract is why — a permission prompt
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
            var seen = await db.Sessions.AsNoTracking()
                .Where(t => t.Id == caller.Session.Value)
                .Select(t => new
                {
                    t.State,
                    t.CurrentInstanceId,
                    t.InputKind,
                    t.PermissionVerdict,
                })
                .FirstOrDefaultAsync(ct);

            // Gone, requeued out from under this worker, or handed to a successor: this
            // caller is no longer the one whose tool call is owed an answer.
            if (seen is null || seen.CurrentInstanceId != caller.Instance.Value)
                return null;

            if (seen.PermissionVerdict is { } verdict && seen.InputKind == InputRequestKind.Permission)
            {
                // The note lives on the event, not InputAnswer (that is the Lead's
                // get_session prose). Same transaction as the verdict, so it is here.
                var message = await db.SessionEvents.AsNoTracking()
                    .Where(e => e.SessionId == caller.Session.Value && e.Kind == nameof(AnswerPermission))
                    .OrderByDescending(e => e.Seq)
                    .Select(e => e.Detail)
                    .FirstOrDefaultAsync(ct);
                return new PermissionOutcome(verdict, message);
            }

            // Parked by the sweeper, or moved on for any other reason, with no verdict.
            if (seen.State != SessionState.BlockedOnInput)
                return null;

            await Task.Delay(pollInterval, clock, ct);
        }
    }

    /// <summary>
    /// One pending permission request as an answerer reads it (§11/§12) — the Lead through
    /// its per-task fetch, a human through the inbox. Team-scoped for a Lead by the caller.
    /// </summary>
    public async Task<PermissionRequestView?> GetPermissionRequestAsync(
        SessionId task, CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
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
    /// <para>Profile is matched exactly as the engine matches it. A session
    /// without a profile is not claimable.</para>
    ///
    /// <para>Continuation targeting (§6/§11) adds a preferred-machine clause to the
    /// SQL half of check 5. A row with no <c>preferred_machine</c> (an ordinary
    /// first dispatch) is claimable by any profile-matching machine. A park or
    /// fail pins the last box so <c>session/load</c> stays in the original cwd. A
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
        // One fact, read once: the registry already folded back-pressure into Ready when it
        // took the heartbeat (RunnerConnectionRegistry.Fold), so re-testing UnderBackPressure
        // here could never refuse anything this line has not already refused. The engine still
        // re-checks both as §9 check 5's enforcement point — a pure function does not trust its
        // caller's derivation — which is what this cheap pre-check exists to stay out of the
        // way of: it only avoids opening a transaction for a machine that cannot be dispatched.
        if (!machine.Ready)
            return new StoreResult.NotFound($"machine {machine.MachineId} is not accepting dispatch");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock a single eligible row's id with SKIP LOCKED, then load the
        // entity by id inside the same transaction. Selecting only the id
        // (not SELECT *) keeps the xmin concurrency token out of the raw query;
        // the row lock is held to end of transaction, so concurrent dispatchers
        // skip it. Profile match is the SQL half of check 5: exact string, no fallback.
        // The preferred-machine clause is the §6/§11 continuation half
        // (see the method summary): NOT (preferred_machine = ANY(connected)) reads as
        // "preferred machine gone" and is true for an empty connected set too.
        var profiles = machine.DeclaredProfiles.ToArray();
        var connected = (connectedMachines is { Count: > 0 } c ? c : [machine.MachineId]).ToArray();
        var claimedId = await db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT id AS "Value" FROM sessions
                 WHERE state = 'Submitted'
                   AND profile = ANY({profiles})
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

        var claimed = await db.Sessions.FirstAsync(t => t.Id == claimedId, ct);

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
                // WorkDirSessionId is deliberately NOT cleared. Directory inheritance is a
                // property of continuation, not of resume (§7, §11): this task still works
                // where its predecessor worked, and keeping it means the task's directory is
                // the same on every attempt instead of moving when a session is abandoned.
                // On this machine that directory starts empty — the predecessor's artifacts
                // went with the machine that vanished — which is what the memory-lost event
                // below is telling the Lead.
                db.SessionEvents.Add(new SessionEventRow
                {
                    SessionId = claimed.Id,
                    TeamId = claimed.TeamId,
                    Kind = SessionEventRow.ContinuationMemoryLostKind,
                    Detail = $"preferred machine '{gonePreferred}' gone; cold-started on " +
                             $"'{machine.MachineId}' — conversational memory lost",
                    OccurredAt = clock.GetUtcNow(),
                });
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
            // Surface the row's opaque transport metadata so DispatchService can act
            // on it (neither reaches the engine): the trace context parents the
            // dispatch span on the Lead's create_session trace, and the harness session
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
                WorkDirSession = claimed.WorkDirSessionId is { } dir ? new SessionId(dir) : null,
            };
        }
        return result;
    }

    /// <summary>
    /// The seed facts a <c>create_session(continues:)</c> reads off the continued task's
    /// row (§6/§11): its owning Team (the same-Team gate), its profile (the default
    /// when the caller omits one), the opaque harness session ref to resume, the
    /// park machine (a fallback preferred machine when the task is parked and no
    /// longer tracked in the live registry), and <see cref="ContinuationSource.LastRanOn"/>
    /// — the machine of the task's most recent dispatch, read off its worker-instance
    /// rows. A pure read; null when the continued task does not exist. Team-scoping is
    /// deliberately not applied here so the tool can surface a precise cross-Team
    /// rejection rather than an indistinguishable not-found (the engine enforces the
    /// same-Team gate on the resulting command).
    ///
    /// <para><b>Why the instance rows and not just the two columns.</b> The registry
    /// forgets a task the moment its process exits and <c>ParkMachine</c> only ever
    /// describes a parked task, so for the ordinary continuation — one whose predecessor
    /// finished — neither can say where it ran, and that used to be refused outright.
    /// <see cref="WorkerInstanceRow.MachineId"/> is the durable answer (one row per
    /// dispatch, §12) and outlives both. Deliberately <em>not</em> filtered on
    /// <c>!Revoked</c>: a task that reached a terminal state has had its last instance
    /// revoked, and where it ran is still where it ran.</para>
    /// </summary>
    public async Task<ContinuationSource?> ReadContinuationSourceAsync(SessionId continued, CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
            .Where(t => t.Id == continued.Value)
            .Select(t => new ContinuationSource(
                new TeamId(t.TeamId), t.Profile, t.HarnessSessionRef, t.ParkMachine,
                db.WorkerInstances
                    .Where(w => w.SessionId == t.Id && w.MachineId != null)
                    .OrderByDescending(w => w.CreatedAt)
                    .ThenByDescending(w => w.Id)
                    .Select(w => w.MachineId)
                    .FirstOrDefault()))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Records a live endpoint for a working task (§8.2). Only the incumbent
    /// worker of a task that is currently working may register — the same
    /// authority check as a state transition, though registration is not one.
    /// "Register after a successful bind" is the worker's discipline (§8.2);
    /// the store only records what it's told.
    ///
    /// <para><b>A name is an address, so it is registered once</b>
    /// (<see cref="Rule.ServiceNameUniqueInTeam"/>). Two registrations used to be able to
    /// hold one <c>(Team, name)</c> — the write was an
    /// unconditional insert — and every resolver is handed a name and a Team and nothing
    /// else, so which port a forward reached became a raffle between duplicate rows. Two
    /// different situations were hiding behind that one insert, and they get different
    /// answers:</para>
    /// <list type="bullet">
    ///   <item><b>The same task re-registering its own name</b> — a service restarted on a
    ///   fresh port, or a re-register after a bind retry — <b>updates</b> the row it already
    ///   owns. The worker is correcting its own advertisement, and its old port is by then
    ///   exactly the stale target §8.2's dial hazard is about.</item>
    ///   <item><b>Another task in the Team claiming a name that is live</b> is
    ///   <b>refused</b>. Silently taking it over would redirect the holder's consumers
    ///   mid-flight, and silently ignoring it would leave the second worker believing it had
    ///   advertised something. Refusing tells it the name is taken so it can pick another.
    ///   The row it collides with is always live: registrations are deleted when their task
    ///   leaves <c>working</c>, so a finished task's name is free again.</item>
    /// </list>
    /// <para>The unique index on <c>(team_id, name)</c> is what makes this an invariant
    /// rather than a check — two concurrent registrations of one name cannot both land, and
    /// the loser surfaces as the same refusal below on its retry.</para>
    /// </summary>
    public async Task<StoreResult> RegisterServiceAsync(
        WorkerCaller caller, string name, int port, CancellationToken ct = default)
    {
        var row = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == caller.Session.Value, ct);
        if (row is null)
            return new StoreResult.NotFound($"no task {caller.Session}");
        if (row.State != SessionState.Working)
            return new StoreResult.Rejected(Rule.InvalidSourceState,
                $"services register only while working, not {row.State}");
        if (row.TeamId != caller.Team.Value || row.CurrentInstanceId != caller.Instance.Value)
            return new StoreResult.Rejected(Rule.IncumbentInstanceOnly,
                "only the incumbent worker of this task may register a service");

        // Tracked, not AsNoTracking: the same-task arm below updates this row.
        var existing = await db.RegisteredServices
            .FirstOrDefaultAsync(s => s.TeamId == caller.Team.Value && s.Name == name, ct);
        if (existing is not null && existing.SessionId != caller.Session.Value)
            return new StoreResult.Rejected(Rule.ServiceNameUniqueInTeam,
                $"service '{name}' is already registered in your Team by another task; " +
                "pick a name nothing else holds");

        if (existing is not null)
        {
            existing.Port = port;
            existing.CreatedAt = clock.GetUtcNow();
        }
        else
        {
            db.RegisteredServices.Add(new RegisteredServiceRow
            {
                SessionId = caller.Session.Value,
                TeamId = caller.Team.Value,
                Name = name,
                Port = port,
                CreatedAt = clock.GetUtcNow(),
            });
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // A concurrent worker claimed the name between the read above and this insert.
            // The unique index made the loss safe; report it as the same refusal a
            // sequential second caller would have got (the only rows this save writes are
            // registered_services, so this is that constraint).
            db.ChangeTracker.Clear();
            return new StoreResult.Rejected(Rule.ServiceNameUniqueInTeam,
                $"service '{name}' was registered by another task in your Team just now; " +
                "pick a name nothing else holds");
        }
        return new StoreResult.Applied(row.ToDomain(), []);
    }

    /// <summary>
    /// The worker's own assignment (§7, worker-skill.md): the prose description,
    /// workspace, namespace, and attempt count a dispatched worker reads before
    /// starting. A pure read gated by the same authority as a worker transition —
    /// returned <b>only</b> for the caller's own task and <b>only</b> while the
    /// caller is that task's incumbent instance (the RegisterServiceAsync gate,
    /// §9 check 14). Anything else returns null, so a zombie or a cross-task
    /// token learns nothing — never another task's content.
    /// </summary>
    public async Task<WorkerAssignment?> GetAssignmentAsync(WorkerCaller caller, CancellationToken ct = default)
    {
        var row = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == caller.Session.Value, ct);
        if (row is null)
            return null;
        if (row.TeamId != caller.Team.Value || row.CurrentInstanceId != caller.Instance.Value)
            return null;

        return new WorkerAssignment(
            row.Namespace, row.Description, row.Workspace, row.Attempt,
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
        var rows = await db.Sessions.AsNoTracking()
            .Where(t => t.TeamId == team.Value)
            .Select(t => new
            {
                t.Id,
                t.Namespace,
                t.State,
                t.CompletionMode,
                t.Attempt,
                Parked = t.ParkMachine != null,
                t.ContinuesSessionId,
                t.CompletionProvenance,
                // §6/§9 check 7: the infrastructure story is structure, so it rides the
                // bulk read — the count and why the last requeue happened. On a canceled
                // task the reason is how a Lead tells the cap from a deliberate cancel.
                t.InfrastructureRequeues,
                t.LastRequeueReason,
                // §10: the bulk read carries only a flag that a report exists, never
                // the prose — the Lead fetches the text per task via get_session_report.
                HasReport = t.WorkerReport != null,
                // §10/§11 the same way for the worker's question: the KIND is typed
                // structure and rides along (it tells a Lead who can answer, which is
                // triage), but the question text does not — get_session_question pulls it.
                t.InputKind,
                HasQuestion = t.BlockedAt != null
                    && t.InputKind != null
                    && t.InputKind != InputRequestKind.Permission,
            })
            .ToListAsync(ct);

        var counts = rows
            .GroupBy(t => t.State)
            .ToDictionary(g => g.Key, g => g.Count());

        var summaries = rows
            .Select(t => new TeamSessionSummary(
                t.Id, t.Namespace, t.State, t.CompletionMode, t.Attempt, t.Parked,
                t.ContinuesSessionId, t.CompletionProvenance, t.HasReport,
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
    /// view with a null <see cref="SessionReportView.Report"/> means the task is the
    /// Lead's but the worker left no report. A pure read; no transition.
    /// </summary>
    public async Task<SessionReportView?> GetSessionReportAsync(TeamId team, SessionId task, CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
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
            .Select(t => new SessionReportView(
                t.Id, t.Namespace, t.WorkerReport, t.ResultReference,
                t.InfrastructureRequeues, t.InfrastructureRequeueLimit, t.LastRequeueReason))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The Lead's deliberate per-task question fetch (§10/§11, §13) — the read half of
    /// the human-in-the-loop channel, shaped exactly like
    /// <see cref="GetSessionReportAsync"/>: the worker's opaque question for one task,
    /// pulled one item at a time rather than riding the bulk
    /// <see cref="GetTeamStateAsync"/> read (which carries the typed kind and a flag,
    /// never the prose). It returns the answer already given alongside, so a Lead — or
    /// a fresh one after a takeover (§4) — can see whether the question is still open
    /// before answering it twice. Team-scoped in the query, so a task in another Team,
    /// or no task at all, returns null: indistinguishable, leaking nothing (§13). A
    /// non-null view with a null question means the task is the Lead's but nothing was
    /// asked. A pure read; no transition.
    /// </summary>
    public async Task<SessionQuestionView?> GetSessionQuestionAsync(TeamId team, SessionId task, CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
            .Where(t => t.Id == task.Value && t.TeamId == team.Value)
            .Select(t => new SessionQuestionView(
                t.Id, t.Namespace, t.State, t.InputKind, t.InputQuestion, t.InputAnswer,
                t.PermissionTool, t.PermissionVerdict, t.PermissionEscalationReason))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The wait-TTL sweeper's poll (§11): every task in
    /// <see cref="SessionState.BlockedOnInput"/> with the timestamp it entered that
    /// state (<see cref="SessionRow.BlockedAt"/>) and its current attempt. A pure
    /// read — no transition, no prose. The sweeper ages <c>BlockedAt</c> against
    /// the configured wait TTL and cross-references the connection registry for
    /// the dispatched machine's liveness, then expresses the outcome as a
    /// <see cref="WaitTtlExpired"/> or <see cref="LivenessLost"/> command through
    /// the store — the engine, never raw SQL, owns every transition (§15).
    /// </summary>
    public async Task<IReadOnlyList<BlockedSessionView>> ListBlockedAsync(CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
            .Where(t => t.BlockedAt != null
                && (t.State == SessionState.BlockedOnInput
                    || (t.State == SessionState.Working && t.InputKind != null
                        && t.InputKind != InputRequestKind.Permission)))
            .OrderBy(t => t.BlockedAt)
            .Select(t => new BlockedSessionView(t.Id, t.BlockedAt, t.Attempt, t.HarnessSessionRef))
            .ToListAsync(ct);

    /// <summary>
    /// The dispatches a machine still holds according to committed state (§10, #86) —
    /// what <see cref="DispatchService.RehydrateMachineAsync"/> re-adopts when that
    /// machine reconnects, so a plane restart no longer strands in-flight work.
    ///
    /// <para>Both live states are included: <see cref="SessionState.Working"/>, and
    /// <see cref="SessionState.BlockedOnInput"/> — a blocked task's harness process is
    /// expected to be gone but the machine still holds its lease until the wait-TTL
    /// sweeper parks it or the machine dies (§11), and the sweeper resolves that machine
    /// through the registry, so it has to be re-adopted too or a blocked task outlives a
    /// restart with nothing able to park or requeue it.</para>
    ///
    /// <para><b>Fenced on the current worker instance</b> (§9.14), which is what keeps
    /// re-adoption from resurrecting the wrong dispatch. The row's
    /// <see cref="SessionRow.CurrentInstanceId"/> names the one incumbent attempt, and the
    /// instance row carries the machine it was minted for — so a task is re-adopted only
    /// by the machine its live incumbent instance actually runs on. The two exclusions
    /// that matters for: a requeue nulls <c>CurrentInstanceId</c> and revokes the
    /// instance, so a task already freed by a disconnect is never re-adopted (which is
    /// what makes a flapping machine cost exactly one requeue per disconnect, #87); and
    /// a task whose incumbent runs on a different machine is never adopted by this one,
    /// so a stale instance's events keep landing on an untracked task and are still
    /// refused rather than reviving a dispatch that has moved on.</para>
    /// </summary>
    public async Task<IReadOnlyList<SessionId>> HeldDispatchesOnAsync(
        string machineId, CancellationToken ct = default)
    {
        var ids = await db.Sessions.AsNoTracking()
            .Where(t => (t.State == SessionState.Working || t.State == SessionState.BlockedOnInput)
                && db.WorkerInstances.Any(w =>
                    w.Id == t.CurrentInstanceId && !w.Revoked && w.MachineId == machineId))
            .OrderBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(ct);
        return ids.Select(id => new SessionId(id)).ToArray();
    }

    /// <summary>
    /// A pure read of a task's current state, or null if it does not exist. The
    /// dispatch loop and the runner event sink read state to decide whether an
    /// inbound runner event still bears on a working task — e.g. an
    /// <c>exited</c> after the worker already reported result is moot (§10).
    /// </summary>
    public async Task<SessionState?> GetStateAsync(SessionId id, CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
            .Where(t => t.Id == id.Value)
            .Select(t => (SessionState?)t.State)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// True when a <c>working</c> task has a pending question (not a permission
    /// wait). A turn ending then is idle-correct, not a silent death.
    /// </summary>
    public async Task<bool> IsAwaitingLeadAsync(SessionId id, CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
            .AnyAsync(t => t.Id == id.Value
                && t.State == SessionState.Working
                && t.BlockedAt != null
                && t.InputKind != null
                && t.InputKind != InputRequestKind.Permission, ct);

    /// <summary>
    /// The state read of <see cref="GetStateAsync"/> plus the instance currently working the
    /// task — the pair the §10 per-task liveness scan decides against
    /// (<see cref="DispatchService.CheckLivenessAsync"/>). Null when the task is gone.
    ///
    /// <para>Two columns rather than one because the scan's requeue is fenced on the attempt
    /// it judged (§9 check 14, <see cref="LivenessLost.Instance"/>): the instance has to come
    /// from the same read as the state, or the command would carry an incumbent the scan
    /// never actually saw beside a <c>working</c>. A pure read; the fence itself is the
    /// engine's.</para>
    /// </summary>
    public async Task<IncumbentDispatchView?> GetIncumbentDispatchAsync(
        SessionId id, CancellationToken ct = default) =>
        await db.Sessions.AsNoTracking()
            .Where(t => t.Id == id.Value)
            .Select(t => new IncumbentDispatchView(
                t.State,
                t.CurrentInstanceId == null ? null : new WorkerInstanceId(t.CurrentInstanceId.Value)))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Whether this task has registered a service (§8.2) — i.e. declared that
    /// something it started is meant to stay reachable. Read by the §10 per-task
    /// liveness scan: a service-bearing task is exempt from the no-progress ceiling,
    /// because sitting idle while others use its service is the job, not a hang. It
    /// is deliberately this fact and not a flag on <c>create_session</c>: the worker
    /// earns the exemption by a deliberate, observable protocol act at the moment it
    /// becomes true, rather than the Lead predicting it before any work has happened
    /// (§2 principle 2 — derive, do not ask).
    /// </summary>
    public Task<bool> HasRegisteredServiceAsync(SessionId id, CancellationToken ct = default) =>
        db.RegisteredServices.AsNoTracking().AnyAsync(s => s.SessionId == id.Value, ct);

    /// <summary>
    /// Stamps the opaque harness session ref onto a task row (§11 resume), from a
    /// <see cref="Landbridge.Contracts.SessionStartedEvent"/> the runner event sink
    /// received. Not a state transition — this is transport metadata the plane
    /// never interprets (like <see cref="SessionRow.ResultReference"/>/<c>TraceContext</c>),
    /// so it is a targeted set-based write that runs no engine transition, takes no
    /// xmin token, and fires no NOTIFY — exactly the shape of the token-revoke
    /// effect. Latest write wins: a resumed or restarted session overwrites the ref
    /// so a subsequent park carries the current session. A no-op row count when the
    /// task no longer exists, which is fine — the ref is only ever read on dispatch.
    /// </summary>
    public async Task StampHarnessSessionRefAsync(SessionId id, string sessionRef, CancellationToken ct = default) =>
        await db.Sessions
            .Where(t => t.Id == id.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.HarnessSessionRef, sessionRef), ct);

    /// <summary>
    /// The machine of this task's most recent dispatch — durable on the instance
    /// row, so a fail that never wrote a park record can still pin session/load.
    /// </summary>
    private string? LastMachineOf(Guid sessionId) =>
        db.WorkerInstances.Local
            .Where(w => w.SessionId == sessionId && w.MachineId != null)
            .OrderByDescending(w => w.CreatedAt)
            .ThenByDescending(w => w.Id)
            .Select(w => w.MachineId)
            .FirstOrDefault()
        ?? db.WorkerInstances
            .Where(w => w.SessionId == sessionId && w.MachineId != null)
            .OrderByDescending(w => w.CreatedAt)
            .ThenByDescending(w => w.Id)
            .Select(w => w.MachineId)
            .FirstOrDefault();

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
        SessionId task, string operation, string target, string errorCode, string? missingScope,
        CancellationToken ct = default)
    {
        var teamId = await db.Sessions.AsNoTracking()
            .Where(t => t.Id == task.Value)
            .Select(t => (Guid?)t.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is null)
            return;

        db.SessionEvents.Add(new SessionEventRow
        {
            SessionId = task.Value,
            TeamId = teamId.Value,
            Kind = SessionEventRow.AuthFailedKind,
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
        SessionId task, string? agentId, string? parentAgentId, CancellationToken ct = default)
    {
        var teamId = await db.Sessions.AsNoTracking()
            .Where(t => t.Id == task.Value)
            .Select(t => (Guid?)t.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is null)
            return;

        db.SessionEvents.Add(new SessionEventRow
        {
            SessionId = task.Value,
            TeamId = teamId.Value,
            Kind = SessionEventRow.SubagentSpawnedKind,
            SubagentId = agentId,
            SubagentParentId = parentAgentId,
            OccurredAt = clock.GetUtcNow(),
        });
        await CommitAsync(task.Value, ct);
    }

    /// <summary>
    /// Records a harness's own usage report for one (task, model) pair (§10 telemetry ingest,
    /// §12 measured view). Out-of-band like <see cref="RecordAuthFailureAsync"/>: no transition,
    /// no xmin token, one NOTIFY so a listening dashboard wakes on it.
    ///
    /// <para><b>Upsert taking the high-water mark, because reports are cumulative.</b> Each
    /// report restates the dispatch's totals rather than adding to them, so keeping the larger
    /// value per counter makes this idempotent AND order-independent: §10's outbound ring is
    /// best-effort and may drop or reorder reports, and neither can make a total go backwards.
    /// A blind overwrite would regress on a reordered pair; a sum would multiply on a redelivered
    /// one. The cost of this shape is only staleness, never a wrong direction.</para>
    ///
    /// <para><b>Cost takes the newest non-null, not the maximum.</b> A cost is the harness's
    /// arithmetic over the same tokens rather than an independent counter, so a lower figure from
    /// a later report is a correction to honour, not a regression to clamp. It is left alone when
    /// a report carries none, so a harness that stops reporting cost mid-dispatch does not erase
    /// what it already stated.</para>
    ///
    /// <para>A no-op when the task is gone: the event carries only a task id (§10) and the Team
    /// is resolved from the row, and these rows exist solely to be read by the dashboard.</para>
    /// </summary>
    public async Task RecordUsageAsync(UsageReportedEvent report, CancellationToken ct = default)
    {
        var teamId = await db.Sessions.AsNoTracking()
            .Where(t => t.Id == report.Session.Value)
            .Select(t => (Guid?)t.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is null)
            return;

        // The empty string IS the unnamed model in storage: a composite primary key cannot
        // contain a NULL (LandbridgeDbContext explains the trade), and the view maps it back.
        var model = report.Model ?? "";
        var row = await db.SessionUsage
            .FirstOrDefaultAsync(u => u.SessionId == report.Session.Value && u.Model == model, ct);
        if (row is null)
        {
            row = new SessionUsageRow
            {
                SessionId = report.Session.Value,
                Model = model,
                TeamId = teamId.Value,
            };
            db.SessionUsage.Add(row);
        }

        row.InputTokens = Math.Max(row.InputTokens, report.InputTokens);
        row.OutputTokens = Math.Max(row.OutputTokens, report.OutputTokens);
        row.CacheReadTokens = Math.Max(row.CacheReadTokens, report.CacheReadTokens);
        row.CacheWriteTokens = Math.Max(row.CacheWriteTokens, report.CacheWriteTokens);
        if (report.ReasoningOutputTokens is { } reasoning)
            row.ReasoningOutputTokens = Math.Max(row.ReasoningOutputTokens ?? 0, reasoning);
        if (report.CostUsd is { } cost)
            row.CostUsd = cost;
        row.ReportedAt = clock.GetUtcNow();

        await CommitAsync(report.Session.Value, ct);
    }

    private async Task<StoreResult> RunTransition(
        SessionRow row, SessionCommand command, CancellationToken ct,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? outerTx = null)
    {
        var before = row.State;
        var result = SessionStateMachine.Apply(row.ToDomain(), command);
        if (result is TransitionResult.Rejected r)
            return new StoreResult.Rejected(r.Rule, r.Reason);

        var ok = (TransitionResult.Transitioned)result;
        row.CopyFrom(ok.Session);
        // #23, §7: the reported result reference is opaque content the store
        // captures on the working → verifying transition. The engine and
        // SessionRecord stay content-free (the reference never lands on the pure
        // state), and CopyFrom deliberately does not carry it — so a succeeding
        // ReportResult is the one place the row's ResultReference is written. It is
        // read back by the Lead's per-task get_session_report fetch and the §12
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
        if (command is RequestInput ri)
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
            // A permission ask is not a new prose question. Clearing InputAnswer
            // here used to wipe a WakeParked Lead note the moment Goose (in
            // approve mode) asked allow-once for get_session; the worker then
            // resumed seeing "e2e auto-allow" as its assignment answer.
            if (ri.Kind != InputRequestKind.Permission)
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
        else if (command is not EscalatePermission)
            // Leave BlockedAt only for a still-open wait: a new RequestInput
            // (stamped above) or an escalation (same permission request, still
            // blocked). Everything else — answer, continue, park, liveness loss,
            // report, cancel — has left the wait, even when the row stays working.
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
            // The permission note is on the event row (Detail), not InputAnswer.
            // InputAnswer is the Lead's prose to the worker (get_session). Mixing
            // them made approve-mode MCP tools overwrite a WakeParked answer.
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
        // pure record. The redispatched worker reads it back on get_session. Only a
        // command that actually carries words writes: clearing is the asking side's
        // job, so a wordless wake (an endpoint_wait's service appearing, a Lead
        // resuming a task it parked itself) never erases a live exchange.
        var answerText = command switch
        {
            AnswerInput answered => answered.Answer,
            ContinueSession continued => continued.Answer,
            LeadMessage spoken => spoken.Text,
            WakeParked woken => woken.Answer,
            _ => null,
        };
        if (answerText is not null)
            row.InputAnswer = answerText;

        // Same-task session/load is machine-local (stage 5; #175 is the later
        // move). A park or fail that does not pin PreferredMachine can be
        // claimed by any profile-matching box, and load then runs in the wrong
        // cwd. Continuations already carry their own preferred machine — leave
        // those alone. Pin, not Degrade: gone means wait, not a cold start.
        if (ok.Session.State is SessionState.Parked or SessionState.Failed
            && row.PreferredMachine is null)
        {
            var lastMachine = row.ParkMachine ?? LastMachineOf(row.Id);
            if (lastMachine is { Length: > 0 })
            {
                row.ParkMachine ??= lastMachine;
                row.PreferredMachine = lastMachine;
                row.OnMachineGone = MachineGonePolicy.Pin;
            }
        }

        // The transaction opens HERE, before the effects — not down in CommitAsync. Two of
        // the effects are set-based (§9.14's instance revoke, and the §8.2/§8.3 service and
        // relay-grant clearing): ExecuteUpdate/ExecuteDelete bypass the change tracker and
        // issue their SQL the moment ApplyEffects runs, rather than riding the SaveChanges
        // that persists the row. A transaction opened after them therefore did not contain
        // them, and the atomicity this class claims held only for the one caller that
        // supplies its own (DispatchNextAsync).
        //
        // No crash is needed to see it: SaveChanges losing on the row's xmin token returns
        // Conflict below, with the revoke already committed on its own. The caller then
        // re-reads a task whose row never moved — still working, its dispatch untouched —
        // while the instance working it has had its authorization destroyed, so the worker
        // 401s on every call it makes (§9 check 14) and the task sits working until a
        // liveness clock reclaims it. Opening first makes the effects and the row commit or
        // roll back together, which is what the summary above has always said.
        await using var ownTx = outerTx is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var teardown = ApplyEffects(row, ok.Effects);
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
            await CommitAsync(row.Id, ct, outerTx ?? ownTx);
            if (ownTx is not null)
                await ownTx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The `await using` above rolls ownTx back, taking the effects with it.
            return new StoreResult.Conflict($"task {row.Id} moved concurrently; re-read and retry");
        }

        // §8.3, after the commit and only after it: tell both machines to close the splices
        // the task just stopped being allowed to hold. Ordered this way because it is a
        // command, not a write — a close sent for a transition that then rolled back would
        // have severed a live session for nothing, where a close sent late costs only the
        // milliseconds between commit and send. Awaited rather than fired off so the wire
        // send is ordered before the caller is told the transition applied. (The one caller
        // that passes an outerTx — dispatch — reaches working and so emits no clearing
        // effect at all, which is why "after the commit" is true here and not only usually.)
        if (teardown.Count > 0 && forwards is not null)
            await forwards.CloseAsync(teardown, ct);

        return new StoreResult.Applied(ok.Session, ok.Effects);
    }

    /// <summary>
    /// Persist a transition's effects (§6) inside the caller's open transaction, and hand
    /// back the one consequence that is a <em>command</em> rather than a write: the live
    /// relay forwards <see cref="ClearServicesAndForwards"/> just released, for the caller to
    /// close once the transition has actually committed (§8.3). Empty for every other
    /// effect and for a task holding no forwards, which is the overwhelming majority.
    /// </summary>
    private IReadOnlyList<ForwardTeardown> ApplyEffects(SessionRow row, IReadOnlyList<Effect> effects)
    {
        IReadOnlyList<ForwardTeardown> teardown = [];
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case MintWorkerInstanceToken mint:
                    db.WorkerInstances.Add(new WorkerInstanceRow
                    {
                        Id = mint.Instance.Value,
                        SessionId = row.Id,
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
                    db.RegisteredServices.Where(s => s.SessionId == row.Id).ExecuteDelete();
                    // §8.3: leaving working also releases the task's relay forwards, and
                    // that takes both halves. Read the live ones FIRST — the revoke below
                    // is what makes them stop being live — so the post-commit close knows
                    // which forwards and which two ends to tell (ForwardTeardownService).
                    // Read HERE, inside the transition's transaction and next to the revoke,
                    // rather than again after the commit: this is the one moment the set is
                    // knowable, since a later read finds them all revoked and cannot tell
                    // them from any other task's — and a transition that loses the xmin race
                    // rolls this back with everything else, so nothing is closed for a
                    // transition that never happened.
                    teardown = db.RelayGrants
                        .Where(g => g.ProducerSessionId == row.Id && !g.Revoked)
                        .Select(g => new { g.ForwardId, g.ConsumerSessionId })
                        .ToList()
                        .Select(g => new ForwardTeardown(
                            new SessionId(row.Id), g.ForwardId.ToString(),
                            g.ConsumerSessionId is { } consumer ? new SessionId(consumer) : null))
                        .ToList();
                    // Revoke every live grant this task produced, so a grant issued
                    // against a now-gone service can never open a tunnel — the same
                    // moment its registered services are cleared, no schema churn. The
                    // revoke closes the door; close-forward ends what is already through
                    // it, since a grant only ever gated open.
                    db.RelayGrants
                        .Where(g => g.ProducerSessionId == row.Id && !g.Revoked)
                        .ExecuteUpdate(s => s.SetProperty(g => g.Revoked, true));
                    break;

                // WriteParkRecord is already reflected by CopyFrom (row park columns).
                // DiscardWorkspace / DeferWorkspaceDiscardUntilVerdict would be landbridged's to
                // enact, and nothing does (§11: "nothing enacts workspace discard today").
                // No §10 command carries a workspace discard and landbridged reads no event
                // details, so this arm is where the intent stops — a discard cancel and a
                // preserve cancel leave identical rows.
                case WriteParkRecord:
                case DiscardWorkspace:
                case DeferWorkspaceDiscardUntilVerdict:
                    break;
            }
        }

        return teardown;
    }

    private void AppendEvent(
        Guid sessionId, Guid teamId, string kind, SessionState? from, SessionState to, string? detail,
        InputRequestKind? inputKind = null, LivenessLossReason? livenessReason = null,
        PermissionVerdict? permissionVerdict = null, PermissionAnswerer? permissionAnswerer = null)
        => db.SessionEvents.Add(new SessionEventRow
        {
            SessionId = sessionId,
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

    /// <summary>
    /// Persists whatever the caller has staged and fires the task's NOTIFY in the same
    /// transaction, so subscribers wake only on committed writes.
    ///
    /// <para><paramref name="outerTx"/> is a transaction this method must not commit —
    /// someone above owns it and commits it (the SKIP LOCKED claim in
    /// <see cref="DispatchNextAsync"/>, or <see cref="RunTransition"/>'s own, opened before
    /// the effects run). Absent one — the out-of-band event recorders, which stage a single
    /// row and no effects — it opens and commits its own, since the write and the NOTIFY
    /// are two statements and a NOTIFY must not outlive a rolled-back insert.</para>
    /// </summary>
    private async Task CommitAsync(
        Guid sessionId, CancellationToken ct,
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
                $"SELECT pg_notify({LandbridgeDbContext.EventChannel}, {sessionId.ToString()})", ct);
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
