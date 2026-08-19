namespace Landbridge.Core;

/// <summary>
/// Spec §6, as a pure function. No clock, no IO, no interpretation of task
/// content — callers supply facts (timer expiries, machine snapshots, lease
/// status) as command fields, and side effects come back as data. Every
/// rejection names the §9 check or §6 invariant that refused it.
/// </summary>
public static class SessionStateMachine
{
    /// <summary>
    /// → submitted. The store supplies the server-assigned namespace (§9
    /// check 2) and the id; the engine gates on the creation checks.
    /// </summary>
    public static TransitionResult Create(CreateSession command, SessionId id, string serverAssignedNamespace)
    {
        if (command.Actor is not LeadClaim lead || lead.Team != command.Team)
            return TransitionResult.Reject(Rule.OnlyLeadCreatesSessions,
                "task creation requires a lead claim for this Team");

        if (string.IsNullOrWhiteSpace(command.Description))
            return TransitionResult.Reject(Rule.DescriptionNonEmpty,
                "description must be non-empty");

        if (string.IsNullOrWhiteSpace(command.Profile))
            return TransitionResult.Reject(Rule.ProfileRequired,
                "profile is required; name a profile list_profiles returned");

        if (string.IsNullOrWhiteSpace(serverAssignedNamespace))
            return TransitionResult.Reject(Rule.NamespaceServerAssigned,
                "namespace must be server-assigned before creation completes");

        // §6/§11 continuation targeting: the resolved facts ride the command as
        // opaque plane metadata (the store seeds them onto the row, never onto the
        // pure record below). Only two of them are the engine's to gate — the rest
        // it never dereferences (§2 principle 1).
        if (command.Continues is { } cont)
        {
            // Same-Team only: a continuation resumes a transcript that belongs to
            // one Team; addressing another Team's task is refused at creation.
            if (cont.ContinuedTeam != command.Team)
                return TransitionResult.Reject(Rule.ContinuationSameTeamOnly,
                    "continues must reference a task in the caller's Team");

            // If the preferred machine's declared profiles are known (it is
            // connected), the effective profile must be one it declares — otherwise
            // the continuation could never dispatch to the machine that holds its
            // transcript. When the machine is gone the set is null and the check is
            // skipped (dispatch's own profile routing still applies).
            var requiredProfile = command.Profile;
            if (cont.PreferredMachineProfiles is { } declared && !declared.Contains(requiredProfile))
                return TransitionResult.Reject(Rule.ContinuationProfileDeclaredByPreferredMachine,
                    $"preferred machine does not declare profile '{requiredProfile}' the continuation requires");
        }

        return TransitionResult.Ok(new SessionRecord
        {
            Id = id,
            Team = command.Team,
            Namespace = serverAssignedNamespace,
            Profile = command.Profile,
        });
    }

    public static TransitionResult Apply(SessionRecord task, SessionCommand command)
    {
        if (task.State.IsTerminal())
            return TransitionResult.Reject(Rule.TerminalStatesAreFinal,
                $"{task.State} is terminal and never resumed");

        return command switch
        {
            Dispatch c => ApplyDispatch(task, c),
            LivenessLost c => ApplyLivenessLost(task, c),
            ReportResult c => ApplyReportResult(task, c),
            VerdictAccept c => ApplyVerdict(task, c.Actor, accepted: true),
            VerdictFail c => ApplyVerdict(task, c.Actor, accepted: false),
            RequestInput c => ApplyRequestInput(task, c),
            AnswerInput c => ApplyAnswerInput(task, c),
            AnswerPermission c => ApplyAnswerPermission(task, c),
            EscalatePermission c => ApplyEscalatePermission(task, c),
            WaitTtlExpired c => ApplyWaitTtlExpired(task, c),
            Park c => ApplyPark(task, c),
            ContinueSession c => ApplyContinueSession(task, c),
            LeadMessage c => ApplyLeadMessage(task, c),
            WakeParked c => ApplyWakeParked(task, c),
            StopPreserveAndPark c => ApplyStopPreserveAndPark(task, c),
            Cancel c => ApplyCancel(task, c),
            CreateSession => TransitionResult.Reject(Rule.InvalidSourceState,
                "CreateSession applies to no existing record; use Create"),
            _ => TransitionResult.Reject(Rule.InvalidSourceState,
                $"unrecognized command {command.GetType().Name}"),
        };
    }

