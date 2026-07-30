namespace Docket.Core.Tests;

/// <summary>Negative tests: one per §9 check the engine owns, plus §6 invariants.</summary>
public class EnforcementRuleTests
{
    // §9 check 1
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Creation_requires_non_empty_completion_criteria(string criteria)
    {
        var result = TaskStateMachine.Create(
            new CreateTask(Given.Lead, Given.Team, criteria, CompletionMode.Automated, null, true),
            Given.Id, "ns");
        Expect.Rejected(result, Rule.CompletionCriteriaNonEmpty);
    }

    // §9 check 2
    [Fact]
    public void Creation_requires_a_server_assigned_namespace()
    {
        var result = TaskStateMachine.Create(
            new CreateTask(Given.Lead, Given.Team, "criteria", CompletionMode.Automated, null, true),
            Given.Id, "");
        Expect.Rejected(result, Rule.NamespaceServerAssigned);
    }

    // §9 check 3
    public static TheoryData<Actor> NonLeadActors() => new()
    {
        Given.Human,
        Given.Verifier,
        new WorkerCaller(Given.Team, Given.Id, WorkerInstanceId.New()),
        Given.ForeignLead,
    };

    [Theory]
    [MemberData(nameof(NonLeadActors))]
    public void Only_a_lead_claim_for_the_team_creates_tasks(Actor actor)
    {
        var result = TaskStateMachine.Create(
            new CreateTask(actor, Given.Team, "criteria", CompletionMode.Automated, null, true),
            Given.Id, "ns");
        Expect.Rejected(result, Rule.OnlyLeadCreatesTasks);
    }

    // §9 check 9 (creation gate)
    [Fact]
    public void Creation_is_refused_when_the_team_budget_is_exhausted()
    {
        var result = TaskStateMachine.Create(
            new CreateTask(Given.Lead, Given.Team, "criteria", CompletionMode.Automated, null,
                TeamBudgetRemains: false),
            Given.Id, "ns");
        Expect.Rejected(result, Rule.TeamBudgetCeiling);
    }

