using System.Net.WebSockets;

namespace Docket.Relay;

/// <summary>
/// One forward id's pairing slot and splice (spec §8.3). Holds the consumer and
/// producer tunnels for a single forward id; once both are present it splices
/// them — copying consumer→producer and producer→consumer concurrently until a
/// side closes or errors, then tearing both down. Created by
/// <see cref="ForwardRegistry"/> and owned by it; all mutation is serialized
/// through <see cref="Gate"/>.
/// </summary>
public sealed class ForwardEntry
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// How long teardown lets the close handshake complete — both ends replying
    /// with a Close frame so each side's final in-flight frame drains — before
    /// aborting the sockets (spec §8.3). Normally satisfied in milliseconds; it is
    /// a safety cap against a peer that never replies, not a delay the happy path
    /// pays.
    /// </summary>
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Serializes claim/socket/removal mutation. Held by the registry too.</summary>
    internal readonly object Gate = new();

    /// <summary>Set under <see cref="Gate"/> when the registry evicts this entry.</summary>
    internal bool Removed;

    private bool _consumerClaimed;
    private bool _producerClaimed;
    private WebSocket? _consumer;
    private WebSocket? _producer;

    // Completes once BOTH sockets are set; the pairing signal a waiting first
    // arrival parks on. Runs continuations asynchronously so a SetSocket caller
    // never runs the waiter's continuation inline under the gate.
    private readonly TaskCompletionSource _bothPresent =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The single splice task, started lazily and shared by both handlers so the
    // copy loops run exactly once no matter which side awaits first.
    private Task? _splice;

    /// <summary>
    /// Reserve the <paramref name="role"/> slot; returns false if that role is
    /// already claimed for this forward id (the duplicate must be rejected,
    /// spec §8.3). Caller must hold <see cref="Gate"/>.
    /// </summary>
    internal bool TryClaim(RelayRole role)
    {
        switch (role)
        {
            case RelayRole.Consumer:
                if (_consumerClaimed) return false;
                _consumerClaimed = true;
                return true;
            case RelayRole.Producer:
                if (_producerClaimed) return false;
                _producerClaimed = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Attach an accepted socket to its (already-claimed) role slot.</summary>
    public void SetSocket(RelayRole role, WebSocket socket)
    {
        lock (Gate)
        {
            if (role == RelayRole.Consumer)
                _consumer = socket;
            else
                _producer = socket;

            if (_consumer is not null && _producer is not null)
                _bothPresent.TrySetResult();
        }
    }

    /// <summary>
    /// Wait for the pair to arrive. Returns true when both sockets are present,
    /// false if <paramref name="timeout"/> elapses first (the caller then closes
    /// cleanly). Throws only if <paramref name="ct"/> fires (the request aborted).
    /// </summary>
    public async Task<bool> WaitForPairAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _bothPresent.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Splice the two tunnels. Idempotent: the first caller starts the copy
    /// loops, and every caller awaits the same task, so both request handlers
    /// stay alive (holding their sockets open) until the splice ends.
    /// </summary>
    public Task SpliceAsync(CancellationToken ct)
    {
        lock (Gate)
            return _splice ??= SpliceCoreAsync(ct);
    }

    private async Task SpliceCoreAsync(CancellationToken ct)
    {
        // Both are non-null: SpliceAsync is only reached after WaitForPairAsync
        // returned true, which gates on both sockets being set.
        var consumer = _consumer!;
        var producer = _producer!;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var toProducer = PumpAsync(consumer, producer, linked.Token);
        var toConsumer = PumpAsync(producer, consumer, linked.Token);

        // The splice persists until one direction ends — a side closed or
        // errored (spec §8.3: an established splice is never severed mid-flight
        // by grant expiry; the working-state teardown signal is a later
        // increment). Whichever finishes first triggers teardown of both.
        await Task.WhenAny(toProducer, toConsumer);

        // Signal close to BOTH peers. CloseOutputAsync flushes each direction's
        // final buffered frame to the transport and then writes a Close frame —
        // the start of the WebSocket close handshake toward each end.
        await RelaySocket.CloseOutputQuietlyAsync(consumer, "relay: peer closed");
        await RelaySocket.CloseOutputQuietlyAsync(producer, "relay: peer closed");

        // Let BOTH pumps finish by observing their peer's reply Close — i.e.
        // complete the close handshake — BEFORE aborting anything. This is
        // load-bearing, not cosmetic: cancelling a pump's in-flight WebSocket
        // ReceiveAsync *aborts* that WebSocket (documented .NET behaviour), which
        // RSTs the TCP connection and discards bytes we just flushed above but the
        // OS has not yet transmitted — the forward's final frame. Under load that
        // lost final frame surfaces at the far end as "closed without completing
        // the close handshake" (e.g. a proxied websocket close, or the tail of a
        // Postgres result). Waiting for the reply Close lets the flushed frame
        // drain first. PumpAsync never throws (it swallows cancellation and
        // WebSocket errors), so this WhenAll completes normally once both ends
        // close. Bounded so a peer that never replies cannot hang teardown — do
        // NOT "simplify" this back to an immediate cancel.
        var drained = Task.WhenAll(toProducer, toConsumer);
        await Task.WhenAny(drained, Task.Delay(CloseHandshakeTimeout, CancellationToken.None));

        // Last resort only: a peer that never sent its reply Close within the
        // window. Abort to reclaim the sockets — the final frame was already
        // flushed above, so this fires for a genuinely stuck peer, not the happy path.
        await linked.CancelAsync();
        try
        {
            await drained;
        }
        catch (Exception)
        {
            // Expected only if a stuck peer forced the abort above.
        }
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var result = await from.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                // ── byte-counter / rate-limit seam (spec §8.3, enforcement §9.10) ──
                // DEFERRED. A per-Team byte counter and forward rate limit would
                // wrap this copy: meter result.Count and gate on the owning Team's
                // byte allowance before forwarding. Not implemented in this
                // increment — the relay moves opaque bytes without interpreting
                // them (design principle 1). This is the single choke point where
                // that metering will live.
                await to.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType, result.EndOfMessage, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Teardown: the opposite direction ended and cancelled us.
        }
        catch (WebSocketException)
        {
            // The peer dropped or the socket was aborted; end this direction.
        }
    }
}
