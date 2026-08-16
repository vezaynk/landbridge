namespace Docket.Core.Tests;

/// <summary>
/// The §6 table read backwards: for each transition, the states it does
/// <em>not</em> apply from, and the callers who may not invoke it. These are the
/// arms that make the engine a state machine rather than a set of mutations — a
/// transition that quietly worked from the wrong state, or for the wrong actor,
/// would be indistinguishable from one that had no guard at all. Every case
/// asserts the specific <see cref="Rule"/>, because "it rejected" is a much
/// weaker claim than "it rejected for the documented reason".
/// </summary>
public class WrongSourceStateAndAuthorityTests
{
    /// <summary>
    /// A command outside the dispatch table. <see cref="TaskCommand"/> is an open
    /// hierarchy, so a caller in another assembly really can hand
    /// <see cref="TaskStateMachine.Apply"/> something it has never heard of; the
    /// engine names it and refuses rather than falling through to a default.
    /// </summary>
    private sealed record UnrecognizedCommand(Actor Actor) : TaskCommand(Actor);

    /// <summary>Every state a task can be in and still be moved at all (§6: terminals are final).</summary>
    public static TheoryData<TaskState> NonTerminalStates() =>
        new(TaskState.Submitted, TaskState.Working, TaskState.Verifying, TaskState.BlockedOnInput, TaskState.Parked);

    // ── Commands the dispatch table does not recognize ────────────────────────

    [Fact]
    public void Create_task_is_refused_against_an_existing_record()
    {
        // CreateTask belongs to TaskStateMachine.Create, which builds a record; there
        // is no meaning to replaying it over a task that already exists, and treating
        // it as a no-op Ok would report success for a command that did nothing.
        var create = new CreateTask(
            Given.Lead, Given.Team, "ship it", CompletionMode.Lead, Profile: null);

        Expect.Rejected(TaskStateMachine.Apply(Given.Task(), create), Rule.InvalidSourceState);
    }

    [Fact]
    public void An_unrecognized_command_is_refused_rather_than_ignored()
    {
        var result = TaskStateMachine.Apply(Given.Task(TaskState.Working), new UnrecognizedCommand(Given.Lead));

        Expect.Rejected(result, Rule.InvalidSourceState);
        // The rejection names the offending type, so an operator reading the trail
        // learns which command arrived rather than only that one did.
        var rejected = Assert.IsType<TransitionResult.Rejected>(result);
        Assert.Contains(nameof(UnrecognizedCommand), rejected.Reason);
    }

    // ── Wrong source state, per transition ────────────────────────────────────

    [Theory]
    [InlineData(TaskState.Working)]
    [InlineData(TaskState.Verifying)]
    [InlineData(TaskState.BlockedOnInput)]
    [InlineData(TaskState.Parked)]
    public void Dispatch_applies_only_from_submitted(TaskState state)
    {
        // §9 check 5's other half: a task already claimed is not re-claimable. The
        // store's SKIP LOCKED transaction is what makes the claim single, but the
        // engine must refuse a second dispatch even if one reaches it.
        var dispatch = new Dispatch(Given.Machine(), WorkerInstanceId.New());

        Expect.Rejected(TaskStateMachine.Apply(Given.Task(state), dispatch), Rule.InvalidSourceState);
    }

    [Theory]
    [InlineData(TaskState.Submitted)]
    [InlineData(TaskState.Verifying)]
    [InlineData(TaskState.Parked)]
    public void Liveness_loss_applies_only_from_working_or_blocked_on_input(TaskState state)
    {
        // Liveness is a property of a task with a live worker. A submitted task has
        // no worker to lose, a verifying one has already reported, and a parked one
        // released its lease on purpose — requeuing any of them on a liveness timer
        // would re-run work that was never lost.
        var lost = new LivenessLost(LivenessLossReason.LivenessTimeout);

        var result = TaskStateMachine.Apply(Given.Task(state), lost);

        Expect.Rejected(result, Rule.InvalidSourceState);
        var rejected = Assert.IsType<TransitionResult.Rejected>(result);
        Assert.Contains("working or blocked_on_input", rejected.Reason);
    }

    [Theory]
    [InlineData(TaskState.Submitted)]
    [InlineData(TaskState.Verifying)]
    [InlineData(TaskState.BlockedOnInput)]
    [InlineData(TaskState.Parked)]
    public void Report_result_applies_only_from_working(TaskState state)
    {
        var task = Given.Task(state);
        var report = new ReportResult(Given.Lead, "artifact://build/42");

        Expect.Rejected(TaskStateMachine.Apply(task, report), Rule.InvalidSourceState);
    }

    [Theory]
    [InlineData(TaskState.Submitted)]
    [InlineData(TaskState.Working)]
    [InlineData(TaskState.BlockedOnInput)]
    [InlineData(TaskState.Parked)]
    public void Both_verdicts_apply_only_from_verifying(TaskState state)
    {
        // A verdict is a judgement on a submitted result. Accepting a task that has
        // not reported would complete work nobody looked at, so the state gate comes
        // before the §9 check 4 identity gate.
        var task = Given.Task(state);

        Expect.Rejected(TaskStateMachine.Apply(task, new VerdictAccept(Given.Lead)), Rule.InvalidSourceState);
        Expect.Rejected(TaskStateMachine.Apply(task, new VerdictFail(Given.Lead)), Rule.InvalidSourceState);
    }

    [Theory]
    [InlineData(TaskState.Submitted)]
    [InlineData(TaskState.Verifying)]
    [InlineData(TaskState.Parked)]
    public void Answer_input_applies_only_from_blocked_on_input_or_working(TaskState state)
    {
        // A parked task is woken by WakeParked, not answered here.
        // Working is legal: a live session that asked a question stays working.
        var answer = new AnswerInput(Given.Lead, Given.Park, Answer: "use the second option");

        Expect.Rejected(TaskStateMachine.Apply(Given.Task(state), answer), Rule.InvalidSourceState);
    }