    // §9 check 5 (machine-eligibility half)
    [Theory]
    [InlineData(false, false, "default")] // not ready
    [InlineData(true, true, "default")]   // back-pressure
    [InlineData(true, false, "gpu")]      // declares only 'gpu'; task wants default
    public void Dispatch_requires_an_eligible_machine(bool ready, bool backPressure, string declared)
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Submitted),
            new Dispatch(Given.Machine(ready, backPressure, declared), WorkerInstanceId.New()));
        Expect.Rejected(result, Rule.MachineIneligibleForDispatch);
    }

    [Fact]
    public void Dispatch_matches_a_requested_profile_by_exact_name()
    {
        var task = Given.Task(TaskState.Submitted, profile: "restricted");

        Expect.Rejected(
            TaskStateMachine.Apply(task, new Dispatch(Given.Machine(), WorkerInstanceId.New())),
            Rule.MachineIneligibleForDispatch);

        Expect.Transitioned(
            TaskStateMachine.Apply(task,
                new Dispatch(Given.Machine(true, false, "default", "restricted"), WorkerInstanceId.New())),
            TaskState.Working);
    }

    // §9 check 4 — automated mode expects the verifier credential
    public static TheoryData<Actor, bool> NonVerifierVerdicts() => new()
    {
        { Given.Human, false },
        { Given.Lead, true },   // even human-confirmed: wrong door for automated
        { new WorkerCaller(Given.Team, Given.Id, WorkerInstanceId.New()), false },
    };

    [Theory]
    [MemberData(nameof(NonVerifierVerdicts))]
    public void Automated_verdicts_require_the_verifier_credential(Actor actor, bool humanConfirmed)
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Automated),
            new VerdictAccept(actor, humanConfirmed));
        Expect.Rejected(result, Rule.CompletionRequiresNonAgentVerdict);
    }

    // §9 check 4 + §7 — review mode requires human confirmation
    [Fact]
    public void A_lead_claim_alone_cannot_complete_a_review_task()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Review),
            new VerdictAccept(Given.Lead, HumanConfirmed: false));
        Expect.Rejected(result, Rule.CompletionRequiresNonAgentVerdict);
    }

    [Fact]
    public void A_human_confirmed_lead_verdict_completes_a_review_task()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Review),
            new VerdictAccept(Given.Lead, HumanConfirmed: true));
        Expect.Transitioned(result, TaskState.Completed);
    }

    [Fact]
    public void A_human_session_completes_a_review_task()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Review),
            new VerdictAccept(Given.Human));
        Expect.Transitioned(result, TaskState.Completed);
    }

    [Fact]
    public void The_verifier_credential_is_the_wrong_door_for_review_verdicts()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Review),
            new VerdictAccept(Given.Verifier));
        Expect.Rejected(result, Rule.CompletionRequiresNonAgentVerdict);
    }

    // §9 check 12
    [Fact]
    public void Cancellation_carries_a_disposition()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Working),
            new Cancel(Given.Lead, Disposition: null));
        Expect.Rejected(result, Rule.CancellationCarriesDisposition);
    }

    // §6 — the control plane cancels only on budget exhaustion, and budget is only its to invoke
    [Fact]
    public void The_control_plane_cancels_only_on_budget_exhaustion()
    {
        Expect.Rejected(
            TaskStateMachine.Apply(Given.Task(TaskState.Working),
                new Cancel(ControlPlaneActor.Instance, CancelDisposition.Preserve)),
            Rule.ActorLacksAuthority);

        Expect.Transitioned(
            TaskStateMachine.Apply(Given.Task(TaskState.Working),
                new Cancel(ControlPlaneActor.Instance, CancelDisposition.Budget)),
            TaskState.Canceled);
    }

    [Fact]
    public void A_lead_cannot_invoke_the_budget_disposition()
    {
        Expect.Rejected(
            TaskStateMachine.Apply(Given.Task(TaskState.Working),
                new Cancel(Given.Lead, CancelDisposition.Budget)),
            Rule.ActorLacksAuthority);
    }

    [Fact]
    public void A_foreign_team_lead_cannot_cancel()
    {
        Expect.Rejected(
            TaskStateMachine.Apply(Given.Task(TaskState.Working),
                new Cancel(Given.ForeignLead, CancelDisposition.Preserve)),
            Rule.ActorLacksAuthority);
    }

    // §11 — discard is deferred while verifying
    [Fact]
    public void Discard_during_verifying_defers_workspace_removal_until_the_verdict()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying),
            new Cancel(Given.Lead, CancelDisposition.Discard));

        Expect.Transitioned(result, TaskState.Canceled);
        var effects = Expect.Effects(result);
        Assert.Contains(new DeferWorkspaceDiscardUntilVerdict(), effects);
        Assert.DoesNotContain(new DiscardWorkspace(), effects);
    }

    [Fact]
    public void Discard_outside_verifying_removes_the_workspace()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Working),
            new Cancel(Given.Lead, CancelDisposition.Discard));

        Expect.Transitioned(result, TaskState.Canceled);
        Assert.Contains(new DiscardWorkspace(), Expect.Effects(result));
    }

    // §6 — terminal states are final
    public static TheoryData<TaskState, string> TerminalByCommand()
    {
        var data = new TheoryData<TaskState, string>();
        foreach (var state in new[] { TaskState.Completed, TaskState.Rejected, TaskState.Canceled })
        foreach (var command in CommandNames)
            data.Add(state, command);
        return data;
    }

    private static readonly string[] CommandNames =
        ["dispatch", "liveness", "report", "accept", "fail", "request", "answer", "ttl", "wake", "stop-park", "cancel"];

    private static TaskCommand CommandByName(string name, TaskRecord task) => name switch
    {
        "dispatch" => new Dispatch(Given.Machine(), WorkerInstanceId.New()),
        "liveness" => new LivenessLost(LivenessLossReason.LivenessTimeout),
        "report" => new ReportResult(new WorkerCaller(task.Team, task.Id, WorkerInstanceId.New()), "ref"),
        "accept" => new VerdictAccept(Given.Verifier),
        "fail" => new VerdictFail(Given.Verifier),
        "request" => new RequestInput(new WorkerCaller(task.Team, task.Id, WorkerInstanceId.New()), InputRequestKind.Question),
        "answer" => new AnswerInput(Given.Lead, Given.Park),
        "ttl" => new WaitTtlExpired(Given.Park),
        "wake" => new WakeParked(),
        "stop-park" => new StopPreserveAndPark(Given.Lead, Given.Park),
        "cancel" => new Cancel(Given.Lead, CancelDisposition.Preserve),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(TerminalByCommand))]
    public void Terminal_states_refuse_every_command(TaskState terminal, string commandName)
    {
        var task = Given.Task(terminal, instance: null) with { CurrentInstance = null };
        var result = TaskStateMachine.Apply(task, CommandByName(commandName, task));
        Expect.Rejected(result, Rule.TerminalStatesAreFinal);
    }
}
