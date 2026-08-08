namespace Docket.Core;

/// <summary>
/// Spec §6, as a pure function. No clock, no IO, no interpretation of task
/// content — callers supply facts (timer expiries, machine snapshots, lease
/// status) as command fields, and side effects come back as data. Every
/// rejection names the §9 check or §6 invariant that refused it.
/// </summary>
public static class TaskStateMachine
{
    /// <summary>
    /// → submitted. The store supplies the server-assigned namespace (§9
    /// check 2) and the id; the engine gates on the creation checks.
    /// </summary>
    public static TransitionResult Create(CreateTask command, TaskId id, string serverAssignedNamespace)
    {
        if (command.Actor is not LeadClaim lead || lead.Team != command.Team)
            return TransitionResult.Reject(Rule.OnlyLeadCreatesTasks,
                "task creation requires a lead claim for this Team");

        if (string.IsNullOrWhiteSpace(command.CompletionCriteria))
            return TransitionResult.Reject(Rule.CompletionCriteriaNonEmpty,
                "completion.criteria must be non-empty");

        if (string.IsNullOrWhiteSpace(serverAssignedNamespace))
            return TransitionResult.Reject(Rule.NamespaceServerAssigned,
                "namespace must be server-assigned before creation completes");

        if (!command.TeamBudgetRemains)
            return TransitionResult.Reject(Rule.TeamBudgetCeiling,
                "Team budget ceiling reached; no new tasks");

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
            var requiredProfile = command.Profile ?? MachineSnapshot.DefaultProfile;
            if (cont.PreferredMachineProfiles is { } declared && !declared.Contains(requiredProfile))
                return TransitionResult.Reject(Rule.ContinuationProfileDeclaredByPreferredMachine,
                    $"preferred machine does not declare profile '{requiredProfile}' the continuation requires");
        }

