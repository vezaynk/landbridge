using Landbridge.Core;

namespace Landbridge.Runner.Tests;

/// <summary>The bounded runner→control-plane buffer, spec §10 buffering:
/// drop-oldest with a gap marker.</summary>
public class OutboundEventRingTests
{
    private static ToolCallEvent Event(SessionId task, int seq) =>
        new(task, seq.ToString(), DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Drops_oldest_and_records_the_gap_on_the_next_survivor()
    {
        var task = SessionId.New();
        var ring = new OutboundEventRing(capacity: 3);

        for (var i = 1; i <= 6; i++)
            ring.Enqueue(Event(task, i));
        ring.Complete();

        var drained = new List<RingItem>();
        await foreach (var item in ring.ReadAllAsync(CancellationToken.None))
            drained.Add(item);

        // Drop-oldest keeps the newest capacity items (§10).
        Assert.Equal(new[] { "4", "5", "6" }, drained.Select(d => ((ToolCallEvent)d.Event).Tool));
        // The gap marker: three events were dropped before the first survivor.
        Assert.Equal(3, drained[0].GapBefore);
        Assert.Equal(0, drained[1].GapBefore);
        Assert.Equal(0, drained[2].GapBefore);
        Assert.Equal(3, ring.DroppedCount);
    }

    [Fact]
    public async Task Within_capacity_nothing_drops_and_order_is_preserved()
    {
        var task = SessionId.New();
        var ring = new OutboundEventRing(capacity: 8);

        ring.Enqueue(Event(task, 1));
        ring.Enqueue(Event(task, 2));
        ring.Complete();

        var drained = new List<RingItem>();
        await foreach (var item in ring.ReadAllAsync(CancellationToken.None))
            drained.Add(item);

        Assert.Equal(new[] { "1", "2" }, drained.Select(d => ((ToolCallEvent)d.Event).Tool));
        Assert.All(drained, d => Assert.Equal(0, d.GapBefore));
        Assert.Equal(0, ring.DroppedCount);
    }
}
