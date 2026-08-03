using Docket.Contracts;
using Docket.Core;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The §12 transcript rendezvous in isolation: a registered request is completed by its
/// reply, an unmatched reply is dropped rather than erroring, and disposing deregisters so
/// the table cannot leak across a long-lived plane.
/// </summary>
public sealed class TranscriptWaitersTests
{
    private static TranscriptChunkEvent Reply(string requestId, string text = "chunk") =>
        new(TaskId.New(), requestId, Text: text, NextOffset: text.Length);

    [Fact]
    public async Task A_reply_completes_the_waiter_registered_for_its_request_id()
    {
        var waiters = new TranscriptWaiters();
        using var waiter = waiters.Register("req-1");

        Assert.True(waiters.Complete(Reply("req-1", "the bytes")));

        Assert.Equal("the bytes", (await waiter.Reply).Text);
    }

    [Fact]
    public void A_reply_nobody_is_waiting_for_is_dropped()
    {
        // A late arrival after the requester gave up (timed out, or the operator navigated
        // away). There is nothing to correlate it to, and it is not an error.
        var waiters = new TranscriptWaiters();

        Assert.False(waiters.Complete(Reply("req-nobody")));

        using var other = waiters.Register("req-1");
        Assert.False(waiters.Complete(Reply("req-different")));
        Assert.False(other.Reply.IsCompleted);
    }

    [Fact]
    public void Disposing_deregisters_so_the_table_never_leaks()
    {
        var waiters = new TranscriptWaiters();

        using (var waiter = waiters.Register("req-1"))
        {
            Assert.Equal(1, waiters.OutstandingCount);
            _ = waiter.Reply;
        }

        Assert.Equal(0, waiters.OutstandingCount);
        // And a reply arriving after the waiter is gone is simply dropped.
        Assert.False(waiters.Complete(Reply("req-1")));
    }

    [Fact]
    public void Completing_is_synchronous_and_never_blocks_on_the_reader()
    {
        // Load-bearing: Complete runs on the runner socket's receive loop, so if it could
        // block on whoever is reading the reply it would stall heartbeats behind it and the
        // liveness scan would start requeueing live tasks. Nobody has awaited this waiter
        // yet, and Complete must still return immediately.
        var waiters = new TranscriptWaiters();
        using var waiter = waiters.Register("req-1");

        Assert.True(waiters.Complete(Reply("req-1")));
        Assert.True(waiter.Reply.IsCompleted);
    }

    [Fact]
    public async Task Two_concurrent_reads_are_correlated_by_request_id_not_by_order()
    {
        var waiters = new TranscriptWaiters();
        using var first = waiters.Register("req-1");
        using var second = waiters.Register("req-2");

        // Answered out of order, as two machines would.
        waiters.Complete(Reply("req-2", "second"));
        waiters.Complete(Reply("req-1", "first"));

        Assert.Equal("first", (await first.Reply).Text);
        Assert.Equal("second", (await second.Reply).Text);
    }
}
