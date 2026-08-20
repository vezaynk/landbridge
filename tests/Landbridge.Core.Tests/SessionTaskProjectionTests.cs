namespace Landbridge.Core.Tests;

public sealed class SessionTaskProjectionTests
{
    [Fact]
    public void Submitted_and_working_project_as_working()
    {
        Assert.Equal(SessionTaskStatus.Working, SessionTaskProjection.Status(Given.Session()));
        Assert.Equal(SessionTaskStatus.Working,
            SessionTaskProjection.Status(Given.Session(SessionState.Working)));
    }

    [Fact]
    public void Lead_owes_a_move_projects_as_input_required()
    {
        Assert.Equal(SessionTaskStatus.InputRequired,
            SessionTaskProjection.Status(Given.Session(SessionState.BlockedOnInput)));
        Assert.Equal(SessionTaskStatus.InputRequired,
            SessionTaskProjection.Status(Given.Session(SessionState.Verifying)));
        Assert.Equal(SessionTaskStatus.InputRequired,
            SessionTaskProjection.Status(Given.Session(SessionState.Working, message: MessageState.AwaitingLead)));
    }

    [Fact]
    public void Accept_and_discard_are_completed_not_failed()
    {
        Assert.Equal(SessionTaskStatus.Completed,
            SessionTaskProjection.Status(Given.Session(SessionState.Completed)));
        Assert.Equal(SessionTaskStatus.Completed,
            SessionTaskProjection.Status(Given.Session(SessionState.Rejected)));
        Assert.Equal("accepted",
            SessionTaskProjection.StatusMessage(Given.Session(SessionState.Completed)));
        Assert.Equal("discarded",
            SessionTaskProjection.StatusMessage(Given.Session(SessionState.Rejected)));
    }

    [Fact]
    public void Cancel_projects_as_cancelled()
    {
        Assert.Equal(SessionTaskStatus.Cancelled,
            SessionTaskProjection.Status(Given.Session(SessionState.Canceled)));
    }

    [Fact]
    public void Mechanical_failure_stays_working_so_same_id_retry_is_not_a_terminal_task()
    {
        var failed = Given.Session(SessionState.Failed);
        Assert.Equal(SessionTaskStatus.Working, SessionTaskProjection.Status(failed));
        Assert.Contains("mechanical failure", SessionTaskProjection.StatusMessage(failed),
            StringComparison.Ordinal);
        Assert.Equal("working", SessionTaskProjection.WireStatus(SessionTaskStatus.Working));
    }

    [Fact]
    public void Deactivated_occupancy_stays_working()
    {
        var parked = Given.Session(SessionState.Parked);
        Assert.Equal(SessionTaskStatus.Working, SessionTaskProjection.Status(parked));
        Assert.Contains("on_disk", SessionTaskProjection.StatusMessage(parked),
            StringComparison.Ordinal);
    }
}
