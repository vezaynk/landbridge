namespace Landbridge.Core.Tests;

public sealed class PlanTests
{
    [Fact]
    public void Submit_opens_a_live_wait_and_keeps_the_instance()
    {
        var working = Given.Session(SessionState.Working);
        var instance = working.CurrentInstance;
        var next = Expect.Transitioned(
            SessionStateMachine.Apply(working, new SubmitPlan(Given.IncumbentOf(working), "run the tests")),
            SessionState.BlockedOnInput);
        Assert.Equal(MessageState.AwaitingPlan, next.MessageState);
        Assert.Equal(instance, next.CurrentInstance);
    }

    [Fact]
    public void Approve_returns_to_idle_without_revoking_the_instance()
    {
        var waiting = Given.Session(SessionState.Working, message: MessageState.AwaitingPlan);
        var instance = waiting.CurrentInstance;
        var next = Expect.Transitioned(
            SessionStateMachine.Apply(waiting, new AnswerPlan(Given.Lead, PlanVerdict.Approve, "ship it")),
            SessionState.Working);
        Assert.Equal(MessageState.Idle, next.MessageState);
        Assert.Equal(instance, next.CurrentInstance);
        Assert.Empty(Expect.Effects(
            SessionStateMachine.Apply(waiting, new AnswerPlan(Given.Lead, PlanVerdict.Approve))));
    }

    [Fact]
    public void Deny_without_a_message_stays_waiting()
    {
        var waiting = Given.Session(SessionState.Working, message: MessageState.AwaitingPlan);
        Expect.Rejected(
            SessionStateMachine.Apply(waiting, new AnswerPlan(Given.Lead, PlanVerdict.Deny)),
            Rule.PlanDenialCarriesMessage);
    }

    [Fact]
    public void A_second_submit_while_waiting_is_refused()
    {
        var waiting = Given.Session(SessionState.Working, message: MessageState.AwaitingPlan);
        Expect.Rejected(
            SessionStateMachine.Apply(waiting, new SubmitPlan(Given.IncumbentOf(waiting), "a revision")),
            Rule.InvalidSourceState);
    }

    [Fact]
    public void Prose_and_a_permission_verdict_do_not_answer_a_plan()
    {
        var waiting = Given.Session(SessionState.Working, message: MessageState.AwaitingPlan);
        Expect.Rejected(
            SessionStateMachine.Apply(waiting, new AnswerInput(Given.Lead, Given.Park, "yes", InputRequestKind.Plan)),
            Rule.PlanVerdictAnswersPlanRequests);
        Expect.Rejected(
            SessionStateMachine.Apply(waiting, new AnswerPermission(
                Given.Lead, InputRequestKind.Plan, false, PermissionVerdict.Allow, "ok")),
            Rule.PlanVerdictAnswersPlanRequests);
    }

    [Fact]
    public void Request_input_cannot_open_a_plan()
    {
        var working = Given.Session(SessionState.Working);
        Expect.Rejected(
            SessionStateMachine.Apply(working, new RequestInput(Given.IncumbentOf(working), InputRequestKind.Plan, "a plan")),
            Rule.PlanVerdictAnswersPlanRequests);
    }

    [Fact]
    public void Park_is_refused_while_a_plan_is_waiting()
    {
        var waiting = Given.Session(SessionState.Working, message: MessageState.AwaitingPlan);
        Expect.Rejected(
            SessionStateMachine.Apply(waiting, new Park(Given.Lead, Given.Park)),
            Rule.InvalidSourceState);
    }
}
