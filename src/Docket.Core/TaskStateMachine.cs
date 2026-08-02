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
            WaitTtlExpired c => ApplyWaitTtlExpired(task, c),
            WakeParked => ApplyWakeParked(task),
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
            new MintWorkerInstanceToken(c.NewInstance));
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

        return TransitionResult.Ok(
            task with
            {
                State = TaskState.Submitted,
                CurrentInstance = null,
                InfrastructureRequeues = task.InfrastructureRequeues + 1,
            },
            effects.ToArray());
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

        return TransitionResult.Ok(
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

    private static TransitionResult ApplyWakeParked(TaskRecord task)
    {
        if (task.State != TaskState.Parked)
            return WrongState(task, TaskState.Parked);

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
