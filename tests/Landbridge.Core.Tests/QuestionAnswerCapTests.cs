namespace Landbridge.Core.Tests;

/// <summary>
/// §10/§11 in-band question and answer: the two halves of the human-in-the-loop
/// exchange are size-capped at the engine exactly as the worker's report is — a length
/// gate, not content interpretation, so real detail lives in the workspace and the
/// plane's in-band carry stays bounded.
///
/// <para>The refusal's <em>resting state</em> is the point of these tests as much as the
/// rule: an over-cap question leaves the task <c>working</c> (the worker asks again,
/// shorter) and an over-cap answer leaves it <c>blocked_on_input</c> (the Lead answers
/// again). Neither ever half-delivers.</para>
/// </summary>
public class QuestionAnswerCapTests
{
    private static TransitionResult Ask(string? question)
    {
        var task = Given.Session(SessionState.Working);
        return SessionStateMachine.Apply(
            task, new RequestInput(Given.IncumbentOf(task), InputRequestKind.Question, question));
    }

    private static TransitionResult Answer(string? answer) =>
        SessionStateMachine.Apply(
            Given.Session(SessionState.Working, message: MessageState.AwaitingLead),
            new AnswerInput(Given.Lead, Given.Park, answer));

    private static TransitionResult Wake(string? answer) =>
        SessionStateMachine.Apply(Given.Session(SessionState.Parked), new WakeParked(answer));

    [Fact]
    public void A_null_question_is_accepted() =>
        Expect.Transitioned(Ask(null), SessionState.Working); // a question is a turn, not a phase

    [Fact]
    public void A_question_at_the_cap_is_accepted() =>
        Expect.Transitioned(
            Ask(new string('x', RequestInput.MaxQuestionBytes)), SessionState.Working);

    [Fact]
    public void A_question_one_byte_over_the_cap_is_rejected() =>
        Expect.Rejected(
            Ask(new string('x', RequestInput.MaxQuestionBytes + 1)), Rule.QuestionWithinSizeCap);

    [Fact]
    public void A_null_answer_is_accepted() =>
        Expect.Transitioned(Answer(null), SessionState.Working); // unblocking without words is legal

    [Fact]
    public void An_answer_at_the_cap_is_accepted() =>
        Expect.Transitioned(Answer(new string('x', AnswerInput.MaxAnswerBytes)), SessionState.Working);

    [Fact]
    public void An_answer_one_byte_over_the_cap_is_rejected() =>
        Expect.Rejected(
            Answer(new string('x', AnswerInput.MaxAnswerBytes + 1)), Rule.AnswerWithinSizeCap);

    [Fact]
    public void The_parked_half_of_the_answer_path_caps_identically()
    {
        // One answer, two branches (§11): a Lead does not know whether the wait-TTL
        // sweeper parked the task first, so the same text must be accepted or refused
        // the same way either way — otherwise the cap depends on a race.
        Expect.Transitioned(Wake(new string('x', AnswerInput.MaxAnswerBytes)), SessionState.Working);
        Expect.Rejected(Wake(new string('x', AnswerInput.MaxAnswerBytes + 1)), Rule.AnswerWithinSizeCap);

        var continueAt = SessionStateMachine.Apply(
            Given.Session(SessionState.Working, message: MessageState.AwaitingLead),
            new ContinueSession(Given.Lead, new string('x', AnswerInput.MaxAnswerBytes)));
        Expect.Transitioned(continueAt, SessionState.Working);
        var continueOver = SessionStateMachine.Apply(
            Given.Session(SessionState.Working, message: MessageState.AwaitingLead),
            new ContinueSession(Given.Lead, new string('x', AnswerInput.MaxAnswerBytes + 1)));
        Expect.Rejected(continueOver, Rule.AnswerWithinSizeCap);
    }

    [Fact]
    public void Both_caps_count_utf8_bytes_not_chars()
    {
        // '€' (U+20AC) is 3 UTF-8 bytes, so a cap's worth of them is exactly at the
        // limit in chars and 3× over in bytes — proving both gates measure the wire.
        Expect.Rejected(Ask(new string('€', RequestInput.MaxQuestionBytes)), Rule.QuestionWithinSizeCap);
        Expect.Rejected(Answer(new string('€', AnswerInput.MaxAnswerBytes)), Rule.AnswerWithinSizeCap);
    }

    [Fact]
    public void The_question_and_answer_caps_are_the_reports_cap()
    {
        // One in-band budget for every piece of prose the plane carries (§10), so a
        // worker never has to remember three numbers.
        Assert.Equal(ReportResult.MaxReportBytes, RequestInput.MaxQuestionBytes);
        Assert.Equal(ReportResult.MaxReportBytes, AnswerInput.MaxAnswerBytes);
    }

    [Fact]
    public void An_over_cap_question_leaves_the_task_working_so_the_worker_can_ask_again()
    {
        // The engine rejects rather than truncates: a truncated question is a wrong
        // question, and the worker has no way to know it was cut.
        var task = Given.Session(SessionState.Working);
        var result = SessionStateMachine.Apply(task, new RequestInput(
            Given.IncumbentOf(task), InputRequestKind.Question,
            new string('x', RequestInput.MaxQuestionBytes + 1)));

        Expect.Rejected(result, Rule.QuestionWithinSizeCap);
        Assert.Equal(SessionState.Working, task.State); // the record is untouched
    }

    [Fact]
    public void The_question_never_lands_on_the_pure_record()
    {
        // §2 principle 1: content rides the command and is the store's to persist. The
        // engine's own state carries the kind's consequence (blocked) and nothing else.
        var task = Given.Session(SessionState.Working);
        var moved = Expect.Transitioned(
            SessionStateMachine.Apply(task, new RequestInput(
                Given.IncumbentOf(task), InputRequestKind.Question, "which database?")),
            SessionState.Working);

        Assert.DoesNotContain(
            "which database?",
            System.Text.Json.JsonSerializer.Serialize(moved),
            StringComparison.Ordinal);
    }
}