    private static TransitionResult ApplyDispatch(SessionRecord task, Dispatch c)
    {
        if (task.State != SessionState.Submitted)
            return WrongState(task, SessionState.Submitted);

        if (!c.Machine.Ready)
            return TransitionResult.Reject(Rule.MachineIneligibleForDispatch,
                $"machine {c.Machine.MachineId} is not ready");

        if (c.Machine.UnderBackPressure)
            return TransitionResult.Reject(Rule.MachineIneligibleForDispatch,
                $"machine {c.Machine.MachineId} is under back-pressure");

        var requiredProfile = task.Profile;
        if (string.IsNullOrWhiteSpace(requiredProfile)
            || !c.Machine.DeclaredProfiles.Contains(requiredProfile))
            return TransitionResult.Reject(Rule.MachineIneligibleForDispatch,
                $"machine {c.Machine.MachineId} does not declare profile '{requiredProfile}'");

        return TransitionResult.Ok(
            task with
            {
                State = SessionState.Working,
                CurrentInstance = c.NewInstance,
                Attempt = task.Attempt + 1,
            },
            new MintWorkerInstanceToken(c.NewInstance, c.Machine.MachineId));
    }

    private static TransitionResult ApplyLivenessLost(SessionRecord task, LivenessLost c)
    {
        if (task.State is not (SessionState.Working or SessionState.BlockedOnInput or SessionState.Verifying))
            return TransitionResult.Reject(Rule.InvalidSourceState,
                $"liveness loss applies to working, blocked_on_input, or verifying, not {task.State}");

        // §9 check 14: a loss that names the attempt it judged applies only while that
        // attempt is still working this task. The plane's per-dispatch clocks read the row,
        // decide, and then send this command, so this is where that read is re-checked
        // against committed state — and without it the requeue landed on whatever the row
        // held by the time it arrived. That is how a task which parked on a permission
        // request in between was requeued out from under a worker still alive inside its
        // tool call (the incumbent is deliberately kept there, §11), taking a §9 check 7
        // requeue and a kill with it; and how a redispatched successor was requeued for its
        // predecessor's silence. Machine death carries no instance and is untouched: it is a
        // fact about the machine rather than about one attempt, and still applies from
        // blocked_on_input.
        if (c.Instance is { } judged
            && (task.State is not (SessionState.Working or SessionState.Verifying)
                || task.CurrentInstance != judged))
            return TransitionResult.Reject(Rule.IncumbentInstanceOnly,
                $"liveness loss was decided about instance {judged}, which is no longer the "
                + $"incumbent of a live attempt (now {task.State}); the dispatch it judged has moved on");

        var effects = new List<Effect> { new ClearServicesAndForwards() };
        if (task.CurrentInstance is { } instance)
            effects.Insert(0, new RevokeWorkerInstanceToken(instance));

        // No automatic requeue. Failed is a park the Lead did not ask for: same
        // release (token gone, process gone, workspace kept), plane-authored
        // reason, inbox. Resume is WakeParked → session/load, with a note if
        // the Lead thinks the reason was flaky.
        return TransitionResult.Ok(
            task with
            {
                State = SessionState.Failed,
                CurrentInstance = null,
                InfrastructureRequeues = task.InfrastructureRequeues + 1,
                LastRequeueReason = c.Reason,
            },
            effects.ToArray());
    }

