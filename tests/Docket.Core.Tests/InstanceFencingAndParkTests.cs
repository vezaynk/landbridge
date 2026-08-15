namespace Docket.Core.Tests;

/// <summary>
/// §9 check 14 (incumbent-only transitions), the zombie-replay scenario it
/// exists for, and the park/recovery paths of §11.
/// </summary>
public class InstanceFencingAndParkTests
{
    [Fact]
    public void A_non_incumbent_instance_cannot_report_a_result()
    {
        var task = Given.Task(TaskState.Working);
        var zombie = new WorkerCaller(task.Team, task.Id, WorkerInstanceId.New());

        Expect.Rejected(
            TaskStateMachine.Apply(task, new ReportResult(zombie, "half-done-ref")),
            Rule.IncumbentInstanceOnly);
    }

    [Fact]
    public void A_non_incumbent_instance_cannot_block_the_task_on_input()
    {
        var task = Given.Task(TaskState.Working);
        var zombie = new WorkerCaller(task.Team, task.Id, WorkerInstanceId.New());

        Expect.Rejected(
            TaskStateMachine.Apply(task, new RequestInput(zombie, InputRequestKind.Question)),
            Rule.IncumbentInstanceOnly);
    }

    [Fact]
    public void A_worker_token_scoped_to_another_task_lacks_authority_entirely()
    {
        var task = Given.Task(TaskState.Working);
        var foreign = new WorkerCaller(task.Team, TaskId.New(), task.CurrentInstance!.Value);

        Expect.Rejected(
            TaskStateMachine.Apply(task, new ReportResult(foreign, "ref")),
            Rule.ActorLacksAuthority);
    }

    [Fact]
    public void Zombie_replay_after_requeue_is_fenced_end_to_end()
    {
        // M3 scenario from the audit: liveness loss requeues the task, a new
        // instance is dispatched, and the orphaned predecessor tries to flip
        // the state under it.
        var task = Given.Task(TaskState.Working);
        var zombieInstance = task.CurrentInstance!.Value;

        var requeued = Expect.Transitioned(
            TaskStateMachine.Apply(task, new LivenessLost(LivenessLossReason.LivenessTimeout)),
            TaskState.Submitted);

        var successor = WorkerInstanceId.New();
        var redispatched = Expect.Transitioned(
            TaskStateMachine.Apply(requeued, new Dispatch(Given.Machine(), successor)),
            TaskState.Working);
        Assert.Equal(2, redispatched.Attempt);

        var zombie = new WorkerCaller(task.Team, task.Id, zombieInstance);
        Expect.Rejected(
            TaskStateMachine.Apply(redispatched, new ReportResult(zombie, "stale-ref")),
            Rule.IncumbentInstanceOnly);
        Expect.Rejected(
            TaskStateMachine.Apply(redispatched, new RequestInput(zombie, InputRequestKind.Question)),
            Rule.IncumbentInstanceOnly);

        // The incumbent proceeds untouched.
        Expect.Transitioned(
            TaskStateMachine.Apply(redispatched,
                new ReportResult(new WorkerCaller(task.Team, task.Id, successor), "real-ref")),
            TaskState.Verifying);
    }

    [Fact]
    public void Requeue_from_blocked_on_input_uses_the_infrastructure_counter_and_skips_service_cleanup()
    {
        // Services were already cleared when the task left working (§6).
        var task = Given.Task(TaskState.BlockedOnInput);

        var result = TaskStateMachine.Apply(task, new LivenessLost(LivenessLossReason.MachineReboot));

        var next = Expect.Transitioned(result, TaskState.Submitted);
        Assert.Equal(1, next.InfrastructureRequeues);
        Assert.DoesNotContain(new ClearServicesAndForwards(), Expect.Effects(result));
    }

    [Fact]
    public void A_liveness_loss_that_names_its_attempt_still_requeues_that_attempt()
    {
        // The positive control for the fence: in the ordinary case the attempt the clock
        // judged is still the one on the task, and naming it changes nothing.
        var task = Given.Task(TaskState.Working);
        var judged = task.CurrentInstance!.Value;

        var result = TaskStateMachine.Apply(
            task, new LivenessLost(LivenessLossReason.NoProgress, judged));

        var next = Expect.Transitioned(result, TaskState.Submitted);
        Assert.Equal(1, next.InfrastructureRequeues);
        Assert.Contains(new RevokeWorkerInstanceToken(judged), Expect.Effects(result));
    }

    [Fact]
    public void A_liveness_loss_cannot_requeue_a_task_that_parked_on_a_permission_request()
    {
        // The plane's per-dispatch clocks read the row, decide, and then apply — and a
        // permission request committing in that window parks the task while KEEPING its
        // instance, because the asking worker is alive inside the tool call it is waiting in
        // (§11). Unfenced, the requeue landed on it anyway: a §9 check 7 requeue against a
        // task nothing was wrong with, the waiting worker's token revoked under it, and the
        // scan's follow-up kill (#84) aimed at a live process.
        var working = Given.Task(TaskState.Working);
        var judged = working.CurrentInstance!.Value;

        var blocked = Expect.Transitioned(
            TaskStateMachine.Apply(working, new RequestInput(
                Given.IncumbentOf(working), InputRequestKind.Permission,
                Question: "run `rm -rf build`?", PermissionTool: "Bash")),
            TaskState.BlockedOnInput);
        Assert.Equal(judged, blocked.CurrentInstance);

        Expect.Rejected(
            TaskStateMachine.Apply(blocked, new LivenessLost(LivenessLossReason.NoProgress, judged)),
            Rule.IncumbentInstanceOnly);
    }

