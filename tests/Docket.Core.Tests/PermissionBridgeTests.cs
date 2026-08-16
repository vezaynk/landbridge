namespace Docket.Core.Tests;

/// <summary>
/// §11's permission bridge in the pure engine: the permission flavor of
/// blocked_on_input, its verdict path back into <c>working</c>, and the escalation that
/// moves a single request out of the Lead's reach. The invariant under nearly all of it
/// is that a permission request's worker is <em>still alive</em> — which is what makes
/// this the one wait that resumes in place, and what makes answering it the wrong way
/// a stranding rather than a delay.
/// </summary>
public class PermissionBridgeTests
{
    private const string Tool = "Bash";
    private const string ProposedInput = """{"command":"/bin/echo hi"}""";

    private static TransitionResult Ask(
        TaskRecord task, string? tool = Tool, string? input = ProposedInput) =>
        TaskStateMachine.Apply(task,
            new RequestInput(Given.IncumbentOf(task), InputRequestKind.Permission, input, tool));

    private static TransitionResult Decide(
        TaskRecord task, PermissionVerdict verdict, string? message = null,
        Actor? actor = null, bool escalated = false,
        InputRequestKind? pending = InputRequestKind.Permission) =>
        TaskStateMachine.Apply(task,
            new AnswerPermission(actor ?? Given.Lead, pending, escalated, verdict, message));

    private static TransitionResult Escalate(
        TaskRecord task, string reason = "wants the keychain; the task never mentions credentials",
        Actor? actor = null, InputRequestKind? pending = InputRequestKind.Permission) =>
        TaskStateMachine.Apply(task, new EscalatePermission(actor ?? Given.Lead, pending, reason));

    // ── Asking ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_permission_request_blocks_the_task_on_input()
    {
        var working = Given.Task(TaskState.Working);

        var after = Expect.Transitioned(Ask(working), TaskState.BlockedOnInput);

        // The incumbent is untouched: the process is blocked inside its tool call, not gone,
        // which is the whole premise of this flavor.
        Assert.Equal(working.CurrentInstance, after.CurrentInstance);
    }

    [Fact]
    public void A_permission_request_must_name_the_tool_it_is_about()
    {
        Expect.Rejected(
            Ask(Given.Task(TaskState.Working), tool: null),
            Rule.PermissionRequestNamesItsTool);
        Expect.Rejected(
            Ask(Given.Task(TaskState.Working), tool: "   "),
            Rule.PermissionRequestNamesItsTool);
    }

    [Fact]
    public void A_permission_request_keeps_the_task_services_and_forwards()
    {
        // ACP sessions stay up through a question, so neither kind tears services down
        // here. Park / AnswerInput / wait-TTL are the edges that release them.
        Assert.Empty(Expect.Effects(Ask(Given.Task(TaskState.Working))));

        var ordinary = Given.Task(TaskState.Working);
        Assert.Empty(Expect.Effects(TaskStateMachine.Apply(ordinary,
            new RequestInput(Given.IncumbentOf(ordinary), InputRequestKind.Question, "which database?"))));
    }

    // ── Deciding ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_allow_returns_the_task_to_working_with_the_same_incumbent()
    {
        var blocked = Given.Task(TaskState.BlockedOnInput);

        var result = Decide(blocked, PermissionVerdict.Allow);

        var after = Expect.Transitioned(result, TaskState.Working);
        Assert.Equal(blocked.CurrentInstance, after.CurrentInstance);
        // No token revoked and no park written: the worker never stopped being the
        // incumbent, so there is nothing to redispatch and nothing to resume.
        Assert.Empty(Expect.Effects(result));
        Assert.Null(after.Park);
    }

    [Fact]
    public void A_deny_carrying_a_message_also_returns_the_task_to_working()
    {
        var blocked = Given.Task(TaskState.BlockedOnInput);

        var after = Expect.Transitioned(
            Decide(blocked, PermissionVerdict.Deny, "not on this task; use the fixture instead"),
            TaskState.Working);

        Assert.Equal(blocked.CurrentInstance, after.CurrentInstance);
    }