    private static TransitionResult ApplyReportResult(SessionRecord task, ReportResult c)
    {
        if (task.State != SessionState.Working)
            return WrongState(task, SessionState.Working);

        if (RequireIncumbent(task, c.Actor) is { } rejection)
            return rejection;

        if (string.IsNullOrWhiteSpace(c.ResultReference))
            return TransitionResult.Reject(Rule.ResultReferenceRequired,
                "working → verifying requires a result reference");

        // §10: the in-band report is bounded. Over-cap is refused here (a length
        // check, not content interpretation — the same shape as the non-empty checks
        // above) so a worker puts real detail in the workspace behind the result
        // reference, not in the plane.
        if (OverCap(c.Report, ReportResult.MaxReportBytes, Rule.ReportWithinSizeCap,
                "report",
                "keep it a summary and put the detail in the workspace behind the result reference")
            is { } tooLong)
            return tooLong;

        // The process stays. A report is "I think I am done", not a yield of the
        // machine — killing the ACP host would take a compile or a descendant
        // server with it. Services stay registered. The Lead accepts, replies
        // (LeadMessage), or parks.
        return TransitionResult.Ok(task with { State = SessionState.Verifying });
    }

    private static TransitionResult ApplyVerdict(SessionRecord task, Actor actor, bool accepted)
    {
        if (task.State != SessionState.Verifying)
            return WrongState(task, SessionState.Verifying);

        // §9 check 4 (doer/judge split): completion comes from a Lead or human
        // credential, NEVER the task's own worker — a WorkerCaller is refused here
        // structurally, exactly as a subagent never accepts its own work. The
        // plane trusts the Lead. Provenance is derived from the actor and
        // recorded on the completing transition (§12 dashboard).
        var provenance = actor switch
        {
            HumanSession => (VerdictProvenance?)VerdictProvenance.Human,
            LeadClaim lead when lead.Team == task.Team => VerdictProvenance.LeadSession,
            _ => null,
        };
        var authorized = provenance is VerdictProvenance.Human or VerdictProvenance.LeadSession;
        if (!authorized)
            return TransitionResult.Reject(Rule.CompletionByLeadOrHuman,
                "completion is a Lead or human verdict, never the task's own worker");

        var effects = new List<Effect> { new ClearServicesAndForwards() };
        if (task.CurrentInstance is { } instance)
            effects.Add(new RevokeWorkerInstanceToken(instance));

        if (accepted)
            return TransitionResult.Ok(
                task with { State = SessionState.Completed, CurrentInstance = null, CompletionProvenance = provenance },
                effects.ToArray());

        // A fail is not a redispatch. The assignment is rejected; the session
        // can still be resumed as a new piece of work. No verification-retry
        // loop — if the Lead wants more from this worker they reply
        // (LeadMessage) instead of failing.
        return TransitionResult.Ok(
            task with
            {
                State = SessionState.Rejected,
                VerificationFailures = task.VerificationFailures + 1,
                CurrentInstance = null,
            },
            effects.ToArray());
    }

    private static TransitionResult ApplyRequestInput(SessionRecord task, RequestInput c)
    {
        if (task.State != SessionState.Working)
            return WrongState(task, SessionState.Working);

        if (RequireIncumbent(task, c.Actor) is { } rejection)
            return rejection;

        if (c.Kind is null)
            return TransitionResult.Reject(Rule.TypedRequestKindRequired,
                "working → blocked_on_input requires a typed request kind");

        // §10/§11: the question is what the worker is asking, bounded exactly as the
        // report is. Refusing here leaves the task working, so a worker whose question
        // was too long asks again, shorter — it is never blocked with no ask attached.
        if (OverCap(c.Question, RequestInput.MaxQuestionBytes, Rule.QuestionWithinSizeCap,
                "question",
                "state the decision and the options, and point at the workspace for the detail")
            is { } tooLong)
            return tooLong;

        if (OverCap(c.PermissionOptions, RequestInput.MaxQuestionBytes, Rule.QuestionWithinSizeCap,
                "permission options",
                "the harness options array is too large to put on the plane")
            is { } optionsTooLong)
            return optionsTooLong;

        // §11 permission bridge: the tool awaiting approval must be named. A
        // non-emptiness check in the same class as DescriptionNonEmpty — the engine does
        // not recognize tool names and never will, it only refuses a permission request
        // that gives its answerer nothing to decide about.
        if (c.Kind == InputRequestKind.Permission && string.IsNullOrWhiteSpace(c.PermissionTool))
            return TransitionResult.Reject(Rule.PermissionRequestNamesItsTool,
                "a permission request must name the tool it is asking about");

        // Permission is the one live wait inside a tool call: the process cannot
        // take another turn until a verdict, so it leaves working. A question is
        // a turn, not a phase — the ACP session stays in working, idle for a
        // Lead follow-up (ideas/sessions.md stage 3). Services and the instance
        // stay either way. Park / wait-TTL / a dead-session AnswerInput are the
        // edges that release them.
        if (c.Kind == InputRequestKind.Permission)
            return TransitionResult.Ok(task with { State = SessionState.BlockedOnInput });

        return TransitionResult.Ok(task);
    }

