namespace Landbridge.Core;

/// <summary>
/// MCP Tasks statuses as a <em>projection</em> of occupancy + the message machine.
/// The session row stays the source of truth. MCP terminal statuses are only
/// emitted for <c>hidden</c> rows so a retryable <c>health=failed</c> session is
/// never a terminal MCP task.
/// </summary>
public enum SessionTaskStatus
{
    Working,
    InputRequired,
    Completed,
    Cancelled,
}

/// <summary>
/// Maps a Landbridge session onto the MCP Tasks lifecycle
/// (<c>io.modelcontextprotocol/tasks</c>). Polling is <c>tasks/get</c>; writes
/// stay the existing Lead tools. Health-failed is <see cref="SessionTaskStatus.Working"/>
/// with a status message, not MCP <c>failed</c>, because same-id retry is legal.
/// </summary>
public static class SessionTaskProjection
{
    public const string ExtensionId = "io.modelcontextprotocol/tasks";
    public const int DefaultPollIntervalMs = 5_000;

    public static SessionTaskStatus Status(SessionRecord session)
    {
        if (session.Hidden)
        {
            return session.MessageVerdict is MessageVerdict.Accepted or MessageVerdict.Discarded
                ? SessionTaskStatus.Completed
                : SessionTaskStatus.Cancelled;
        }

        return session.MessageState switch
        {
            MessageState.AwaitingLead
                or MessageState.AwaitingPermission
                or MessageState.AwaitingReport => SessionTaskStatus.InputRequired,
            _ => SessionTaskStatus.Working,
        };
    }

    public static string WireStatus(SessionTaskStatus status) => status switch
    {
        SessionTaskStatus.Working => "working",
        SessionTaskStatus.InputRequired => "input_required",
        SessionTaskStatus.Completed => "completed",
        SessionTaskStatus.Cancelled => "cancelled",
        _ => "working",
    };

    public static string? StatusMessage(SessionRecord session)
    {
        if (session.Hidden)
        {
            return session.MessageVerdict switch
            {
                MessageVerdict.Accepted => "accepted",
                MessageVerdict.Discarded => "discarded",
                _ => "cancelled",
            };
        }

        if (session.Health == SessionHealth.Failed)
            return "mechanical failure; retry with answer_input_request on this session id";

        if (session.OccupancyDesired == Occupancy.OnDisk)
            return "occupancy released (desired=on_disk)";

        return session.MessageState switch
        {
            MessageState.AwaitingPermission =>
                "permission wait; answer_permission_request",
            MessageState.AwaitingLead =>
                "waiting on the Lead; get_session_question then answer_input_request",
            MessageState.AwaitingReport =>
                "report ready; get_session_report then submit_review",
            MessageState.AwaitingPull =>
                "worker pulling get_session",
            _ => null,
        };
    }
}
