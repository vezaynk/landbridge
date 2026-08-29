namespace Landbridge.Core.Tests;

/// <summary>Negative tests: one per §9 check the engine owns, plus §6 invariants.</summary>
public class EnforcementRuleTests
{
    // §9 check 1
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Creation_requires_non_empty_description(string description)
    {
        var result = SessionStateMachine.Create(
            new CreateSession(Given.Lead, Given.Team, description, "default"),
            Given.Id, "ns");
        Expect.Rejected(result, Rule.DescriptionNonEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Creation_requires_a_profile(string profile)
    {
        var result = SessionStateMachine.Create(
            new CreateSession(Given.Lead, Given.Team, "do the work", profile),
            Given.Id, "ns");
        Expect.Rejected(result, Rule.ProfileRequired);
    }

    // §9 check 2
    [Fact]
    public void Creation_requires_a_server_assigned_namespace()
    {
        var result = SessionStateMachine.Create(
            new CreateSession(Given.Lead, Given.Team, "criteria", "default"),
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
        var result = SessionStateMachine.Create(
            new CreateSession(actor, Given.Team, "criteria", "default"),
            Given.Id, "ns");
        Expect.Rejected(result, Rule.OnlyLeadCreatesSessions);
    }

    // §9 check 5 (machine-eligibility half)
    [Theory]
    [InlineData(false, false, "default")] // not ready
    [InlineData(true, true, "default")]   // back-pressure
    [InlineData(true, false, "gpu")]      // declares only 'gpu'; task wants default
    public void Dispatch_requires_an_eligible_machine(bool ready, bool backPressure, string declared)
    {
        var result = SessionStateMachine.Apply(
            Given.Session(SessionState.Submitted),
            new Dispatch(Given.Machine(ready, backPressure, declared), WorkerInstanceId.New()));
        Expect.Rejected(result, Rule.MachineIneligibleForDispatch);
    }

    [Fact]
    public void Dispatch_matches_a_requested_profile_by_exact_name()
    {
        var task = Given.Session(SessionState.Submitted, profile: "restricted");

        Expect.Rejected(
            SessionStateMachine.Apply(task, new Dispatch(Given.Machine(), WorkerInstanceId.New())),
            Rule.MachineIneligibleForDispatch);

        Expect.Transitioned(
            SessionStateMachine.Apply(task,
                new Dispatch(Given.Machine(true, false, "default", "restricted"), WorkerInstanceId.New())),
            SessionState.Working);
    }

    // §9 check 4 (doer/judge split) — a task's own worker can never complete it.
    [Fact]
    public void A_task_worker_cannot_complete_its_own_task()
    {
        var task = Given.Reported();
        var incumbent = new WorkerCaller(task.Team, task.Id, task.CurrentInstance!.Value);
        var result = SessionStateMachine.Apply(task, new VerdictAccept(incumbent));
        Expect.Rejected(result, Rule.CompletionByLeadOrHuman);
    }

    // §9 check 4 — the Lead session's verdict completes, and records lead-session provenance.
    [Fact]
    public void A_lead_completes_a_session()
    {
        var result = SessionStateMachine.Apply(
            Given.Reported(),
            new VerdictAccept(Given.Lead));
        var task = Expect.Transitioned(result, SessionState.Completed);
        Assert.Equal(VerdictProvenance.LeadSession, task.CompletionProvenance);
    }

    [Fact]
    public void A_human_session_completes_a_session_with_human_provenance()
    {
        var result = SessionStateMachine.Apply(
            Given.Reported(),
            new VerdictAccept(Given.Human));
        var task = Expect.Transitioned(result, SessionState.Completed);
        Assert.Equal(VerdictProvenance.Human, task.CompletionProvenance);
    }

    [Fact]
    public void The_control_plane_cannot_stop_a_session()
    {
        Expect.Rejected(
            SessionStateMachine.Apply(Given.Session(SessionState.Working),
                new StopSession(ControlPlaneActor.Instance)),
            Rule.CompletionByLeadOrHuman);
    }

    [Fact]
    public void A_foreign_team_lead_cannot_stop_a_session()
    {
        Expect.Rejected(
            SessionStateMachine.Apply(Given.Session(SessionState.Working),
                new StopSession(Given.ForeignLead)),
            Rule.CompletionByLeadOrHuman);
    }

    [Fact]
    public void Stop_from_a_report_hides_and_parks()
    {
        var result = SessionStateMachine.Apply(Given.Reported(), new StopSession(Given.Lead));
        var next = Expect.Transitioned(result, SessionState.Completed);
        Assert.True(next.Hidden);
        Assert.Equal(Occupancy.OnDisk, next.OccupancyDesired);
        Assert.DoesNotContain(new DiscardWorkspace(), Expect.Effects(result));
    }

    // §6 — terminal states are final
    public static TheoryData<SessionState, string> TerminalByCommand()
    {
        var data = new TheoryData<SessionState, string>();
        foreach (var state in new[] { SessionState.Completed, SessionState.Rejected, SessionState.Canceled })
        foreach (var command in CommandNames.Where(c => c != "wake"))
            data.Add(state, command);
        return data;
    }

    private static readonly string[] CommandNames =
        ["dispatch", "liveness", "report", "accept", "fail", "request", "answer", "continue", "message", "ttl", "wake", "stop-park", "park", "cancel", "stop"];

    private static SessionCommand CommandByName(string name, SessionRecord task) => name switch
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
        "stop" => new StopSession(Given.Lead),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(TerminalByCommand))]
    public void Terminal_states_refuse_every_command(SessionState terminal, string commandName)
    {
        var task = Given.Session(terminal, instance: null) with { CurrentInstance = null };
        var result = SessionStateMachine.Apply(task, CommandByName(commandName, task));
        Expect.Rejected(result, Rule.TerminalStatesAreFinal);
    }

    [Theory]
    [InlineData(SessionState.Completed)]
    [InlineData(SessionState.Rejected)]
    [InlineData(SessionState.Canceled)]
    public void Send_input_request_unhides_a_stopped_session(SessionState hidden)
    {
        var task = Given.Session(hidden, instance: null) with { CurrentInstance = null };
        var next = Expect.Transitioned(
            SessionStateMachine.Apply(task, new WakeParked("more")), SessionState.Working);
        Assert.False(next.Hidden);
        Assert.Equal(Occupancy.Running, next.OccupancyDesired);
        Assert.Equal(PendingSpawn.Load, next.PendingSpawn);
        Assert.Equal(MessageState.AwaitingPull, next.MessageState);
    }
}