        return TransitionResult.Ok(new TaskRecord
        {
            Id = id,
            Team = command.Team,
            Namespace = serverAssignedNamespace,
            CompletionMode = command.Mode,
            Profile = command.Profile,
        });
    }

    public static TransitionResult Apply(TaskRecord task, TaskCommand command)
    {
        if (task.State.IsTerminal())
            return TransitionResult.Reject(Rule.TerminalStatesAreFinal,
                $"{task.State} is terminal and never resumed");

        return command switch
        {
            Dispatch c => ApplyDispatch(task, c),
            LivenessLost c => ApplyLivenessLost(task, c),
            ReportResult c => ApplyReportResult(task, c),
            VerdictAccept c => ApplyVerdict(task, c.Actor, c.HumanConfirmed, accepted: true),
            VerdictFail c => ApplyVerdict(task, c.Actor, c.HumanConfirmed, accepted: false),
            RequestInput c => ApplyRequestInput(task, c),
            AnswerInput c => ApplyAnswerInput(task, c),
            AnswerPermission c => ApplyAnswerPermission(task, c),
            EscalatePermission c => ApplyEscalatePermission(task, c),
            WaitTtlExpired c => ApplyWaitTtlExpired(task, c),
            WakeParked c => ApplyWakeParked(task, c),
            StopPreserveAndPark c => ApplyStopPreserveAndPark(task, c),
            Cancel c => ApplyCancel(task, c),
            CreateTask => TransitionResult.Reject(Rule.InvalidSourceState,
                "CreateTask applies to no existing record; use Create"),
            _ => TransitionResult.Reject(Rule.InvalidSourceState,
                $"unrecognized command {command.GetType().Name}"),
        };
    }

    private static TransitionResult ApplyDispatch(TaskRecord task, Dispatch c)
    {
        if (task.State != TaskState.Submitted)
            return WrongState(task, TaskState.Submitted);

        if (!c.Machine.Ready)
            return TransitionResult.Reject(Rule.MachineIneligibleForDispatch,
                $"machine {c.Machine.MachineId} is not ready");

        if (c.Machine.UnderBackPressure)
            return TransitionResult.Reject(Rule.MachineIneligibleForDispatch,
                $"machine {c.Machine.MachineId} is under back-pressure");

        var requiredProfile = task.Profile ?? MachineSnapshot.DefaultProfile;
        if (!c.Machine.DeclaredProfiles.Contains(requiredProfile))
            return TransitionResult.Reject(Rule.MachineIneligibleForDispatch,
                $"machine {c.Machine.MachineId} does not declare profile '{requiredProfile}'");

        return TransitionResult.Ok(
            task with
            {
                State = TaskState.Working,
                CurrentInstance = c.NewInstance,
                Attempt = task.Attempt + 1,
            },
            new MintWorkerInstanceToken(c.NewInstance, c.Machine.MachineId));
    }

    private static TransitionResult ApplyLivenessLost(TaskRecord task, LivenessLost c)
    {
        if (task.State is not (TaskState.Working or TaskState.BlockedOnInput))
            return TransitionResult.Reject(Rule.InvalidSourceState,
                $"liveness loss applies to working or blocked_on_input, not {task.State}");

        var effects = new List<Effect>();
        if (task.CurrentInstance is { } instance)
            effects.Add(new RevokeWorkerInstanceToken(instance));
        if (task.State == TaskState.Working)
            effects.Add(new ClearServicesAndForwards());

        // The reason rides onto the record so the requeue trail says WHICH signal fired
        // (#73): without it every requeue is indistinguishable, and a task looping on a
        // wedged machine reads exactly like one whose machine rebooted twice.
        var requeued = task with
        {
            CurrentInstance = null,
            InfrastructureRequeues = task.InfrastructureRequeues + 1,
            LastRequeueReason = c.Reason,
        };

        // §9 check 7: infrastructure requeues are capped. At the cap the task is
        // abandoned rather than dispatched again — terminal as `canceled`, the state §6
        // already gives the control plane for "this work was called off", and deliberately
        // NOT `rejected`: rejection is the verification counter's alone (§6, two
        // counters), and blaming the work for a machine that keeps failing it punishes the
        // wrong party. The workspace is preserved (no discard effect) — whatever wedged
        // every attempt is the evidence a human needs — and the reason that ended it stays
        // on the record for get_task_report/get_team_state.
        if (requeued.InfrastructureRequeuesExhausted)
            return TransitionResult.Ok(requeued with { State = TaskState.Canceled }, effects.ToArray());

        return TransitionResult.Ok(requeued with { State = TaskState.Submitted }, effects.ToArray());
    }

    private static TransitionResult ApplyReportResult(TaskRecord task, ReportResult c)
    {
        if (task.State != TaskState.Working)
            return WrongState(task, TaskState.Working);

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

        return TransitionResult.Ok(
            task with { State = TaskState.Verifying },
            new ClearServicesAndForwards());
    }

    private static TransitionResult ApplyVerdict(TaskRecord task, Actor actor, bool humanConfirmed, bool accepted)
    {
        if (task.State != TaskState.Verifying)
            return WrongState(task, TaskState.Verifying);

        // §9 check 4 (doer/judge split): completion comes from a Lead or human
        // credential, NEVER the task's own worker — a WorkerCaller is refused here
        // structurally, exactly as a subagent never accepts its own work. In `lead`
        // mode a Lead session adjudicates autonomously (orchestrator judgment); in
        // `review` mode a Lead verdict additionally carries human confirmation (§7),
        // while a human session completes either mode outright. The former verifier
        // credential is gone (§5): CI and tests are evidence the Lead gathers itself,
        // not a verdict-issuing actor. Provenance is derived from the actor and
        // recorded on the completing transition (§12 dashboard).
        var provenance = actor switch
        {
            HumanSession => (VerdictProvenance?)VerdictProvenance.Human,
            LeadClaim lead when lead.Team == task.Team => VerdictProvenance.LeadSession,
            _ => null,
        };
        var authorized = provenance switch
        {
            VerdictProvenance.Human => true,
            VerdictProvenance.LeadSession => task.CompletionMode != CompletionMode.Review || humanConfirmed,
            _ => false,
        };
        if (!authorized)
            return TransitionResult.Reject(Rule.CompletionByLeadOrHuman,
                actor is LeadClaim && task.CompletionMode == CompletionMode.Review
                    ? "review verdicts require human confirmation; a lead claim alone cannot complete a task"
                    : "completion is a Lead or human verdict, never the task's own worker");

        var revoke = task.CurrentInstance is { } instance
            ? new Effect[] { new RevokeWorkerInstanceToken(instance) }
            : [];

        if (accepted)
            return TransitionResult.Ok(
                task with { State = TaskState.Completed, CurrentInstance = null, CompletionProvenance = provenance },
                revoke);

        var failures = task.VerificationFailures + 1;
        if (failures >= task.VerificationRetryLimit)
            return TransitionResult.Ok(
                task with
                {
                    State = TaskState.Rejected,
                    VerificationFailures = failures,
                    CurrentInstance = null,
                },
                revoke);

        return TransitionResult.Ok(
            task with
            {
                State = TaskState.Submitted,
                VerificationFailures = failures,
                CurrentInstance = null,
            },
            revoke);
    }

    private static TransitionResult ApplyRequestInput(TaskRecord task, RequestInput c)
    {
        if (task.State != TaskState.Working)
            return WrongState(task, TaskState.Working);

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

        // §11 permission bridge: the tool awaiting approval must be named. A
        // non-emptiness check in the same class as CompletionCriteria — the engine does
        // not recognize tool names and never will, it only refuses a permission request
        // that gives its answerer nothing to decide about.
        if (c.Kind == InputRequestKind.Permission && string.IsNullOrWhiteSpace(c.PermissionTool))
            return TransitionResult.Reject(Rule.PermissionRequestNamesItsTool,
                "a permission request must name the tool it is asking about");

        // A permission request is the one blocked_on_input flavor the asking process
        // survives (§11): it is parked inside a live tool call, not gone. So it keeps its
        // registered services and relay forwards — tearing them down here would break a
        // worker mid-turn for asking a question, and it is about to return to working with
        // the same instance. Every other kind ends the turn, so leaving working releases
        // them as it always has.
        return c.Kind == InputRequestKind.Permission
            ? TransitionResult.Ok(task with { State = TaskState.BlockedOnInput })
            : TransitionResult.Ok(
                task with { State = TaskState.BlockedOnInput },
                new ClearServicesAndForwards());
    }

    private static TransitionResult ApplyAnswerInput(TaskRecord task, AnswerInput c)
    {
        if (task.State != TaskState.BlockedOnInput)
            return WrongState(task, TaskState.BlockedOnInput);

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
        var effects = new List<Effect>();
        if (task.CurrentInstance is { } instance)
            effects.Add(new RevokeWorkerInstanceToken(instance));
        if (c.Park is { } park)
            effects.Add(new WriteParkRecord(park));

        return TransitionResult.Ok(
            task with
            {
                State = TaskState.Submitted,
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
    private static TransitionResult ApplyAnswerPermission(TaskRecord task, AnswerPermission c)
    {
        if (task.State != TaskState.BlockedOnInput)
            return WrongState(task, TaskState.BlockedOnInput);

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

        return TransitionResult.Ok(task with { State = TaskState.Working });
    }

    /// <summary>
    /// blocked_on_input → blocked_on_input, §11's permission bridge. An authority change
    /// rather than a state change: the task is still waiting on exactly the same request,
    /// but from here only a human may decide it.
    /// </summary>
    private static TransitionResult ApplyEscalatePermission(TaskRecord task, EscalatePermission c)
    {
        if (task.State != TaskState.BlockedOnInput)
            return WrongState(task, TaskState.BlockedOnInput);

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

    private static TransitionResult ApplyWaitTtlExpired(TaskRecord task, WaitTtlExpired c)
    {
        if (task.State != TaskState.BlockedOnInput)
            return WrongState(task, TaskState.BlockedOnInput);

        var effects = new List<Effect> { new WriteParkRecord(c.Park) };
        if (task.CurrentInstance is { } instance)
            effects.Insert(0, new RevokeWorkerInstanceToken(instance));

        return TransitionResult.Ok(
            task with
            {
                State = TaskState.Parked,
                CurrentInstance = null,
                Park = c.Park,
            },
            effects.ToArray());
    }

    private static TransitionResult ApplyWakeParked(TaskRecord task, WakeParked c)
    {
        if (task.State != TaskState.Parked)
            return WrongState(task, TaskState.Parked);

        // Same cap as the blocked half: one answer path, one gate, so a Lead's answer
        // is accepted or refused identically whether or not the sweeper parked first.
        if (OverCap(c.Answer, AnswerInput.MaxAnswerBytes, Rule.AnswerWithinSizeCap,
                "answer",
                "answer the decision and point at a reference for the detail")
            is { } tooLong)
            return tooLong;

        // The park record survives into submitted: redispatch reads it for
        // machine/directory affinity (§11).
        return TransitionResult.Ok(task with { State = TaskState.Submitted });
    }

    private static TransitionResult ApplyStopPreserveAndPark(TaskRecord task, StopPreserveAndPark c)
    {
        if (task.State != TaskState.Working)
            return WrongState(task, TaskState.Working);

        if (c.Actor is not LeadClaim lead || lead.Team != task.Team)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "preserve_and_park is a Lead stop disposition");

        var effects = new List<Effect> { new ClearServicesAndForwards(), new WriteParkRecord(c.Park) };
        if (task.CurrentInstance is { } instance)
            effects.Insert(0, new RevokeWorkerInstanceToken(instance));

        return TransitionResult.Ok(
            task with
            {
                State = TaskState.Parked,
                CurrentInstance = null,
                Park = c.Park,
            },
            effects.ToArray());
    }

    private static TransitionResult ApplyCancel(TaskRecord task, Cancel c)
    {
        if (c.Disposition is null)
            return TransitionResult.Reject(Rule.CancellationCarriesDisposition,
                "cancellation carries a disposition enum");

        var authorized = c.Actor switch
        {
            // §6: the control plane may cancel only on budget exhaustion,
            // and budget exhaustion is only the control plane's to invoke.
            ControlPlaneActor => c.Disposition == CancelDisposition.Budget,
            HumanSession => c.Disposition != CancelDisposition.Budget,
            LeadClaim lead => lead.Team == task.Team && c.Disposition != CancelDisposition.Budget,
            _ => false,
        };
        if (!authorized)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "cancellation is for the Lead, a human, or the control plane on budget exhaustion");

        var effects = new List<Effect>();
        if (task.CurrentInstance is { } instance)
            effects.Add(new RevokeWorkerInstanceToken(instance));
        if (task.State == TaskState.Working)
            effects.Add(new ClearServicesAndForwards());
        if (c.Disposition == CancelDisposition.Discard)
            effects.Add(task.State == TaskState.Verifying
                ? new DeferWorkspaceDiscardUntilVerdict()
                : new DiscardWorkspace());

        return TransitionResult.Ok(
            task with { State = TaskState.Canceled, CurrentInstance = null },
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

    private static TransitionResult? RequireIncumbent(TaskRecord task, Actor actor)
    {
        if (actor is not WorkerCaller worker)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "this transition is triggered by the working agent");

        if (worker.Task != task.Id || worker.Team != task.Team)
            return TransitionResult.Reject(Rule.ActorLacksAuthority,
                "worker token is scoped to a different task");

        if (task.CurrentInstance is not { } incumbent || worker.Instance != incumbent)
            return TransitionResult.Reject(Rule.IncumbentInstanceOnly,
                "worker-triggered transitions are accepted only from the incumbent instance");

        return null;
    }

    private static bool IsLeadOrHuman(TaskRecord task, Actor actor) => actor switch
    {
        HumanSession => true,
        LeadClaim lead => lead.Team == task.Team,
        _ => false,
    };

    private static TransitionResult WrongState(TaskRecord task, TaskState expected) =>
        TransitionResult.Reject(Rule.InvalidSourceState,
            $"transition applies to {expected}, not {task.State}");
}
