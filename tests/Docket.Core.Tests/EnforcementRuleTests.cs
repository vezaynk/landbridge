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
            new CreateTask(Given.Lead, Given.Team, criteria, CompletionMode.Lead, null),
            Given.Id, "ns");
        Expect.Rejected(result, Rule.CompletionCriteriaNonEmpty);
    }

    // §9 check 2
    [Fact]
    public void Creation_requires_a_server_assigned_namespace()
    {
        var result = TaskStateMachine.Create(
            new CreateTask(Given.Lead, Given.Team, "criteria", CompletionMode.Lead, null),
            Given.Id, "");
        Expect.Rejected(result, Rule.NamespaceServerAssigned);
    }

    // §9 check 3
    public static TheoryData<Actor> NonLeadActors() => new()
    {
        Given.Human,
        new WorkerCaller(Given.Team, Given.Id, WorkerInstanceId.New()),
        Given.ForeignLead,
    };

    [Theory]
    [MemberData(nameof(NonLeadActors))]
    public void Only_a_lead_claim_for_the_team_creates_tasks(Actor actor)
    {
        var result = TaskStateMachine.Create(
            new CreateTask(actor, Given.Team, "criteria", CompletionMode.Lead, null),
            Given.Id, "ns");
        Expect.Rejected(result, Rule.OnlyLeadCreatesTasks);
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

    // §9 check 4 (doer/judge split) — a task's own worker can never complete it,
    // in either mode. This is the invariant that survives cutting the verifier.
    public static TheoryData<CompletionMode> BothModes() => new()
    {
        CompletionMode.Lead,
        CompletionMode.Review,
    };

    [Theory]
    [MemberData(nameof(BothModes))]
    public void A_task_worker_cannot_complete_its_own_task(CompletionMode mode)
    {
        var task = Given.Task(TaskState.Verifying, mode);
        var incumbent = new WorkerCaller(task.Team, task.Id, task.CurrentInstance!.Value);
        var result = TaskStateMachine.Apply(task, new VerdictAccept(incumbent));
        Expect.Rejected(result, Rule.CompletionByLeadOrHuman);
    }

    // §9 check 4 — lead mode: the Lead session's verdict completes autonomously,
    // no human confirmation, and the completion records lead-session provenance.
    [Fact]
    public void A_lead_completes_a_lead_mode_task_autonomously()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Lead),
            new VerdictAccept(Given.Lead, HumanConfirmed: false));
        var task = Expect.Transitioned(result, TaskState.Completed);
        Assert.Equal(VerdictProvenance.LeadSession, task.CompletionProvenance);
    }

    // §9 check 4 + §7 — review mode requires human confirmation
    [Fact]
    public void A_lead_claim_alone_cannot_complete_a_review_task()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Review),
            new VerdictAccept(Given.Lead, HumanConfirmed: false));
        Expect.Rejected(result, Rule.CompletionByLeadOrHuman);
    }

    [Fact]
    public void A_human_confirmed_lead_verdict_completes_a_review_task()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Review),
            new VerdictAccept(Given.Lead, HumanConfirmed: true));
        var task = Expect.Transitioned(result, TaskState.Completed);
        Assert.Equal(VerdictProvenance.LeadSession, task.CompletionProvenance);
    }

    [Fact]
    public void A_human_session_completes_a_review_task_with_human_provenance()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Verifying, CompletionMode.Review),
            new VerdictAccept(Given.Human));
        var task = Expect.Transitioned(result, TaskState.Completed);
        Assert.Equal(VerdictProvenance.Human, task.CompletionProvenance);
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

    // §6 — the control plane cannot cancel, on any disposition. It had exactly one
    // disposition it was allowed (budget exhaustion) and that went with the budget
    // subsystem; the plane's own giving-up path is the check 7 requeue cap inside
    // LivenessLost, which reaches `canceled` without a Cancel command.
    [Theory]
    [InlineData(CancelDisposition.Preserve)]
    [InlineData(CancelDisposition.Discard)]
    public void The_control_plane_cannot_cancel(CancelDisposition disposition)
    {
        Expect.Rejected(
            TaskStateMachine.Apply(Given.Task(TaskState.Working),
                new Cancel(ControlPlaneActor.Instance, disposition)),
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
        ["dispatch", "liveness", "report", "accept", "fail", "request", "answer", "continue", "message", "ttl", "wake", "stop-park", "park", "cancel"];

    private static TaskCommand CommandByName(string name, TaskRecord task) => name switch
    {
        "dispatch" => new Dispatch(Given.Machine(), WorkerInstanceId.New()),
        "liveness" => new LivenessLost(LivenessLossReason.LivenessTimeout),
        "report" => new ReportResult(new WorkerCaller(task.Team, task.Id, WorkerInstanceId.New()), "ref"),
        "accept" => new VerdictAccept(Given.Lead),
        "fail" => new VerdictFail(Given.Lead),
        "request" => new RequestInput(new WorkerCaller(task.Team, task.Id, WorkerInstanceId.New()), InputRequestKind.Question),
        "answer" => new AnswerInput(Given.Lead, Given.Park),
        "continue" => new ContinueSession(Given.Lead, "use staging"),
        "message" => new LeadMessage(Given.Lead, "keep going"),
        "ttl" => new WaitTtlExpired(Given.Park),
        "wake" => new WakeParked(),
        "stop-park" => new StopPreserveAndPark(Given.Lead, Given.Park),
        "park" => new Park(Given.Lead, Given.Park),
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
