namespace Landbridge.Core.Tests;

/// <summary>One positive test per §6 transition row, plus end-to-end chains.</summary>
public class LifecycleTests
{
    [Fact]
    public void Create_by_lead_produces_submitted()
    {
        var result = SessionStateMachine.Create(
            new CreateSession(Given.Lead, Given.Team, "pnpm test", CompletionMode.Lead,
                Profile: null),
            Given.Id, "team-x/task-y");

        var task = Expect.Transitioned(result, SessionState.Submitted);
        Assert.Equal(0, task.Attempt);
        Assert.Null(task.CurrentInstance);
    }

    [Fact]
    public void Dispatch_moves_submitted_to_working_mints_token_and_increments_attempt()
    {
        var instance = WorkerInstanceId.New();
        var result = SessionStateMachine.Apply(
            Given.Session(SessionState.Submitted),
            new Dispatch(Given.Machine(), instance));

        var task = Expect.Transitioned(result, SessionState.Working);
        Assert.Equal(instance, task.CurrentInstance);
        Assert.Equal(1, task.Attempt);
        // The mint carries the dispatching machine (§12): the instance row is the one
        // durable record of where a dispatch ran, and a terminal task's transcript can
        // only be found by asking that machine.
        Assert.Contains(new MintWorkerInstanceToken(instance, "machine-a"), Expect.Effects(result));
    }

    [Fact]
    public void Liveness_loss_fails_a_working_task_instead_of_requeueing()
    {
        var task = Given.Session(SessionState.Working);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new LivenessLost(LivenessLossReason.MachineReboot));

