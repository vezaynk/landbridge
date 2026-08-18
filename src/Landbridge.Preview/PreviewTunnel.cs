using System.Net.WebSockets;
using Landbridge.Contracts;

namespace Landbridge.Preview;

/// <summary>
/// The consumer end of a relay tunnel, as dialed by the preview frontend (spec
/// §8.4). The frontend <em>is</em> the consumer (there is no consumer
/// <c>landbridged</c>): it dials the relay's unchanged <c>/tunnel</c> presenting the
/// consumer role + grant + forward id, replays the browser's already-read request
/// head into the tunnel, and byte-splices the browser connection to the tunnel for
/// the connection's life — HTTP request/response and a WebSocket upgrade alike, as
/// opaque bytes. The splice pumps mirror <c>Landbridge.Runner.RelayForwarder</c>'s
/// proven pattern (a fresh dependency-free copy — the frontend does not reference
/// the runner), adapted to a browser <see cref="Stream"/> on one side.
///
/// <para>A <b>first-upstream-byte gate</b> turns the §8.4 failure modes into clean
/// statuses instead of hangs: a producer that never dials shows up as the relay
/// closing the consumer end with no pair (→ <see cref="ProxyOutcome.NoUpstream"/>,
/// 502) or as silence past the timeout (→ <see cref="ProxyOutcome.Timeout"/>, 504).
/// Nothing is written to the browser until the first upstream byte arrives, so the
/// handler can still send an error response on either failure.</para>
/// </summary>
public sealed class PreviewTunnel
{
    private const int BufferSize = 64 * 1024;

    /// <summary>Dial the relay's <c>/tunnel</c> as the consumer for <paramref name="forwardId"/> (§8.4).</summary>
    public static async Task<ClientWebSocket> DialConsumerAsync(
        string relayUrl, string forwardId, string grant, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(RelayTunnel.ForwardIdHeader, forwardId);
        ws.Options.SetRequestHeader(RelayTunnel.GrantHeader, grant);
        ws.Options.SetRequestHeader(RelayTunnel.RoleHeader, RelayTunnel.ConsumerRole);
        try
        {
            await ws.ConnectAsync(TunnelUri(relayUrl), ct);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary><c>http(s)://host:port</c> → <c>ws(s)://host:port/tunnel</c> (mirrors the runner's tunnel client).</summary>
    public static Uri TunnelUri(string relayUrl)
    {
        var b = new UriBuilder(relayUrl)
        {
            Scheme = relayUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/tunnel",
        };
        return b.Uri;
    }

    /// <summary>
    /// Replay <paramref name="initialBrowserBytes"/> (the request head + any bytes
    /// read past it) into the tunnel, then splice <paramref name="browser"/> ↔
    /// <paramref name="ws"/> bidirectionally until either side ends — gated on the
    /// first upstream byte (§8.4). Returns the outcome; on
    /// <see cref="ProxyOutcome.Completed"/> the browser has already seen the full
    /// response, on the failures nothing was written to it.
    /// </summary>
    public static async Task<ProxyOutcome> ProxyAsync(
        Stream browser, WebSocket ws, ReadOnlyMemory<byte> initialBrowserBytes,
        TimeSpan firstByteTimeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Replay the browser's request head into the tunnel so the producer's
        // service sees the real request bytes, unrewritten (§8.4).
        if (!initialBrowserBytes.IsEmpty)
            await ws.SendAsync(initialBrowserBytes, WebSocketMessageType.Binary, endOfMessage: true, linked.Token);

        // browser → tunnel runs concurrently with the gate so a request whose body
        // is split across packets still reaches the upstream while we await its
        // first response byte (otherwise a body-then-respond service would deadlock).
        var browserToWs = PumpBrowserToWsAsync(browser, ws, linked.Token);

        // ── First-upstream-byte gate ───────────────────────────────────────────
        var buffer = new byte[BufferSize];
        WebSocketReceiveResult first;
        try
        {
            using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            gateCts.CancelAfter(firstByteTimeout);
            first = await ws.ReceiveAsync(buffer, gateCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await AbandonAsync(ws, linked, browserToWs);
            return ProxyOutcome.Timeout; // upstream silent past the timeout → 504
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            await AbandonAsync(ws, linked, browserToWs);
            return ProxyOutcome.NoUpstream; // tunnel dropped before any byte → 502
        }

        if (first.MessageType == WebSocketMessageType.Close || first.Count == 0)
        {
            // Relay closed the consumer end with no pair (producer never dialed) → 502.
            await AbandonAsync(ws, linked, browserToWs);
            return ProxyOutcome.NoUpstream;
        }

        // First upstream bytes are real: hand them to the browser and splice the rest.
        await browser.WriteAsync(buffer.AsMemory(0, first.Count), linked.Token);
        await browser.FlushAsync(linked.Token);

        var wsToBrowser = PumpWsToBrowserAsync(ws, browser, linked.Token);

        // Whichever direction ends first triggers teardown of both.
        await Task.WhenAny(browserToWs, wsToBrowser);
        await CloseWebSocketQuietlyAsync(ws);
        await linked.CancelAsync();
        try { await Task.WhenAll(browserToWs, wsToBrowser); }
        catch (Exception) { /* expected on teardown */ }
        return ProxyOutcome.Completed;
    }

    /// <summary>
    /// Abandon a connection that produced no upstream byte: close the tunnel,
    /// cancel the browser→tunnel pump, and drain it. The browser stream is left
    /// untouched so the handler can still write a 502/504 error response.
    /// </summary>
    private static async Task AbandonAsync(WebSocket ws, CancellationTokenSource linked, Task pump)
    {
        await CloseWebSocketQuietlyAsync(ws);
        await linked.CancelAsync();
        try { await pump; }
        catch (Exception) { /* expected on teardown */ }
    }

    private static async Task PumpBrowserToWsAsync(Stream from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, ct);
                if (read == 0)
                    return; // browser closed its half
                await to.SendAsync(
                    new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException) { /* teardown */ }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            // Peer dropped or the socket was aborted; end this direction.
        }
    }

    private static async Task PumpWsToBrowserAsync(WebSocket from, Stream to, CancellationToken ct)
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
                await to.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* teardown */ }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
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
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "preview: peer closed", cts.Token);
        }
        catch (Exception)
        {
            // Best-effort: the peer may already be gone, or the socket aborted.
        }
    }
}

/// <summary>The result of splicing a browser connection through the tunnel (§8.4).</summary>
public enum ProxyOutcome
{
    /// <summary>The response streamed through and both sides closed normally.</summary>
    Completed,

    /// <summary>The tunnel dropped before any upstream byte — producer never dialed (→ 502).</summary>
    NoUpstream,

    /// <summary>No upstream byte within the first-byte timeout (→ 504).</summary>
    Timeout,
}
