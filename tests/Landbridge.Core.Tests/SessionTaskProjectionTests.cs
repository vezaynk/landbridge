namespace Landbridge.Core.Tests;

public sealed class SessionTaskProjectionTests
{
    [Fact]
    public void Idle_session_has_no_live_task()
    {
        var session = Given.Session();
        Assert.Null(session.MessageId);
        Assert.Null(SessionTaskProjection.Status(session, Guid.NewGuid()));
    }

    [Fact]
    public void Lead_owed_envelope_is_input_required()
    {
        Assert.Equal(SessionTaskStatus.InputRequired,
            SessionTaskProjection.LiveStatus(MessageState.AwaitingLead));
        Assert.Equal(SessionTaskStatus.InputRequired,
            SessionTaskProjection.LiveStatus(MessageState.AwaitingPermission));
        Assert.Equal(SessionTaskStatus.InputRequired,
            SessionTaskProjection.LiveStatus(MessageState.AwaitingReport));
    }

    [Fact]
    public void Awaiting_pull_is_working()
    {
        Assert.Equal(SessionTaskStatus.Working,
            SessionTaskProjection.LiveStatus(MessageState.AwaitingPull));
    }

    [Fact]
    public void Accept_and_discard_close_the_envelope_as_completed()
    {
        Assert.Equal(SessionTaskStatus.Completed,
            SessionTaskProjection.ClosedStatus(MessageTerminal.Completed));
        Assert.Equal("accepted",
            SessionTaskProjection.ClosedStatusMessage(MessageTerminal.Completed, MessageVerdict.Accepted));
        Assert.Equal("discarded",
            SessionTaskProjection.ClosedStatusMessage(MessageTerminal.Completed, MessageVerdict.Discarded));
    }

    [Fact]
    public void Session_cancel_closes_the_envelope_as_cancelled()
    {
        Assert.Equal(SessionTaskStatus.Cancelled,
            SessionTaskProjection.ClosedStatus(MessageTerminal.Cancelled));
    }

    [Fact]
    public void Opening_an_envelope_mints_a_task_id_distinct_from_the_session()
    {
        var idle = Given.Session(SessionState.Working, message: MessageState.Idle);
        Assert.Null(idle.MessageId);

        var asked = Expect.Transitioned(
            SessionStateMachine.Apply(
                idle,
                new RequestInput(Given.IncumbentOf(idle), InputRequestKind.Question, "which DB?")),
            SessionState.Working);
        Assert.NotNull(asked.MessageId);
        Assert.NotEqual(asked.Id.Value, asked.MessageId);
        Assert.Equal(MessageState.AwaitingLead, asked.MessageState);
        Assert.Equal(SessionTaskStatus.InputRequired,
            SessionTaskProjection.Status(asked, asked.MessageId!.Value));
    }

    [Fact]
    public void Closing_moves_the_id_to_last_message_and_the_next_open_is_a_new_id()
    {
        var asked = Given.Session(SessionState.Working, message: MessageState.AwaitingLead);
        var openId = asked.MessageId;

        var pulled = Expect.Transitioned(
            SessionStateMachine.Apply(
                asked with { MessageState = MessageState.AwaitingPull },
                new PullReceipt(Given.IncumbentOf(asked))),
            SessionState.Working);
        Assert.Null(pulled.MessageId);
        Assert.Equal(openId, pulled.LastMessageId);
        Assert.Equal(MessageTerminal.Completed, pulled.LastMessageTerminal);
        Assert.Equal(SessionTaskStatus.Completed,
            SessionTaskProjection.Status(pulled, openId!.Value));

        var askedAgain = Expect.Transitioned(
            SessionStateMachine.Apply(
                pulled,
                new RequestInput(Given.IncumbentOf(pulled), InputRequestKind.Question, "again?")),
            SessionState.Working);
        Assert.NotNull(askedAgain.MessageId);
        Assert.NotEqual(openId, askedAgain.MessageId);
        Assert.Equal(openId, askedAgain.LastMessageId);
    }

    [Fact]
    public void Hidden_session_is_not_itself_a_task_status()
    {
        var completed = Given.Session(SessionState.Completed);
        Assert.Null(completed.MessageId);
        Assert.Equal(MessageTerminal.Completed, completed.LastMessageTerminal);
        Assert.Equal(SessionTaskStatus.Completed,
            SessionTaskProjection.Status(completed, completed.LastMessageId!.Value));
    }
}
