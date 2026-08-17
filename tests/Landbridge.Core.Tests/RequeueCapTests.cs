namespace Docket.Core.Tests;

/// <summary>
/// Infrastructure loss is a fail-and-park, not a redispatch. The Lead sees a
/// plane-authored reason and may resume the same session if the reason looks flaky.
/// </summary>
public class RequeueCapTests
{
    [Fact]
    public void Liveness_loss_fails_the_attempt_instead_of_requeueing()
    {
        var result = SessionStateMachine.Apply(
            Given.Session(SessionState.Working, infrastructureRequeues: 3, requeueLimit: 5),
            new LivenessLost(LivenessLossReason.NoProgress));

        var next = Expect.Transitioned(result, SessionState.Failed);
        Assert.Equal(4, next.InfrastructureRequeues);
        Assert.Equal(LivenessLossReason.NoProgress, next.LastRequeueReason);
        Assert.Null(next.CurrentInstance);
        Assert.False(next.State.IsTerminal());
    }

    [Fact]
    public void Every_failure_records_the_signal_that_caused_it()
    {
        var first = Expect.Transitioned(
            SessionStateMachine.Apply(
                Given.Session(SessionState.Working, requeueLimit: 5),
                new LivenessLost(LivenessLossReason.MachineReboot)),
            SessionState.Failed);
        Assert.Equal(LivenessLossReason.MachineReboot, first.LastRequeueReason);

        var second = Expect.Transitioned(
            SessionStateMachine.Apply(
                Given.Session(SessionState.Working, infrastructureRequeues: 1, requeueLimit: 5),
                new LivenessLost(LivenessLossReason.NoProgress)),
            SessionState.Failed);
        Assert.Equal(LivenessLossReason.NoProgress, second.LastRequeueReason);
    }

    [Fact]
    public void A_failed_attempt_revokes_the_token_and_keeps_the_workspace()
    {
        var task = Given.Session(SessionState.Working, infrastructureRequeues: 4, requeueLimit: 5);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new LivenessLost(LivenessLossReason.NoProgress));
        var next = Expect.Transitioned(result, SessionState.Failed);
        Assert.Null(next.CurrentInstance);

        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
        Assert.DoesNotContain(new DiscardWorkspace(), effects);
    }

    [Fact]
    public void Reaching_any_count_never_rejects_and_never_touches_the_verification_counter()
    {
        var next = Expect.Transitioned(
            SessionStateMachine.Apply(
                Given.Session(SessionState.Working, infrastructureRequeues: 4, requeueLimit: 5),
                new LivenessLost(LivenessLossReason.LivenessTimeout)),
            SessionState.Failed);

        Assert.Equal(0, next.VerificationFailures);
        Assert.NotEqual(SessionState.Rejected, next.State);
        Assert.NotEqual(SessionState.Canceled, next.State);
    }

    [Fact]
    public void A_failed_task_can_be_woken_like_a_park()
    {
        var failed = Expect.Transitioned(
            SessionStateMachine.Apply(
                Given.Session(SessionState.Working),
                new LivenessLost(LivenessLossReason.ProcessExited)),
            SessionState.Failed);

        var woken = Expect.Transitioned(
            SessionStateMachine.Apply(failed, new WakeParked("try again — handshake flake")),
            SessionState.Submitted);
        Assert.Equal(LivenessLossReason.ProcessExited, woken.LastRequeueReason);
    }

    [Fact]
    public void A_loss_from_blocked_on_input_fails_the_same_way()
    {
        var task = Given.Session(SessionState.BlockedOnInput, infrastructureRequeues: 1, requeueLimit: 2);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new LivenessLost(LivenessLossReason.MachineReboot));
        var next = Expect.Transitioned(result, SessionState.Failed);
        Assert.Equal(LivenessLossReason.MachineReboot, next.LastRequeueReason);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
    }
}
