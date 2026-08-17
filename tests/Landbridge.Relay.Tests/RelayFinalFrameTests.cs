using System.Net.WebSockets;
using Landbridge.Contracts;

namespace Landbridge.Relay.Tests;

/// <summary>
/// Regression for the close-teardown final-frame drop (spec §8.3): a forward must
/// deliver a peer's final in-flight frame before the splice tears down, rather than
/// aborting the tunnel and truncating it. The old teardown did
/// <c>CloseOutput</c>-then-immediate-<c>CancelAsync</c>; cancelling a pump's
/// in-flight WebSocket receive aborts the socket (RSTs the connection), discarding
/// a just-sent final frame whose bytes are still in flight. The far-end symptom was
/// "closed without completing the close handshake" — affecting ANY forward (a
/// proxied websocket close, or the tail of a Postgres result), which is why the fix
/// and this guard live at the relay level.
/// </summary>
public sealed class RelayFinalFrameTests
{
    /// <summary>
    /// Send a final frame then close, across many rapid forwards, and require that
    /// EVERY one delivers the frame + a clean close. The race only loses when the
    /// abort catches the final frame in flight — a narrow, scheduling-dependent
    /// window on loopback — so this drives it repeatedly to force the window open;
    /// the fixed teardown (complete the close handshake before aborting) makes all
    /// iterations clean.
    /// </summary>
    [Fact]
    public async Task Final_frame_before_close_is_never_truncated()
    {
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Accept);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var payload = RelayTestKit.RandomBytes(4096);

        for (var i = 0; i < 200; i++)
        {
            var forwardId = Guid.NewGuid().ToString();
            using var consumer = await host.ConnectAsync(
                forwardId, RelayTestKit.GoodGrant, RelayTunnel.ConsumerRole, ct);
            using var producer = await host.ConnectAsync(
                forwardId, RelayTestKit.GoodGrant, RelayTunnel.ProducerRole, ct);

            // Producer: final frame, then immediately close — "final frame then close".
            await RelayTestKit.SendAsync(producer, payload, ct);
            await producer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);

            // Read the final frame only AFTER the producer has closed, so the relay's
            // teardown races the delivery — the shape that lost the tail.
            var received = await RelayTestKit.ReceiveExactlyAsync(consumer, payload.Length, ct);
            RelayTestKit.AssertBytesEqual(payload, received);

            var tail = await consumer.ReceiveAsync(new byte[16], ct);
            Assert.Equal(WebSocketMessageType.Close, tail.MessageType);
        }
    }
}
