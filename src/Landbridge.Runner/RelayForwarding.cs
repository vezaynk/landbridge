using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.Runner;

/// <summary>
/// The landbridged data planes for a relay forward (spec §8.3): the concurrent I/O
/// behind <c>open-forward</c>. On the <b>consumer</b> side it binds a loopback
/// listener, reports the bound port via <c>forward-opened</c>, accepts a
/// connection, opens an authenticated tunnel to the relay, and splices the TCP
/// stream to the WebSocket. On the <b>producer</b> side it opens its tunnel and
/// dials the registered local service, splicing the two together. Either end
/// emits <c>forward-closed</c> when its splice ends (or fails to establish).
///
/// <para>Owned by <see cref="RunnerDaemon"/>: every forward runs as a tracked
/// background task so <see cref="RunnerDaemon.HandleAsync"/> never blocks,
/// <see cref="Close"/> ends one on the plane's command (§8.3 — the splice's authority
/// ends when the owning task leaves <c>working</c>), and <see cref="DisposeAsync"/> tears
/// every live forward down on shutdown / kill-all (§10). It moves opaque bytes without
/// interpreting them (design principle 1).</para>
///
/// <para><b>v1 is one tunnel per forward id.</b> The grant is single-use per role
/// and the relay pairs exactly two ends per id, so the consumer listener accepts
/// <em>exactly one</em> connection. §8.3's "one listener, N tunnels"
/// generalization — a fresh tunnel per accepted connection — is deferred; it slots
/// in at the accept loop below. That deferral is about the <em>listener</em>: §8.4
/// preview already has a per-connection story, by minting a fresh grant and forward id
/// for each browser connection rather than multiplexing one listener, so "deferred"
/// here does not mean no per-connection path exists.</para>
/// </summary>
public sealed class RelayForwarder : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// How long teardown lets the close handshake complete (the relay replies with
    /// a Close frame so this end's final in-flight frame drains) before aborting
    /// the tunnel (spec §8.3). Normally milliseconds — a safety cap against a peer
    /// that never replies, mirroring <c>Landbridge.Relay.ForwardEntry</c>.
    /// </summary>
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the consumer listener waits for its one connection before giving
    /// up and closing (§8.3). Bounded by the grant's remaining TTL — v1 uses a
    /// fixed ceiling matching <c>RelayGrantService.GrantTtl</c> (2 min); tests
    /// override it. A forward nobody connects to must not linger.
    /// </summary>
    public static readonly TimeSpan DefaultAcceptTimeout = TimeSpan.FromMinutes(2);

    private readonly OutboundEventRing _ring;
    private readonly TimeSpan _acceptTimeout;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, LiveForward> _live = new(StringComparer.Ordinal);

    /// <param name="serviceOnPort">
    /// §8.2/§8.3 refuse-at-dial: asks whether a loopback port belongs to a
    /// landbridged-supervised service and, if so, whether that service is currently up.
    /// Returns null when this machine declares no service on the port — which must NOT
    /// be treated as "down", because the port may legitimately belong to a
    /// worker-started listener landbridged knows nothing about.
    /// </param>
    public RelayForwarder(
        OutboundEventRing ring, TimeSpan? acceptTimeout = null, Action<string>? log = null,
        Func<int, bool?>? serviceOnPort = null)
    {
        _ring = ring;
        _acceptTimeout = acceptTimeout ?? DefaultAcceptTimeout;
        _log = log;
        _serviceOnPort = serviceOnPort;
    }

    private readonly Func<int, bool?>? _serviceOnPort;

    /// <summary>Live forward count — exposed so tests/diagnostics can assert no leak.</summary>
    public int ActiveForwardCount => _live.Count;

    /// <summary>
    /// Start one end of a forward and return immediately — the whole splice runs
    /// on a tracked background task so the command handler never blocks (§10). The
    /// role is already validated by the caller; an unknown role never reaches here.
    /// </summary>
    public void Open(OpenForwardCommand command)
    {
        var forwardId = command.ForwardId;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var live = new LiveForward(linked);

        // A forward id is unique per grant, so a collision here means the same
        // command arrived twice; keep the first and drop the duplicate.
        if (!_live.TryAdd(forwardId, live))
        {
            linked.Dispose();
            return;
        }

        live.Runner = Task.Run(async () =>
        {
            string? refusal = null;
            try
            {
                if (command.Role == RelayTunnel.ConsumerRole)
                    await RunConsumerAsync(command, linked.Token);
                else
                    refusal = await RunProducerAsync(command, linked.Token);
            }
            catch (OperationCanceledException)
            {
                // Teardown, not a fault: shutdown (§10) or a commanded close (Close, below)
                // cancels this forward's token, and the accept wait is the one place that
                // surfaces as an exception rather than a return. Swallowed so an ordered
                // close leaves no faulted task behind for nobody to observe — the finally
                // below still reports it closed exactly once.
            }
            finally
            {
                // A forward closing is always reported once, whether it spliced,
                // timed out, or failed to establish (§8.3). A refusal rides along so the
                // consumer is told why rather than just that.
                _ring.Enqueue(new ForwardClosedEvent(command.Session, forwardId, refusal));
                _live.TryRemove(new KeyValuePair<string, LiveForward>(forwardId, live));
                linked.Dispose();
            }
        });
    }

    /// <summary>
    /// End one forward this machine holds (§8.3 <c>close-forward</c>): cancel its own
    /// token, which unwinds whichever state it is in — a consumer still waiting on its
    /// one accept closes its loopback listener, and an established splice tears both
    /// directions down through the ordinary <see cref="SpliceAsync"/> path (close frame
    /// first, then the TCP shutdown that ends the peer's connection). Returns whether
    /// this machine held it, so the daemon's ack says which happened.
    ///
    /// <para><b>Resolved by forward id alone.</b> That is the key <see cref="_live"/> and
    /// the relay's own pairing already use, and it is unique per grant;
    /// <see cref="CloseForwardCommand.Session"/> is correlation, and which end's task it
    /// names depends on which end the plane addressed (see the record's remarks), so
    /// matching on it would import that asymmetry into the runner.</para>
    ///
    /// <para>Cancelling is all this does — the splice reports its own
    /// <c>forward-closed</c> from <see cref="Open"/>'s finally, the same way it does for
    /// a peer drop or a timeout, so a commanded close is not a second reporting path.
    /// The token may already be disposed by a forward finishing in this instant, which is
    /// the same outcome as closing it and is treated as such.</para>
    /// </summary>
    public bool Close(CloseForwardCommand command)
    {
        if (!_live.TryGetValue(command.ForwardId, out var live))
            return false;
        try
        {
            live.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended on its own between the lookup and here; already closed.
            return false;
        }
        _log?.Invoke($"forward {command.ForwardId}: closed on command");
        return true;
    }

    // ── Consumer end ──────────────────────────────────────────────────────────

    private async Task RunConsumerAsync(OpenForwardCommand command, CancellationToken ct)
    {
        // Loopback ONLY — never 0.0.0.0 (§8.3): binding IPAddress.Loopback is the
        // guarantee, asserted so a refactor can't silently widen it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var local = (IPEndPoint)listener.LocalEndpoint;
            Debug.Assert(IPAddress.IsLoopback(local.Address), "consumer listener must bind loopback only (§8.3)");
            var boundPort = local.Port;

            // Report the bound port up so the control plane can return it to the
            // worker (§8.3). Emitted before the accept so open_forward unblocks the
            // moment the address is dialable.
            _ring.Enqueue(new ForwardOpenedEvent(command.Session, command.ForwardId, boundPort));
            _log?.Invoke($"forward {command.ForwardId}: consumer bound 127.0.0.1:{boundPort}");

            Socket accepted;
            try
            {
                using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                acceptCts.CancelAfter(_acceptTimeout);
                // Exactly one connection (v1, one tunnel per forward id). The
                // N-tunnel generalization would loop here, opening a tunnel per accept.
                accepted = await listener.AcceptSocketAsync(acceptCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Nobody connected within the bounded wait — close cleanly (§8.3).
                _log?.Invoke($"forward {command.ForwardId}: consumer accept timed out; closing");
                return;
            }

            // Stop listening the moment we have our one connection.
            listener.Stop();

            using (accepted)
            using (var tcp = new NetworkStream(accepted, ownsSocket: false))
            using (var ws = await OpenTunnelAsync(command, RelayTunnel.ConsumerRole, ct))
            {
                await SpliceAsync(tcp, accepted, ws, ct);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── Producer end ────────────────────────────────────────────────────────────

    private async Task<string?> RunProducerAsync(OpenForwardCommand command, CancellationToken ct)
    {
        // §8.2 refuse-at-dial. A registration can outlive the process it advertises —
        // and §8.2's stated hazard is not the failed connection, it is the SUCCESSFUL
        // one: dial a port whose service has died and something else may have taken it,
        // so the consumer forwards into the wrong stack and gets plausible wrong answers
        // instead of an error. Nobody could close that before, because nobody knew which
        // listener was the intended one. For a landbridged-supervised service, landbridged does.
        // Only a declared-and-down service refuses; an unknown port dials as before,
        // since it may be a worker-started listener that is none of our business.
        if (_serviceOnPort?.Invoke(command.Port) is false)
        {
            var refusal = $"the service backing this registration is not running on 127.0.0.1:{command.Port}";
            _log?.Invoke($"forward {command.ForwardId}: refused — {refusal}");
            return refusal;
        }

        // Dial the registered local service first: a service that died between
        // registration and dial must surface as forward-closed, not a hang (§8.3).
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, command.Port, ct);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            _log?.Invoke($"forward {command.ForwardId}: producer dial of 127.0.0.1:{command.Port} failed: {e.Message}");
            return null; // finally emits forward-closed
        }

        using (socket)
        using (var tcp = new NetworkStream(socket, ownsSocket: false))
        using (var ws = await OpenTunnelAsync(command, RelayTunnel.ProducerRole, ct))
        {
            _log?.Invoke($"forward {command.ForwardId}: producer dialed 127.0.0.1:{command.Port}, tunnel open");
            await SpliceAsync(tcp, socket, ws, ct);
        }

        return null;
    }

    // ── Tunnel + splice ──────────────────────────────────────────────────────────

    private static async Task<ClientWebSocket> OpenTunnelAsync(
        OpenForwardCommand command, string role, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(RelayTunnel.ForwardIdHeader, command.ForwardId);
        ws.Options.SetRequestHeader(RelayTunnel.GrantHeader, command.Grant);
        ws.Options.SetRequestHeader(RelayTunnel.RoleHeader, role);
        try
        {
            await ws.ConnectAsync(TunnelUri(command.RelayUrl), ct);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary><c>http(s)://host:port</c> → <c>ws(s)://host:port/tunnel</c>.</summary>
    private static Uri TunnelUri(string relayUrl)
    {
        var b = new UriBuilder(relayUrl)
        {
            Scheme = relayUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/tunnel",
        };
        return b.Uri;
    }

    /// <summary>
    /// Splice a TCP stream to the relay WebSocket, both directions concurrently
    /// until either side closes or errors, then tear both down — the ForwardEntry
    /// pump/teardown pattern adapted to one TCP side (§8.3). 64 KB buffers; opaque
    /// binary frames.
    /// </summary>
    private static async Task SpliceAsync(NetworkStream tcp, Socket socket, WebSocket ws, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tcpToWs = PumpTcpToWsAsync(tcp, ws, linked.Token);
        var wsToTcp = PumpWsToTcpAsync(ws, tcp, linked.Token);

        // Whichever direction ends first triggers teardown of both.
        await Task.WhenAny(tcpToWs, wsToTcp);

        // Close the WS output so the relay observes a close frame — flushing this
        // direction's final in-flight frame first — then let both pumps complete
        // the close handshake BEFORE aborting anything. Cancelling an in-flight
        // WebSocket op aborts the socket and RSTs the tunnel, discarding a final
        // frame still in flight to the relay (e.g. the tail of a producer service's
        // response, or a proxied websocket's close frame) — the same §8.3 teardown
        // hazard fixed in Landbridge.Relay.ForwardEntry. Bounded so a stuck peer cannot
        // hang teardown; do NOT collapse this back to an immediate cancel.
        await CloseWebSocketQuietlyAsync(ws);
        var drained = Task.WhenAll(tcpToWs, wsToTcp);
        await Task.WhenAny(drained, Task.Delay(CloseHandshakeTimeout, CancellationToken.None));
        try { socket.Shutdown(SocketShutdown.Both); } catch { /* already torn down */ }
        await linked.CancelAsync();

        try { await drained; }
        catch (Exception) { /* expected only if a stuck peer forced the abort above */ }
    }

    private static async Task PumpTcpToWsAsync(NetworkStream from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, ct);
                if (read == 0)
                    return; // TCP EOF
                await to.SendAsync(
                    new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException) { /* teardown */ }
        catch (Exception e) when (e is WebSocketException or IOException or SocketException or ObjectDisposedException)
        {
            // Peer dropped or the socket was aborted; end this direction.
        }
    }

    private static async Task PumpWsToTcpAsync(WebSocket from, NetworkStream to, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var result = await from.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                await to.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            }
        }
        catch (OperationCanceledException) { /* teardown */ }
        catch (Exception e) when (e is WebSocketException or IOException or SocketException or ObjectDisposedException)
        {
            // Peer dropped or the socket was aborted; end this direction.
        }
    }

    private static async Task CloseWebSocketQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "relay: peer closed", cts.Token);
        }
        catch (Exception)
        {
            // Best-effort: the peer may already be gone, or the socket aborted.
        }
    }

    /// <summary>
    /// Tear every live forward down (§10 — the daemon's shutdown / kill-all also
    /// takes tunnels down). Cancels all splice tasks and awaits their teardown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        var runners = _live.Values.Select(f => f.Runner).Where(t => t is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(runners); }
        catch (Exception) { /* teardown races are expected */ }
        _cts.Dispose();
    }

    /// <summary>
    /// One live forward: its splice task and — the part that makes <see cref="Close"/>
    /// possible — its <em>own</em> cancellation source. Before this the source was a local
    /// that only the splice itself could see, so the finest handle anything had was the
    /// class-wide <c>_cts</c> <see cref="DisposeAsync"/> cancels, and the finest act
    /// available was taking every forward on the machine down at once.
    /// </summary>
    private sealed class LiveForward(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task? Runner { get; set; }
    }
}
