namespace Docket.Core.Tests;

/// <summary>
/// §9 check 7's ceiling on infrastructure requeues, and the reason each requeue records
/// (#73). The pair that matters is
/// <see cref="Requeue_below_the_cap_returns_the_task_to_submitted"/> and
/// <see cref="The_requeue_that_reaches_the_cap_abandons_the_task"/>: a task has to
/// survive an ordinary bad patch — a reboot, a flaky dispatch — while a task that wedges
/// every machine it touches has to stop. Get only the first and a wedged task loops
/// forever, burning a whole no-progress ceiling of model time on every attempt;
/// get only the second and one bad machine ends work that was fine.
///
/// The other invariant under test is §6's two counters: reaching this cap never
/// <c>rejects</c> a task, because rejection is a verdict on the <em>work</em>, and a
/// machine that keeps failing a task says nothing about whether the task was any good.
/// </summary>
public class RequeueCapTests
{
    [Fact]
    public void Requeue_below_the_cap_returns_the_task_to_submitted()
    {
        var result = TaskStateMachine.Apply(
            Given.Task(TaskState.Working, infrastructureRequeues: 3, requeueLimit: 5),
            new LivenessLost(LivenessLossReason.NoProgress));

        var next = Expect.Transitioned(result, TaskState.Submitted);
        Assert.Equal(4, next.InfrastructureRequeues);
        Assert.False(next.InfrastructureRequeuesExhausted);
    }

    [Fact]
    public void Every_requeue_records_the_signal_that_caused_it()
    {
        // The half of #73 that made a loop unreadable: the reason was computed and
        // dropped, so N requeues left N identical marks. Each command's reason now lands
        // on the record the store persists, and a later requeue overwrites it — the row
        // holds the live reason, the event log holds the history.
        var first = Expect.Transitioned(
            TaskStateMachine.Apply(
                Given.Task(TaskState.Working, requeueLimit: 5),
                new LivenessLost(LivenessLossReason.MachineReboot)),
            TaskState.Submitted);
        Assert.Equal(LivenessLossReason.MachineReboot, first.LastRequeueReason);

        var second = Expect.Transitioned(
            TaskStateMachine.Apply(
                Given.Task(TaskState.Working, infrastructureRequeues: 1, requeueLimit: 5),
                new LivenessLost(LivenessLossReason.NoProgress)),
            TaskState.Submitted);
        Assert.Equal(LivenessLossReason.NoProgress, second.LastRequeueReason);
    }

    [Fact]
    public void The_requeue_that_reaches_the_cap_abandons_the_task()
    {
        var task = Given.Task(TaskState.Working, infrastructureRequeues: 4, requeueLimit: 5);
        var incumbent = task.CurrentInstance!.Value;

        var result = TaskStateMachine.Apply(task, new LivenessLost(LivenessLossReason.NoProgress));

        // Canceled, not a fourth redispatch: §6 already gives the control plane this
        // state for work that was called off, and the reason that ended it stays legible.
        var next = Expect.Transitioned(result, TaskState.Canceled);
        Assert.Equal(5, next.InfrastructureRequeues);
        Assert.True(next.InfrastructureRequeuesExhausted);
        Assert.Equal(LivenessLossReason.NoProgress, next.LastRequeueReason);
        Assert.Null(next.CurrentInstance);

        // The same wind-down every requeue does — the predecessor's token is revoked
        // (§9 check 14) and the task's services and forwards are released (§6) — and
        // deliberately NO workspace discard: whatever wedged five attempts is the
        // evidence a human needs to diagnose it.
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
        Assert.DoesNotContain(new DiscardWorkspace(), effects);
    }

    [Fact]
    public void Reaching_the_cap_never_rejects_and_never_touches_the_verification_counter()
    {
        // §6's two counters, at the one place they could be conflated. `rejected` means
        // the work failed its criteria; infrastructure exhaustion means the plane gave up
        // on placing it, which is not the same judgement and must not consume the retries
        // the task has for actually failing.
        var next = Expect.Transitioned(
            TaskStateMachine.Apply(
                Given.Task(TaskState.Working, infrastructureRequeues: 4, requeueLimit: 5),
                new LivenessLost(LivenessLossReason.LivenessTimeout)),
            TaskState.Canceled);

        Assert.Equal(0, next.VerificationFailures);
        Assert.NotEqual(TaskState.Rejected, next.State);
    }

    [Fact]
    public void A_non_positive_cap_is_uncapped()
    {
        // The documented opt-out (§9 check 7): an operator who would rather a task retry
        // forever than go terminal configures the cap off, and gets exactly the behaviour
        // that shipped before the cap existed.
        var task = Given.Task(TaskState.Working, infrastructureRequeues: 99, requeueLimit: 0);

        var next = Expect.Transitioned(
            TaskStateMachine.Apply(task, new LivenessLost(LivenessLossReason.MachineReboot)),
            TaskState.Submitted);

        Assert.Equal(100, next.InfrastructureRequeues);
        Assert.False(next.InfrastructureRequeuesExhausted);
    }

    [Fact]
    public void A_requeue_from_blocked_on_input_counts_against_the_cap_too()
    {
        // §6: a blocked task whose machine dies requeues on the same counter, so the cap
        // has to see those attempts as well — otherwise a task that wedges its machine
        // while waiting escapes the ceiling by never being working when it fails.
        var task = Given.Task(TaskState.BlockedOnInput, infrastructureRequeues: 1, requeueLimit: 2);
        var incumbent = task.CurrentInstance!.Value;

        var result = TaskStateMachine.Apply(task, new LivenessLost(LivenessLossReason.MachineReboot));

        var next = Expect.Transitioned(result, TaskState.Canceled);
        Assert.Equal(LivenessLossReason.MachineReboot, next.LastRequeueReason);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        // Not working, so nothing to clear: services are registered only while working (§8.2).
        Assert.DoesNotContain(new ClearServicesAndForwards(), effects);
    }

    [Fact]
    public void A_task_abandoned_at_the_cap_is_final()
    {
        // §6: terminal states are never resumed. Worth pinning here because the cap is the
        // first path that reaches a terminal state without anyone asking for it — a Lead
        // that re-dispatches, cancels, or answers this task must be refused, not obeyed.
        var abandoned = Expect.Transitioned(
            TaskStateMachine.Apply(
                Given.Task(TaskState.Working, infrastructureRequeues: 4, requeueLimit: 5),
                new LivenessLost(LivenessLossReason.ProcessExited)),
            TaskState.Canceled);

        Expect.Rejected(
            TaskStateMachine.Apply(abandoned, new Dispatch(Given.Machine(), WorkerInstanceId.New())),
            Rule.TerminalStatesAreFinal);
        Expect.Rejected(
            TaskStateMachine.Apply(abandoned, new LivenessLost(LivenessLossReason.MachineReboot)),
            Rule.TerminalStatesAreFinal);
    }
}