    [Fact]
    public void A_liveness_loss_cannot_requeue_the_successor_of_the_attempt_it_judged()
    {
        // The same window, the other move the task can make in it: requeued (by a disconnect,
        // a reboot) and redispatched before the decision arrives. Applying it then requeues a
        // healthy successor for its predecessor's silence — two requeues off the cap for one
        // loss, and a kill sent to the worker that is actually making progress.
        var task = Given.Task(TaskState.Working);
        var judged = task.CurrentInstance!.Value;

        var requeued = Expect.Transitioned(
            TaskStateMachine.Apply(task, new LivenessLost(LivenessLossReason.MachineReboot)),
            TaskState.Submitted);
        var redispatched = Expect.Transitioned(
            TaskStateMachine.Apply(requeued, new Dispatch(Given.Machine(), WorkerInstanceId.New())),
            TaskState.Working);

        Expect.Rejected(
            TaskStateMachine.Apply(redispatched, new LivenessLost(LivenessLossReason.NoProgress, judged)),
            Rule.IncumbentInstanceOnly);
        Assert.Equal(1, redispatched.InfrastructureRequeues);
    }

    [Fact]
    public void An_answer_after_the_machine_is_gone_still_requeues_for_a_cold_start()
    {
        // The dispatched machine is gone, so there is no park record to write (null
        // park). The answer still requeues the task (→ submitted) rather than
        // rejecting: redispatch cold-starts it elsewhere from the workspace (§11).
        // No park record is written and the infrastructure counter is untouched (§6).
        var task = Given.Task(TaskState.BlockedOnInput);
        var incumbent = task.CurrentInstance!.Value;

        var result = TaskStateMachine.Apply(task, new AnswerInput(Given.Lead, Park: null));

        var next = Expect.Transitioned(result, TaskState.Submitted);
        Assert.Null(next.Park);
        Assert.Null(next.CurrentInstance);
        Assert.Equal(0, next.InfrastructureRequeues);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.DoesNotContain(effects, e => e is WriteParkRecord);
    }

    [Fact]
    public void Workers_cannot_answer_input_requests()
    {
        var task = Given.Task(TaskState.BlockedOnInput);
        Expect.Rejected(
            TaskStateMachine.Apply(task, new AnswerInput(Given.IncumbentOf(task), Given.Park)),
            Rule.ActorLacksAuthority);
        Expect.Rejected(
            TaskStateMachine.Apply(task, new ContinueSession(Given.IncumbentOf(task), "no")),
            Rule.ActorLacksAuthority);
    }

    [Fact]
    public void Request_input_requires_a_typed_kind()
    {
        var task = Given.Task(TaskState.Working);
        Expect.Rejected(
            TaskStateMachine.Apply(task, new RequestInput(Given.IncumbentOf(task), Kind: null)),
            Rule.TypedRequestKindRequired);
    }

    [Fact]
    public void Report_result_requires_a_result_reference()
    {
        var task = Given.Task(TaskState.Working);
        Expect.Rejected(
            TaskStateMachine.Apply(task, new ReportResult(Given.IncumbentOf(task), " ")),
            Rule.ResultReferenceRequired);
    }

    [Fact]
    public void Only_the_lead_may_park_a_working_task()
    {
        var task = Given.Task(TaskState.Working);
        Expect.Rejected(
            TaskStateMachine.Apply(task, new StopPreserveAndPark(Given.ForeignLead, Given.Park)),
            Rule.ActorLacksAuthority);
    }

    [Fact]
    public void Park_wake_redispatch_chain_preserves_affinity_and_counts_attempts()
    {
        // blocked → parked → submitted → working: the §11 recovery path.
        var task = Given.Task(TaskState.BlockedOnInput) with { Attempt = 1 };

        var parked = Expect.Transitioned(
            TaskStateMachine.Apply(task, new WaitTtlExpired(Given.Park)), TaskState.Parked);

        var woken = Expect.Transitioned(
            TaskStateMachine.Apply(parked, new WakeParked()), TaskState.Submitted);
        Assert.Equal(Given.Park, woken.Park); // redispatch reads this for affinity

        var resumed = Expect.Transitioned(
            TaskStateMachine.Apply(woken, new Dispatch(Given.Machine(), WorkerInstanceId.New())),
            TaskState.Working);
        Assert.Equal(2, resumed.Attempt); // successor can see it inherited a workspace
    }

    [Theory]
    [InlineData(TaskState.Submitted)]
    [InlineData(TaskState.Working)]
    [InlineData(TaskState.Verifying)]
    public void Wait_ttl_only_applies_to_blocked_tasks(TaskState state)
    {
        Expect.Rejected(
            TaskStateMachine.Apply(Given.Task(state), new WaitTtlExpired(Given.Park)),
            Rule.InvalidSourceState);
    }

    [Theory]
    [InlineData(TaskState.Submitted)]
    [InlineData(TaskState.Working)]
    [InlineData(TaskState.BlockedOnInput)]
    public void Wake_only_applies_to_parked_tasks(TaskState state)
    {
        Expect.Rejected(
            TaskStateMachine.Apply(Given.Task(state), new WakeParked()),
            Rule.InvalidSourceState);
    }

    [Fact]
    public void Cancel_reaches_every_non_terminal_state()
    {
        foreach (var state in new[]
                 {
                     TaskState.Submitted, TaskState.Working, TaskState.Verifying,
                     TaskState.BlockedOnInput, TaskState.Parked,
                 })
        {
            var result = TaskStateMachine.Apply(
                Given.Task(state),
                new Cancel(Given.Lead, CancelDisposition.Preserve));
            Expect.Transitioned(result, TaskState.Canceled);
        }
    }
}