    private static TransitionResult ApplyAnswerInput(SessionRecord task, AnswerInput c)
    {
        if (task.State is not (SessionState.BlockedOnInput or SessionState.Working))
            return WrongState(task, SessionState.BlockedOnInput);

        if (!IsLeadOrHuman(task, c.Actor))
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "input requests are answered by the Lead or a human");

        // §11 permission bridge: prose is not a verdict. This path revokes the incumbent's
        // token and requeues for redispatch, which is right for every kind whose worker has
        // already exited — and stranding for the one whose worker is still alive inside a
        // tool call waiting for allow or deny. Refused rather than approximated.
        if (c.PendingKind == InputRequestKind.Permission)
            return TransitionResult.Reject(Rule.PermissionVerdictAnswersPermissionRequests,
                "this task is waiting on a permission verdict, not prose; answer it with allow or deny");

        // §10/§11: the answer's text is bounded like the question it answers. Refused
        // over-cap, which leaves the task blocked_on_input — better a still-waiting
        // task the Lead re-answers than an unblocked one whose answer was dropped.
        if (OverCap(c.Answer, AnswerInput.MaxAnswerBytes, Rule.AnswerWithinSizeCap,
                "answer",
                "answer the decision and point at a reference for the detail")
            is { } tooLong)
            return tooLong;

        // §11: a headless worker that blocked has already ended its turn and its
        // process is gone — "resume does not restore in place". The answer therefore
        // drives blocked_on_input → submitted through the same park→redispatch path
        // the wait-TTL sweeper uses (never → working): revoke the predecessor
        // instance's token first (§5), write the park record so redispatch resumes
        // the transcript on the preferred machine (§11), and leave the infrastructure
        // counter untouched — a Lead answering is not an infrastructure requeue (§6,
        // two counters). A null park means the dispatched machine is gone; the task
        // still requeues and redispatch cold-starts elsewhere.
        var effects = new List<Effect> { new ClearServicesAndForwards() };
        if (task.CurrentInstance is { } instance)
            effects.Add(new RevokeWorkerInstanceToken(instance));
        if (c.Park is { } park)
            effects.Add(new WriteParkRecord(park));

