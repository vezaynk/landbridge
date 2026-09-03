using Landbridge.Core;

namespace Landbridge.ControlPlane.Tests;

public sealed class EventLogDetailTests
{
    [Fact]
    public void DeliverReport_describes_unread_to_read()
    {
        var before = Rec(reportUnread: true);
        var after = before with { ReportUnread = false };
        var text = EventLogDetail.Describe(
            new DeliverReport(new LeadClaim(before.Team)), before, after, []);
        Assert.Equal("unread to read", text);
    }

    [Fact]
    public void PullReceipt_describes_the_message_transition()
    {
        var before = Rec(message: MessageState.AwaitingPull);
        var after = before with { MessageState = MessageState.Idle };
        var text = EventLogDetail.Describe(
            new PullReceipt(new WorkerCaller(before.Team, before.Id, WorkerInstanceId.New())),
            before, after, []);
        Assert.Equal("AwaitingPull to Idle", text);
    }

    [Fact]
    public void ObserveOccupancy_names_the_observed_value_when_nothing_moved()
    {
        var rec = Rec(observed: Occupancy.Running);
        var text = EventLogDetail.Describe(
            new ObserveOccupancy(Occupancy.Running), rec, rec, []);
        Assert.Equal("observed Running", text);
    }

    [Fact]
    public void Effects_are_the_detail_when_occupancy_did_not_move()
    {
        var rec = Rec();
        var instance = WorkerInstanceId.New();
        var text = EventLogDetail.Describe(
            new Dispatch(
                new MachineSnapshot("box-1", true, false, new HashSet<string> { "default" }),
                instance),
            rec, rec with { CurrentInstance = instance, Attempt = 1 },
            [new MintWorkerInstanceToken(instance, "box-1")]);
        Assert.Equal("MintWorkerInstanceToken", text);
    }

    private static SessionRecord Rec(
        Occupancy observed = Occupancy.None,
        MessageState message = MessageState.Idle,
        bool reportUnread = false)
        => new()
        {
            Id = SessionId.New(),
            Team = TeamId.New(),
            Namespace = "ns",
            OccupancyDesired = Occupancy.Running,
            OccupancyObserved = observed,
            MessageState = message,
            ReportUnread = reportUnread,
        };
}