    [Theory]
    [InlineData(TaskState.Submitted)]
    [InlineData(TaskState.Verifying)]
    [InlineData(TaskState.BlockedOnInput)]
    [InlineData(TaskState.Parked)]
    public void Preserve_and_park_applies_only_from_working(TaskState state)
    {
        // preserve_and_park is a stop disposition for a running worker; a task that
        // is not working has nothing to preserve, and parking a blocked one would
        // bypass the wait-TTL sweeper that owns that transition (§11).
        var park = new StopPreserveAndPark(Given.Lead, Given.Park);

        Expect.Rejected(TaskStateMachine.Apply(Given.Task(state), park), Rule.InvalidSourceState);
    }

    /// <summary>
    /// §6: terminal states are final. Checked ahead of every per-transition gate,
    /// so the rule holds for the whole vocabulary rather than per handler.
    /// </summary>
    [Theory]
    [InlineData(TaskState.Completed)]
    [InlineData(TaskState.Rejected)]
    [InlineData(TaskState.Canceled)]
    public void No_transition_moves_a_terminal_task(TaskState terminal)
    {
        var task = Given.Task(terminal);

        foreach (var command in new TaskCommand[]
                 {
                     new Dispatch(Given.Machine(), WorkerInstanceId.New()),
                     new LivenessLost(LivenessLossReason.LivenessTimeout),
                     new ReportResult(Given.Lead, "artifact://x"),
                     new VerdictAccept(Given.Lead),
                     new VerdictFail(Given.Lead),
                     new AnswerInput(Given.Lead, Given.Park),
                     new ContinueSession(Given.Lead, "use staging"),
                     new StopPreserveAndPark(Given.Lead, Given.Park),
                     new Park(Given.Lead, Given.Park),
                     new Cancel(Given.Lead, CancelDisposition.Preserve),
                     new WakeParked(),
                 })
        {
            Expect.Rejected(TaskStateMachine.Apply(task, command), Rule.TerminalStatesAreFinal);
        }
    }

    // ── Authority: who may invoke a transition ───────────────────────────────

    /// <summary>
    /// The incumbent-worker gate (<see cref="Rule.IncumbentInstanceOnly"/>'s
    /// structural half): the two transitions a worker drives for itself are refused
    /// outright to anyone who is not a worker at all. A Lead reporting a result on
    /// its worker's behalf would put the doer and the judge in one credential, which
    /// is the split §9 check 4 exists to keep.
    /// </summary>
    [Fact]
    public void Worker_driven_transitions_refuse_a_caller_that_is_not_a_worker()
    {
        var task = Given.Task(TaskState.Working);

        foreach (var actor in new Actor[] { Given.Lead, Given.Human, ControlPlaneActor.Instance })
        {
            Expect.Rejected(
                TaskStateMachine.Apply(task, new ReportResult(actor, "artifact://build/42")),
                Rule.ActorLacksAuthority);
            Expect.Rejected(
                TaskStateMachine.Apply(task, new RequestInput(actor, InputRequestKind.Question, "which option?")),
                Rule.ActorLacksAuthority);
        }
    }

    [Fact]
    public void Cancellation_refuses_a_worker_cancelling_its_own_task()
    {
        // The authority table names a human and the Team's Lead. A worker is neither:
        // a task that could cancel itself would let an agent retire work it was told
        // to do, and the incumbent is exactly the caller most likely to try.
        var task = Given.Task(TaskState.Working);
        var incumbent = Given.IncumbentOf(task);

        foreach (var disposition in new[] { CancelDisposition.Preserve, CancelDisposition.Discard })
        {
            Expect.Rejected(
                TaskStateMachine.Apply(task, new Cancel(incumbent, disposition)),
                Rule.ActorLacksAuthority);
        }
    }

    [Fact]
    public void Park_refuses_a_worker_parking_its_own_task()
    {
        var task = Given.Task(TaskState.Working);
        Expect.Rejected(
            TaskStateMachine.Apply(task, new Park(Given.IncumbentOf(task), Given.Park)),
            Rule.ActorLacksAuthority);
    }

    [Fact]
    public void Park_refuses_a_foreign_lead()
    {
        Expect.Rejected(
            TaskStateMachine.Apply(Given.Task(TaskState.Working), new Park(Given.ForeignLead, Given.Park)),
            Rule.ActorLacksAuthority);
    }

    [Fact]
    public void Cancellation_still_admits_the_two_authorized_callers()
    {
        // The negative cases above are only meaningful next to the positives: the
        // gate is narrow, not shut. Two callers, not three — the control plane's arm
        // existed for budget exhaustion alone and went with it (§6).
        var task = Given.Task(TaskState.Working);

        Expect.Transitioned(
            TaskStateMachine.Apply(task, new Cancel(Given.Lead, CancelDisposition.Preserve)), TaskState.Canceled);
        Expect.Transitioned(
            TaskStateMachine.Apply(task, new Cancel(Given.Human, CancelDisposition.Discard)), TaskState.Canceled);
    }

    [Fact]
    public void A_foreign_teams_lead_cancels_nothing_here()
    {
        // Team scoping on the authority check, not just on the record: holding a
        // lead claim is not enough, it has to be a claim for this task's Team.
        var task = Given.Task(TaskState.Working);

        Expect.Rejected(
            TaskStateMachine.Apply(task, new Cancel(Given.ForeignLead, CancelDisposition.Preserve)),
            Rule.ActorLacksAuthority);
    }
}
