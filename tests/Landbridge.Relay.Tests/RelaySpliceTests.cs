using System.Net;
using System.Net.WebSockets;
using static Landbridge.Relay.Tests.RelayTestKit;

namespace Landbridge.Relay.Tests;

/// <summary>
/// The relay splice core over the wire (spec §8.3): the real relay host on
/// loopback Kestrel, driven by <see cref="ClientWebSocket"/> pairs. Covers grant
/// validation at tunnel-open, pairing by forward id, bidirectional splicing with
/// streaming fidelity, and clean teardown with no forward-id leak.
/// </summary>
public sealed class RelaySpliceTests
{
    private static CancellationToken Timeout(out CancellationTokenSource cts)
    {
        cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return cts.Token;
    }

    [Fact]
    public async Task Happy_path_splices_both_directions_with_streaming_fidelity()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Accept);
        const string forwardId = "fwd-happy";

        var consumer = await host.ConnectAsync(forwardId, GoodGrant, "consumer", ct);
        var producer = await host.ConnectAsync(forwardId, GoodGrant, "producer", ct);

        // consumer→producer as one large (>64 KiB) message: proves the relay
        // streams a payload that fragments across its receive buffer.
        var fromConsumer = RandomBytes(256 * 1024);
        // producer→consumer as many back-to-back messages: proves multi-write.
        var fromProducer = RandomBytes(300 * 1024);

        var sendC = SendAsync(consumer, fromConsumer, ct);
        var sendP = SendChunkedAsync(producer, fromProducer, 33 * 1024, ct);
        var recvOnConsumer = ReceiveExactlyAsync(consumer, fromProducer.Length, ct);
        var recvOnProducer = ReceiveExactlyAsync(producer, fromConsumer.Length, ct);

        await Task.WhenAll(sendC, sendP);
        AssertBytesEqual(fromProducer, await recvOnConsumer);
        AssertBytesEqual(fromConsumer, await recvOnProducer);

        await ShutdownAsync(consumer, ct);
        await ShutdownAsync(producer, ct);

        // Teardown removes the entry — no forward-id leak.
        Assert.True(
            await WaitForAsync(() => host.Registry.ActiveForwardCount == 0, TimeSpan.FromSeconds(10)),
            "forward-id entry leaked after the splice closed");
    }

    [Fact]
    public async Task Invalid_grant_is_refused_before_any_splice()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Reject);

        var status = await host.ConnectExpectingRefusalAsync("fwd-bad", "whatever", "consumer", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        // A rejected grant never reaches pairing, so no entry is created.
        Assert.Equal(0, host.Registry.ActiveForwardCount);
    }

    [Fact]
    public async Task Unpaired_tunnel_closes_cleanly_within_the_bounded_wait()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;
        await using var host = await RelayTestHost.StartAsync(
            DelegateGrantValidator.Accept, pairWaitTimeout: TimeSpan.FromSeconds(1));
        const string forwardId = "fwd-lonely";

        var consumer = await host.ConnectAsync(forwardId, GoodGrant, "consumer", ct);

        // The pair never arrives; the relay closes this side once the bounded
        // wait elapses — no hang.
        var buffer = new byte[256];
        var result = await consumer.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);

        Assert.True(
            await WaitForAsync(() => host.Registry.ActiveForwardCount == 0, TimeSpan.FromSeconds(10)),
            "unpaired forward-id entry leaked");

        consumer.Dispose();
    }

    [Fact]
    public async Task Duplicate_role_is_rejected_and_the_registry_does_not_wedge()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Accept);
        const string forwardId = "fwd-dup";

        // First consumer holds the slot and waits for its pair.
        var consumer = await host.ConnectAsync(forwardId, GoodGrant, "consumer", ct);

        // A second consumer for the same forward id is refused.
        var status = await host.ConnectExpectingRefusalAsync(forwardId, GoodGrant, "consumer", ct);
        Assert.Equal(HttpStatusCode.Conflict, status);

        // The registry is not wedged: the legitimate producer still pairs and
        // splices with the first consumer.
        var producer = await host.ConnectAsync(forwardId, GoodGrant, "producer", ct);

        var ping = RandomBytes(4096);
        var sendPing = SendAsync(consumer, ping, ct);
        var got = await ReceiveExactlyAsync(producer, ping.Length, ct);
        await sendPing;
        AssertBytesEqual(ping, got);

        await ShutdownAsync(consumer, ct);
        await ShutdownAsync(producer, ct);

        Assert.True(
            await WaitForAsync(() => host.Registry.ActiveForwardCount == 0, TimeSpan.FromSeconds(10)),
            "forward-id entry leaked after the splice closed");
    }

    [Fact]
    public async Task Forward_id_is_reusable_after_a_splice_closes()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Accept);
        const string forwardId = "fwd-reuse";

        for (var round = 0; round < 2; round++)
        {
            var consumer = await host.ConnectAsync(forwardId, GoodGrant, "consumer", ct);
            var producer = await host.ConnectAsync(forwardId, GoodGrant, "producer", ct);

            var payload = RandomBytes(8192);
            var send = SendAsync(producer, payload, ct);
            var got = await ReceiveExactlyAsync(consumer, payload.Length, ct);
            await send;
            AssertBytesEqual(payload, got);

            await ShutdownAsync(consumer, ct);
            await ShutdownAsync(producer, ct);

            Assert.True(
                await WaitForAsync(() => host.Registry.ActiveForwardCount == 0, TimeSpan.FromSeconds(10)),
                $"forward-id entry leaked after round {round}");
        }
    }

    [Fact]
    public async Task Concurrent_forward_ids_splice_independently()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Accept);

        async Task RunForwardAsync(string forwardId)
        {
            var consumer = await host.ConnectAsync(forwardId, GoodGrant, "consumer", ct);
            var producer = await host.ConnectAsync(forwardId, GoodGrant, "producer", ct);

            var payload = RandomBytes(16 * 1024);
            var send = SendAsync(consumer, payload, ct);
            var got = await ReceiveExactlyAsync(producer, payload.Length, ct);
            await send;
            AssertBytesEqual(payload, got);

            await ShutdownAsync(consumer, ct);
            await ShutdownAsync(producer, ct);
        }

        await Task.WhenAll(
            RunForwardAsync("fwd-a"),
            RunForwardAsync("fwd-b"),
            RunForwardAsync("fwd-c"));

        Assert.True(
            await WaitForAsync(() => host.Registry.ActiveForwardCount == 0, TimeSpan.FromSeconds(10)),
            "a forward-id entry leaked after concurrent splices closed");
    }
}
