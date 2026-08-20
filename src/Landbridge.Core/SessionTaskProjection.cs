namespace Landbridge.Core;

/// <summary>
/// MCP Tasks statuses as a projection of one message envelope, not the session.
/// Sessions have no terminal state. The envelope does: returning to
/// <see cref="MessageState.Idle"/> completes or cancels that MCP task, and the
/// next exchange mints a new <see cref="SessionRecord.MessageId"/>.
/// </summary>
public enum SessionTaskStatus
{
    Working,
    InputRequired,
    Completed,
    Cancelled,
}

/// <summary>
/// Maps the outstanding (or last) Lead↔worker envelope onto MCP Tasks
/// (<c>io.modelcontextprotocol/tasks</c>). Task id is the envelope id, never
/// the session id.
/// </summary>
public static class SessionTaskProjection
{
    public const string ExtensionId = "io.modelcontextprotocol/tasks";
    public const int DefaultPollIntervalMs = 5_000;

    public static SessionTaskStatus? Status(SessionRecord session, Guid taskId)
    {
        if (session.MessageId == taskId)
            return LiveStatus(session.MessageState);
        if (session.LastMessageId == taskId)
            return ClosedStatus(session.LastMessageTerminal);
        return null;
    }

    public static SessionTaskStatus LiveStatus(MessageState state) => state switch
    {
        MessageState.AwaitingLead
            or MessageState.AwaitingPermission
            or MessageState.AwaitingReport => SessionTaskStatus.InputRequired,
        MessageState.AwaitingPull => SessionTaskStatus.Working,
        _ => SessionTaskStatus.Working,
    };

    public static SessionTaskStatus ClosedStatus(MessageTerminal? terminal) =>
        terminal == MessageTerminal.Cancelled
            ? SessionTaskStatus.Cancelled
            : SessionTaskStatus.Completed;

    public static string WireStatus(SessionTaskStatus status) => status switch
    {
        SessionTaskStatus.Working => "working",
        SessionTaskStatus.InputRequired => "input_required",
        SessionTaskStatus.Completed => "completed",
        SessionTaskStatus.Cancelled => "cancelled",
        _ => "working",
    };

    public static string? LiveStatusMessage(MessageState state) => state switch
    {
        MessageState.AwaitingPermission =>
            "permission wait; answer_permission_request",
        MessageState.AwaitingLead =>
            "waiting on the Lead; get_session_question then answer_input_request",
        MessageState.AwaitingReport =>
            "report ready; get_session_report, then reply or submit_review to close",
        MessageState.AwaitingPull =>
            "worker pulling get_session",
        _ => null,
    };

    public static string? ClosedStatusMessage(MessageTerminal? terminal, MessageVerdict? verdict)
    {
        if (terminal == MessageTerminal.Cancelled)
            return "cancelled";
        return verdict switch
        {
            MessageVerdict.Accepted => "accepted",
            MessageVerdict.Discarded => "discarded",
            _ => null,
        };
    }
}