        return TransitionResult.Ok(
            task with
            {
                State = SessionState.Submitted,
                CurrentInstance = null,
                Park = c.Park ?? task.Park,
            },
            effects.ToArray());
    }

    /// <summary>
    /// blocked_on_input → working, §11's permission bridge. The only transition back into
    /// <c>working</c> that is not a dispatch, and deliberately so: the asking process never
    /// left, so there is nothing to dispatch to — the incumbent instance and its token are
    /// carried through untouched and the worker resumes inside the tool call it blocked in.
    /// </summary>
    private static TransitionResult ApplyAnswerPermission(SessionRecord task, AnswerPermission c)
    {
        if (task.State != SessionState.BlockedOnInput)
        {
            // A question stays working; a verdict on it is the crossed-path
            // refusal, not a wrong-state one.
            if (task.State == SessionState.Working && c.PendingKind != InputRequestKind.Permission)
                return TransitionResult.Reject(Rule.PermissionVerdictAnswersPermissionRequests,
                    "a permission verdict answers a permission request; this task is waiting on "
                    + (c.PendingKind is { } kind ? $"{kind} input" : "input of no recorded kind"));
            return WrongState(task, SessionState.BlockedOnInput);
        }

        if (c.PendingKind != InputRequestKind.Permission)
            return TransitionResult.Reject(Rule.PermissionVerdictAnswersPermissionRequests,
                "a permission verdict answers a permission request; this task is waiting on "
                + (c.PendingKind is { } kind ? $"{kind} input" : "input of no recorded kind"));

        if (!IsLeadOrHuman(task, c.Actor))
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "permission requests are decided by the Lead or a human");

        // Escalation's whole content: the Lead gave up its authority over this one request
        // and said why. A human is unaffected — the dashboard answers escalated and
        // unescalated requests alike, because a human never needed the Lead's permission.
        if (c.EscalatedToHuman && c.Actor is LeadClaim)
            return TransitionResult.Reject(Rule.EscalatedPermissionIsHumanOnly,
                "this permission request was escalated to a human; a lead claim can no longer decide it");

        // Returning to working means returning to a worker. Without an incumbent there is
        // no one holding the tool call open, so the verdict has nowhere to land and the task
        // would go working with nothing running it — refused instead, leaving the request
        // pending for the sweeper to park.
        if (task.CurrentInstance is null)
            return TransitionResult.Reject(Rule.PermissionWaiterStillIncumbent,
                "the worker that asked for permission is no longer the incumbent; nothing is waiting for a verdict");

        // §11: a denial the agent cannot read is a wall it walks into again. The message is
        // the difference between "no" and "no, and here is what to do instead", so deny
        // carries one by rule rather than by convention.
        if (c.Verdict == PermissionVerdict.Deny && string.IsNullOrWhiteSpace(c.Message))
            return TransitionResult.Reject(Rule.PermissionDenialCarriesMessage,
                "a denial must carry a message: say why, and what the worker should do instead");

        if (OverCap(c.Message, AnswerPermission.MaxMessageBytes, Rule.AnswerWithinSizeCap,
                "message",
                "state the decision and what to do instead, and point at a reference for the detail")
            is { } tooLong)
            return tooLong;

        return TransitionResult.Ok(task with { State = SessionState.Working });
    }

    /// <summary>
    /// blocked_on_input → blocked_on_input, §11's permission bridge. An authority change
    /// rather than a state change: the task is still waiting on exactly the same request,
    /// but from here only a human may decide it.
    /// </summary>
    private static TransitionResult ApplyEscalatePermission(SessionRecord task, EscalatePermission c)
    {
        if (task.State != SessionState.BlockedOnInput)
            return WrongState(task, SessionState.BlockedOnInput);

        if (c.PendingKind != InputRequestKind.Permission)
            return TransitionResult.Reject(Rule.PermissionVerdictAnswersPermissionRequests,
                "only a permission request is escalated to a human this way");

        if (!IsLeadOrHuman(task, c.Actor))
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "permission requests are escalated by the Lead or a human");

        if (string.IsNullOrWhiteSpace(c.Reason))
            return TransitionResult.Reject(Rule.PermissionEscalationCarriesReason,
                "escalation requires a reason: the human decides without your context");

        if (OverCap(c.Reason, AnswerPermission.MaxMessageBytes, Rule.AnswerWithinSizeCap,
                "reason",
                "say what you could not justify from the task, and point at a reference for the detail")
            is { } tooLong)
            return tooLong;

        // The record is unchanged on purpose: state, incumbent, and park all still describe
        // a task blocked on the same request. What changed is stored beside the request by
        // the control plane, which is also where the wait deadline it does not reset lives —
        // escalating does not buy the human more time than the Lead had.
        return TransitionResult.Ok(task);
    }

    private static TransitionResult ApplyWaitTtlExpired(SessionRecord task, WaitTtlExpired c)
    {
        if (task.State is not (SessionState.BlockedOnInput or SessionState.Working))
            return WrongState(task, SessionState.BlockedOnInput);

        var effects = new List<Effect> { new WriteParkRecord(c.Park), new ClearServicesAndForwards() };
        if (task.CurrentInstance is { } instance)
            effects.Insert(0, new RevokeWorkerInstanceToken(instance));

        return TransitionResult.Ok(
            task with
            {
                State = SessionState.Parked,
                CurrentInstance = null,
                Park = c.Park,
            },
            effects.ToArray());
    }

    private static TransitionResult ApplyPark(SessionRecord task, Park c)
    {
        if (task.State is not (SessionState.Working or SessionState.BlockedOnInput or SessionState.Verifying))
            return WrongState(task, SessionState.Working);

        var authorized = c.Actor switch
        {
            HumanSession => true,
            LeadClaim lead => lead.Team == task.Team,
            _ => false,
        };
        if (!authorized)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "park is for the Lead of this Team or a human");

        var effects = new List<Effect> { new WriteParkRecord(c.Record), new ClearServicesAndForwards() };
        if (task.CurrentInstance is { } instance)
            effects.Insert(0, new RevokeWorkerInstanceToken(instance));

        return TransitionResult.Ok(
            task with
            {
                State = SessionState.Parked,
                CurrentInstance = null,
                Park = c.Record,
            },
            effects.ToArray());
    }

    private static TransitionResult ApplyContinueSession(SessionRecord task, ContinueSession c)
    {
        if (task.State is not (SessionState.BlockedOnInput or SessionState.Working))
            return WrongState(task, SessionState.BlockedOnInput);

        if (!IsLeadOrHuman(task, c.Actor))
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "input requests are answered by the Lead or a human");

        if (c.PendingKind == InputRequestKind.Permission)
            return TransitionResult.Reject(Rule.PermissionVerdictAnswersPermissionRequests,
                "this task is waiting on a permission verdict, not prose; answer it with allow or deny");

        if (OverCap(c.Answer, AnswerInput.MaxAnswerBytes, Rule.AnswerWithinSizeCap,
                "answer",
                "answer the decision and point at a reference for the detail")
            is { } tooLong)
            return tooLong;

        if (task.CurrentInstance is null)
            return TransitionResult.Reject(Rule.InvalidSourceState,
                "continue-session needs the incumbent still on the row");

        return TransitionResult.Ok(task with { State = SessionState.Working });
    }

    /// <summary>
    /// working → working: the Lead spoke without being asked. The session is
    /// live; this writes the words and doorbells. Not a dispatch and not a
    /// park. Permission still needs a verdict, not prose.
    /// </summary>
    private static TransitionResult ApplyLeadMessage(SessionRecord task, LeadMessage c)
    {
        if (task.State is not (SessionState.Working or SessionState.Verifying))
            return WrongState(task, SessionState.Working);

        if (!IsLeadOrHuman(task, c.Actor))
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "only the Lead of this Team or a human can message a live worker");

        if (c.PendingKind == InputRequestKind.Permission)
            return TransitionResult.Reject(Rule.PermissionVerdictAnswersPermissionRequests,
                "this task is waiting on a permission verdict, not prose; answer it with allow or deny");

        if (task.CurrentInstance is null)
            return TransitionResult.Reject(Rule.InvalidSourceState,
                "lead-message needs the incumbent still on the row");

        if (OverCap(c.Text, AnswerInput.MaxAnswerBytes, Rule.AnswerWithinSizeCap,
                "message",
                "say what the worker should do and point at a reference for the detail")
            is { } tooLong)
            return tooLong;

        return TransitionResult.Ok(task with { State = SessionState.Working });
    }

    private static TransitionResult ApplyWakeParked(SessionRecord task, WakeParked c)
    {
        if (task.State is not (SessionState.Parked or SessionState.Failed))
            return WrongState(task, SessionState.Parked);

        // Same cap as the blocked half: one answer path, one gate, so a Lead's answer
        // is accepted or refused identically whether or not the sweeper parked first.
        if (OverCap(c.Answer, AnswerInput.MaxAnswerBytes, Rule.AnswerWithinSizeCap,
                "answer",
                "answer the decision and point at a reference for the detail")
            is { } tooLong)
            return tooLong;

        // The park record survives into submitted: redispatch reads it for
        // machine/directory affinity (§11).
        return TransitionResult.Ok(task with { State = SessionState.Submitted });
    }

    private static TransitionResult ApplyStopPreserveAndPark(SessionRecord task, StopPreserveAndPark c)
    {
        if (task.State != SessionState.Working)
            return WrongState(task, SessionState.Working);

        if (c.Actor is not LeadClaim lead || lead.Team != task.Team)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "preserve_and_park is a Lead stop disposition");

        var effects = new List<Effect> { new ClearServicesAndForwards(), new WriteParkRecord(c.Park) };
        if (task.CurrentInstance is { } instance)
            effects.Insert(0, new RevokeWorkerInstanceToken(instance));

        return TransitionResult.Ok(
            task with
            {
                State = SessionState.Parked,
                CurrentInstance = null,
                Park = c.Park,
            },
            effects.ToArray());
    }

    private static TransitionResult ApplyCancel(SessionRecord task, Cancel c)
    {
        if (c.Disposition is null)
            return TransitionResult.Reject(Rule.CancellationCarriesDisposition,
                "cancellation carries a disposition enum");

        var authorized = c.Actor switch
        {
            // §6: cancelling is a judgement that the work should not continue, which is the
            // Lead's or a human's alone. The plane holds no such opinion — infrastructure
            // giving up lands as Failed (a park the Lead did not ask for), never as a
            // Cancel command.
            HumanSession => true,
            LeadClaim lead => lead.Team == task.Team,
            _ => false,
        };
        if (!authorized)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "cancellation is for the Lead of this Team or a human");

        var effects = new List<Effect>();
        if (task.CurrentInstance is { } instance)
            effects.Add(new RevokeWorkerInstanceToken(instance));
        if (task.State is SessionState.Working or SessionState.Verifying)
            effects.Add(new ClearServicesAndForwards());
        if (c.Disposition == CancelDisposition.Discard)
            effects.Add(task.State == SessionState.Verifying
                ? new DeferWorkspaceDiscardUntilVerdict()
                : new DiscardWorkspace());

        return TransitionResult.Ok(
            task with { State = SessionState.Canceled, CurrentInstance = null },
            effects.ToArray());
    }

    /// <summary>
    /// The one length gate every in-band prose field passes (§10): the worker's report,
    /// its question, and the Lead's answer. A byte count, never content interpretation —
    /// the engine still reads none of it (§2 principle 1) — measured in UTF-8 to match
    /// the wire. Returns the rejection to propagate, or null when the text fits (or is
    /// absent). <paramref name="advice"/> tells the caller where the detail belongs
    /// instead, since a bare "too long" leaves an agent no move but to retry blind.
    /// </summary>
    private static TransitionResult? OverCap(
        string? text, int maxBytes, Rule rule, string field, string advice) =>
        text is { } t && System.Text.Encoding.UTF8.GetByteCount(t) > maxBytes
            ? TransitionResult.Reject(rule,
                $"{field} exceeds the {maxBytes / 1024} KB in-band cap; {advice}")
            : null;

    private static TransitionResult? RequireIncumbent(SessionRecord task, Actor actor)
    {
        if (actor is not WorkerCaller worker)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "this transition is triggered by the working agent");

        if (worker.Session != task.Id || worker.Team != task.Team)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "worker token is scoped to a different task");

        if (task.CurrentInstance is not { } incumbent || worker.Instance != incumbent)
            return TransitionResult.Reject(Rule.IncumbentInstanceOnly,
                "worker-triggered transitions are accepted only from the incumbent instance");

        return null;
    }

    private static bool IsLeadOrHuman(SessionRecord task, Actor actor) => actor switch
    {
        HumanSession => true,
        LeadClaim lead => lead.Team == task.Team,
        _ => false,
    };

    private static TransitionResult WrongState(SessionRecord task, SessionState expected) =>
        TransitionResult.Reject(Rule.InvalidSourceState,
            $"transition applies to {expected}, not {task.State}");
}