        var next = Expect.Transitioned(result, SessionState.Failed);
        Assert.Equal(1, next.InfrastructureRequeues);
        Assert.Equal(0, next.VerificationFailures);
        Assert.Null(next.CurrentInstance);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
    }

    [Fact]
    public void Report_result_moves_working_to_verifying_and_keeps_the_process()
    {
        var task = Given.Session(SessionState.Working);
        var result = SessionStateMachine.Apply(task,
            new ReportResult(Given.IncumbentOf(task), "git:refs/agents/run-1/fix"));

        Expect.Transitioned(result, SessionState.Verifying);
        Assert.Empty(Expect.Effects(result));
    }

    [Fact]
    public void Lead_acceptance_completes_a_lead_task_and_revokes_the_instance()
    {
        var task = Given.Session(SessionState.Verifying);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new VerdictAccept(Given.Lead));

        var next = Expect.Transitioned(result, SessionState.Completed);
        Assert.Null(next.CurrentInstance);
        Assert.Equal(VerdictProvenance.LeadSession, next.CompletionProvenance);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), Expect.Effects(result));
    }

    [Fact]
    public void Failed_verification_rejects_without_redispatch()
    {
        var result = SessionStateMachine.Apply(
            Given.Session(SessionState.Verifying, verificationFailures: 0, retryLimit: 3),
            new VerdictFail(Given.Lead));

        var next = Expect.Transitioned(result, SessionState.Rejected);
        Assert.Equal(1, next.VerificationFailures);
        Assert.Equal(0, next.InfrastructureRequeues);
    }

    [Fact]
    public void Liveness_loss_fails_a_verifying_task_and_releases_services()
    {
        var task = Given.Session(SessionState.Verifying);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(
            task, new LivenessLost(LivenessLossReason.ProcessExited, incumbent));

        var next = Expect.Transitioned(result, SessionState.Failed);
        Assert.Null(next.CurrentInstance);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
    }

    [Fact]
    public void Park_from_verifying_releases_the_session()
    {
        var task = Given.Session(SessionState.Verifying);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new Park(Given.Lead, Given.Park));

        var next = Expect.Transitioned(result, SessionState.Parked);
        Assert.Equal(Given.Park, next.Park);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
    }

    [Fact]
    public void A_lead_reply_to_a_report_returns_the_live_worker_to_working()
    {
        var task = Given.Session(SessionState.Verifying);
        var result = SessionStateMachine.Apply(task, new LeadMessage(Given.Lead, "needs a test"));
        var next = Expect.Transitioned(result, SessionState.Working);
        Assert.Equal(task.CurrentInstance, next.CurrentInstance);
    }

    [Fact]
    public void Request_input_of_a_question_stays_working()
    {
        var task = Given.Session(SessionState.Working);
        var result = SessionStateMachine.Apply(task,
            new RequestInput(Given.IncumbentOf(task), InputRequestKind.Question));

        Expect.Transitioned(result, SessionState.Working);
        Assert.DoesNotContain(Expect.Effects(result), e => e is ClearServicesAndForwards);
    }

    [Fact]
    public void Request_input_of_permission_still_blocks()
    {
        var task = Given.Session(SessionState.Working);
        var result = SessionStateMachine.Apply(task,
            new RequestInput(Given.IncumbentOf(task), InputRequestKind.Permission, PermissionTool: "Bash"));

        Expect.Transitioned(result, SessionState.BlockedOnInput);
    }

    [Fact]
    public void Lead_can_message_a_working_session_without_a_pending_question()
    {
        var task = Given.Session(SessionState.Working);
        var incumbent = task.CurrentInstance!.Value;

        var next = Expect.Transitioned(
            SessionStateMachine.Apply(task, new LeadMessage(Given.Lead, "keep going on the tests")),
            SessionState.Working);
        Assert.Equal(incumbent, next.CurrentInstance);
        Assert.Empty(Expect.Effects(
            SessionStateMachine.Apply(Given.Session(SessionState.Working), new LeadMessage(Given.Lead, "ok"))));
    }

    [Theory]
    [InlineData(true)]  // Lead answers
    [InlineData(false)] // Human answers
    public void Answer_requeues_the_blocked_task_for_redispatch_with_resume(bool byLead)
    {
        // §11: the worker process is gone the moment the task blocked, so the answer
        // cannot resume in place — it writes a park record and requeues (→ submitted),
        // never → working. The predecessor token is revoked (§5) and the
        // infrastructure counter is untouched — a Lead answering is not an
        // infrastructure requeue (§6, two counters).
        var task = Given.Session(SessionState.BlockedOnInput);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(
            task,
            new AnswerInput(byLead ? Given.Lead : Given.Human, Given.Park));

        var next = Expect.Transitioned(result, SessionState.Submitted);
        Assert.Equal(Given.Park, next.Park);
        Assert.Null(next.CurrentInstance);
        Assert.Equal(0, next.InfrastructureRequeues);
        var effects = Expect.Effects(result);
        Assert.Contains(new WriteParkRecord(Given.Park), effects);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Continue_session_resumes_a_live_wait_in_place(bool byLead)
    {
        var task = Given.Session(SessionState.BlockedOnInput);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(
            task,
            new ContinueSession(byLead ? Given.Lead : Given.Human, "use staging-pg"));

        var next = Expect.Transitioned(result, SessionState.Working);
        Assert.Equal(incumbent, next.CurrentInstance);
        Assert.Null(next.Park);
        Assert.Empty(Expect.Effects(result));
    }

    [Fact]
    public void Continue_session_refuses_a_permission_request()
    {
        var task = Given.Session(SessionState.BlockedOnInput);
        Expect.Rejected(
            SessionStateMachine.Apply(
                task,
                new ContinueSession(Given.Lead, "go ahead", InputRequestKind.Permission)),
            Rule.PermissionVerdictAnswersPermissionRequests);
    }

    [Fact]
    public void Wait_ttl_expiry_parks_the_task_and_writes_the_park_record()
    {
        var task = Given.Session(SessionState.BlockedOnInput);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new WaitTtlExpired(Given.Park));

        var next = Expect.Transitioned(result, SessionState.Parked);
        Assert.Equal(Given.Park, next.Park);
        Assert.Null(next.CurrentInstance);
        var effects = Expect.Effects(result);
        Assert.Contains(new WriteParkRecord(Given.Park), effects);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
    }

    [Fact]
    public void Waking_a_parked_task_requeues_it_and_keeps_the_park_record_for_affinity()
    {
        var task = Given.Session(SessionState.Parked) with { Park = Given.Park };

        var result = SessionStateMachine.Apply(task, new WakeParked());

        var next = Expect.Transitioned(result, SessionState.Submitted);
        Assert.Equal(Given.Park, next.Park);
    }

    [Fact]
    public void Park_from_working_releases_the_session_and_revokes_the_instance()
    {
        var task = Given.Session(SessionState.Working);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new Park(Given.Lead, Given.Park));

        var next = Expect.Transitioned(result, SessionState.Parked);
        Assert.Equal(Given.Park, next.Park);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
        Assert.Contains(new WriteParkRecord(Given.Park), effects);
    }

    [Fact]
    public void Park_from_blocked_on_input_clears_services()
    {
        // A question no longer tears services down, so park is the first time they go.
        var task = Given.Session(SessionState.BlockedOnInput);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new Park(Given.Lead, Given.Park));

        var next = Expect.Transitioned(result, SessionState.Parked);
        Assert.Equal(Given.Park, next.Park);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new WriteParkRecord(Given.Park), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
    }

    [Fact]
    public void Stop_with_preserve_and_park_parks_a_working_task()
    {
        var task = Given.Session(SessionState.Working);
        var incumbent = task.CurrentInstance!.Value;

        var result = SessionStateMachine.Apply(task, new StopPreserveAndPark(Given.Lead, Given.Park));

        var next = Expect.Transitioned(result, SessionState.Parked);
        Assert.Equal(Given.Park, next.Park);
        var effects = Expect.Effects(result);
        Assert.Contains(new RevokeWorkerInstanceToken(incumbent), effects);
        Assert.Contains(new ClearServicesAndForwards(), effects);
        Assert.Contains(new WriteParkRecord(Given.Park), effects);
    }

    [Fact]
    public void Full_lifecycle_with_one_failed_verification_lands_completed_with_correct_counters()
    {
        var created = SessionStateMachine.Create(
            new CreateSession(Given.Lead, Given.Team, "make test", CompletionMode.Lead,
                Profile: null),
            Given.Id, "team-x/task-y");
        var task = ((TransitionResult.Transitioned)created).Session;

        var first = WorkerInstanceId.New();
        task = Expect.Transitioned(SessionStateMachine.Apply(task, new Dispatch(Given.Machine(), first)), SessionState.Working);
        task = Expect.Transitioned(SessionStateMachine.Apply(task, new ReportResult(new WorkerCaller(task.Team, task.Id, first), "ref-1")), SessionState.Verifying);
        task = Expect.Transitioned(SessionStateMachine.Apply(task, new LeadMessage(Given.Lead, "add a test")), SessionState.Working);
        task = Expect.Transitioned(SessionStateMachine.Apply(task, new ReportResult(new WorkerCaller(task.Team, task.Id, first), "ref-2")), SessionState.Verifying);
        task = Expect.Transitioned(SessionStateMachine.Apply(task, new VerdictAccept(Given.Lead)), SessionState.Completed);

        Assert.Equal(1, task.Attempt);
        Assert.Equal(0, task.VerificationFailures);
        Assert.Equal(0, task.InfrastructureRequeues);
    }
}