    [Fact]
    public void A_deny_without_a_message_is_refused()
    {
        // A refusal an agent cannot read is one it walks into again.
        foreach (var empty in new[] { null, "", "   " })
            Expect.Rejected(
                Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Deny, empty),
                Rule.PermissionDenialCarriesMessage);
    }

    [Fact]
    public void An_allow_needs_no_message()
    {
        Expect.Transitioned(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Allow),
            TaskState.Working);
    }

    [Fact]
    public void An_over_cap_message_is_refused_and_the_task_stays_blocked()
    {
        var tooLong = new string('x', AnswerPermission.MaxMessageBytes + 1);

        Expect.Rejected(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Deny, tooLong),
            Rule.AnswerWithinSizeCap);
    }

    [Fact]
    public void A_verdict_is_refused_on_a_task_that_is_not_blocked()
    {
        foreach (var state in new[] { TaskState.Working, TaskState.Submitted, TaskState.Verifying, TaskState.Parked })
            Expect.Rejected(
                Decide(Given.Task(state), PermissionVerdict.Allow),
                Rule.InvalidSourceState);
    }

    [Fact]
    public void A_verdict_is_refused_on_a_request_of_another_kind()
    {
        // The task is blocked and the caller has authority — but it is waiting on prose,
        // and moving it to working would leave it working with nothing running it.
        Expect.Rejected(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Allow,
                pending: InputRequestKind.Question),
            Rule.PermissionVerdictAnswersPermissionRequests);
        Expect.Rejected(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Allow, pending: null),
            Rule.PermissionVerdictAnswersPermissionRequests);
    }

    [Fact]
    public void Prose_is_refused_on_a_permission_request()
    {
        // The mirror of the above, and the more dangerous direction: this path revokes the
        // incumbent's token, which would strand a worker still holding its tool call open.
        Expect.Rejected(
            TaskStateMachine.Apply(
                Given.Task(TaskState.BlockedOnInput),
                new AnswerInput(Given.Lead, Given.Park, "go ahead", InputRequestKind.Permission)),
            Rule.PermissionVerdictAnswersPermissionRequests);
    }

    [Fact]
    public void Prose_still_answers_every_other_kind()
    {
        // The new gate is scoped to the permission kind; a caller that supplies no kind at
        // all (every pre-existing one) is unaffected.
        Expect.Transitioned(
            TaskStateMachine.Apply(
                Given.Task(TaskState.BlockedOnInput),
                new AnswerInput(Given.Lead, Given.Park, "postgres", InputRequestKind.Question)),
            TaskState.Submitted);
        Expect.Transitioned(
            TaskStateMachine.Apply(
                Given.Task(TaskState.BlockedOnInput),
                new AnswerInput(Given.Lead, Given.Park, "postgres")),
            TaskState.Submitted);
    }

    [Fact]
    public void A_worker_cannot_decide_its_own_permission_request()
    {
        var blocked = Given.Task(TaskState.BlockedOnInput);

        Expect.Rejected(
            Decide(blocked, PermissionVerdict.Allow, actor: Given.IncumbentOf(blocked)),
            Rule.ActorLacksAuthority);
    }

    [Fact]
    public void Another_teams_lead_cannot_decide_a_permission_request()
    {
        Expect.Rejected(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Allow, actor: Given.ForeignLead),
            Rule.ActorLacksAuthority);
    }

    [Fact]
    public void A_verdict_with_no_incumbent_is_refused()
    {
        // Returning to working means returning to a worker. Without one the verdict has
        // nowhere to land, so the request stays pending for the sweeper to park.
        var orphaned = Given.Task(TaskState.BlockedOnInput) with { CurrentInstance = null };

        Expect.Rejected(
            Decide(orphaned, PermissionVerdict.Allow),
            Rule.PermissionWaiterStillIncumbent);
    }

    // ── Escalating ────────────────────────────────────────────────────────────

    [Fact]
    public void Escalation_leaves_the_task_blocked_on_the_same_request()
    {
        var blocked = Given.Task(TaskState.BlockedOnInput);

        var result = Escalate(blocked);

        var after = Expect.Transitioned(result, TaskState.BlockedOnInput);
        Assert.Equal(blocked.CurrentInstance, after.CurrentInstance);
        Assert.Empty(Expect.Effects(result));
    }

    [Fact]
    public void Escalation_requires_a_reason()
    {
        foreach (var empty in new[] { "", "   " })
            Expect.Rejected(
                Escalate(Given.Task(TaskState.BlockedOnInput), reason: empty),
                Rule.PermissionEscalationCarriesReason);
    }

    [Fact]
    public void An_over_cap_escalation_reason_is_refused()
    {
        Expect.Rejected(
            Escalate(Given.Task(TaskState.BlockedOnInput),
                reason: new string('y', AnswerPermission.MaxMessageBytes + 1)),
            Rule.AnswerWithinSizeCap);
    }

    [Fact]
    public void Only_a_permission_request_is_escalated_this_way()
    {
        Expect.Rejected(
            Escalate(Given.Task(TaskState.BlockedOnInput), pending: InputRequestKind.AuthHelp),
            Rule.PermissionVerdictAnswersPermissionRequests);
    }

    [Fact]
    public void Escalation_is_refused_on_a_task_that_is_not_blocked()
    {
        Expect.Rejected(Escalate(Given.Task(TaskState.Working)), Rule.InvalidSourceState);
    }

    [Fact]
    public void A_worker_cannot_escalate_its_own_permission_request()
    {
        var blocked = Given.Task(TaskState.BlockedOnInput);

        Expect.Rejected(
            Escalate(blocked, actor: Given.IncumbentOf(blocked)),
            Rule.ActorLacksAuthority);
    }

    // ── What escalation actually buys ─────────────────────────────────────────

    [Fact]
    public void Once_escalated_the_lead_can_no_longer_decide_it()
    {
        Expect.Rejected(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Allow,
                actor: Given.Lead, escalated: true),
            Rule.EscalatedPermissionIsHumanOnly);
        // Denying is refused for the same reason: escalation removes the authority, not
        // just the ability to approve.
        Expect.Rejected(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Deny, "no",
                actor: Given.Lead, escalated: true),
            Rule.EscalatedPermissionIsHumanOnly);
    }

    [Fact]
    public void A_human_decides_an_escalated_request()
    {
        Expect.Transitioned(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Allow,
                actor: Given.Human, escalated: true),
            TaskState.Working);
    }

    [Fact]
    public void A_human_decides_an_unescalated_request_too()
    {
        // Escalation removes the Lead's authority; it does not create the human's. A human
        // never needed to wait for a Lead to hand a request over.
        Expect.Transitioned(
            Decide(Given.Task(TaskState.BlockedOnInput), PermissionVerdict.Allow, actor: Given.Human),
            TaskState.Working);
    }

    // ── Interaction with the wait TTL ─────────────────────────────────────────

    [Fact]
    public void An_unanswered_permission_request_still_parks_on_its_wait_ttl()
    {
        // The abandonment path is shared with every other kind: nothing about being a
        // permission request exempts a request nobody answered.
        var blocked = Given.Task(TaskState.BlockedOnInput);

        var after = Expect.Transitioned(
            TaskStateMachine.Apply(blocked, new WaitTtlExpired(Given.Park)), TaskState.Parked);

        Assert.Null(after.CurrentInstance);
        Assert.Equal(Given.Park, after.Park);
    }
}
