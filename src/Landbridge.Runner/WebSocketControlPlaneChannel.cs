using System.Net;
using System.Net.WebSockets;
using System.Text;
using Landbridge.Contracts;

namespace Landbridge.Runner;

/// <summary>
/// The real control-plane link, spec §10: <b>landbridged dials outbound</b> to
/// <c>LANDBRIDGE_CONTROL_URL</c> (ws/wss) with its machine token and never listens.
/// Events and heartbeats go up as encoded frames; the receive loop on the dialed
/// socket decodes commands and hands them to the daemon. It reconnects with
/// backoff on drop.
///
/// Delivery is <b>best-effort against a live connection</b> (§10): when the
/// socket is not open, <see cref="PublishAsync"/>/<see cref="HeartbeatAsync"/>
/// return <c>false</c> — never throw, never queue. Runner→control-plane
/// buffering is the caller's <see cref="OutboundEventRing"/>, not this channel.
/// </summary>
public sealed class WebSocketControlPlaneChannel : IControlPlaneChannel, IAsyncDisposable
{
    private readonly Uri _controlUrl;
    private readonly Func<string> _tokenProvider;
    private readonly Func<CancellationToken, Task>? _onAuthRejected;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private volatile ClientWebSocket? _socket;
    private Func<RunnerCommand, CancellationToken, Task>? _onCommand;
    private Task? _connectLoop;

    /// <summary>
    /// Env-token mode (the Aspire dev loop): a fixed bearer, never refreshed. The
    /// provider is a constant and no auth-rejected hook is wired, so a 401 simply
    /// backs off and retries with the same token — unchanged behaviour.
    /// </summary>
    public WebSocketControlPlaneChannel(Uri controlUrl, string machineToken, TimeProvider clock, Action<string>? log = null)
        : this(controlUrl, () => machineToken, clock, log) { }

    /// <summary>
    /// File-credential mode (§5, §13): the bearer is read fresh from
    /// <paramref name="tokenProvider"/> on every (re)connect, so a token refreshed
    /// mid-run is picked up on the next dial. When a dial is rejected 401 and
    /// <paramref name="onAuthRejected"/> is supplied, it is invoked before the
    /// backoff — the seam the <see cref="MachineTokenRefresher"/> hangs its
    /// reactive refresh on, so the retry carries a fresh token.
    /// </summary>
    public WebSocketControlPlaneChannel(
        Uri controlUrl,
        Func<string> tokenProvider,
        TimeProvider clock,
        Action<string>? log = null,
        Func<CancellationToken, Task>? onAuthRejected = null)
    {
        _controlUrl = controlUrl;
        _tokenProvider = tokenProvider;
        _onAuthRejected = onAuthRejected;
        _clock = clock;
        _log = log;
    }

    /// <summary>Whether a live connection is currently open (diagnostics/tests).</summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>
    /// Starts dialing and the receive loop. <paramref name="onCommand"/> receives
    /// each decoded command — wire it to <c>daemon.HandleAsync</c>. This dials
    /// out; the receive loop runs on the dialed socket, never a listener (§10).
    /// </summary>
    public void Start(Func<RunnerCommand, CancellationToken, Task> onCommand)
    {
        _onCommand = onCommand;
        _connectLoop = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    public Task<bool> PublishAsync(RunnerEvent evt, long gapBefore, CancellationToken ct) =>
        // The gap marker is carried locally by the ring; the frozen event
        // vocabulary has no gap field, so it is not transmitted (documented gap).
        SendTextAsync(RunnerWire.EncodeEvent(evt), ct);

    public Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct) =>
        SendTextAsync(RunnerWire.EncodeHeartbeat(heartbeat), ct);

    private async Task<bool> SendTextAsync(string json, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            return false; // §10: best-effort, no live connection.

        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await _sendLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            return true;
        }
        catch (Exception e) when (
            e is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // §10: never throw, never queue — the ring already buffered it.
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromMilliseconds(200);
        var maxBackoff = TimeSpan.FromSeconds(10);

        while (!ct.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            var authRejected = false;
            try
            {
                socket = new ClientWebSocket();
                // Surface the handshake's HTTP status so a rejected upgrade can be
                // told apart from a transport drop (the 401 refresh hook below).
                socket.Options.CollectHttpResponseDetails = true;
                // Read the bearer fresh each dial: a refresh that landed since the
                // last attempt is presented now (§13).
                socket.Options.SetRequestHeader("Authorization", $"Bearer {_tokenProvider()}");
                await socket.ConnectAsync(_controlUrl, ct);
                _socket = socket;
                _log?.Invoke($"control plane connected: {_controlUrl}");
                backoff = TimeSpan.FromMilliseconds(200); // reset on a good connection
                await ReceiveLoopAsync(socket, ct);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception e)
            {
                _log?.Invoke($"control plane connection lost: {e.Message}");
                // A 401 on the upgrade means the machine access token expired or
                // was revoked under a live daemon — invoke the refresh hook so the
                // next dial (after backoff) carries a fresh token. Env-token mode
                // leaves the hook null and simply retries the same token.
                authRejected = socket?.HttpStatusCode == HttpStatusCode.Unauthorized;
            }
            finally
            {
                _socket = null;
                socket?.Dispose();
            }

            if (ct.IsCancellationRequested)
                break;

            if (authRejected && _onAuthRejected is not null)
            {
                try
                {
                    await _onAuthRejected(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    _log?.Invoke($"token refresh after 401 failed: {e.Message}");
                }
            }
            try
            {
                // Sleeps on the INJECTED clock, so a test that hands this channel a
                // FakeTimeProvider must advance it or this backoff never wakes — the
                // reconnect loop would hang rather than retry. Every test that drives a
                // real socket through here therefore passes TimeProvider.System; use a
                // fake only when the test also controls the clock.
                await Task.Delay(backoff, _clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveFullMessageAsync(socket, buffer, ct);
            if (message is null)
                break; // clean close from the control plane

            // §10: the command channel decodes commands only; anything outside
            // the vocabulary is rejected (DecodeCommand returns null).
            if (RunnerWire.DecodeCommand(message, out var traceparent) is { } command && _onCommand is { } handler)
            {
                // §1 tracing: open the landbridged handle span, parented on the plane's
                // dispatch span via the wire traceparent, and hold it open across
                // the handler — so a worker spawned during handling inherits it
                // through Activity.Current (→ LANDBRIDGE_TRACEPARENT in the child env).
                using var activity = RunnerTelemetry.StartHandleActivity(command, traceparent);
                try
                {
                    await handler(command, ct);
                }
                catch (Exception e)
                {
                    _log?.Invoke($"command handler threw: {e.Message}");
                }
            }
        }
    }

    private static async Task<string?> ReceiveFullMessageAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", ct);
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_connectLoop is not null)
        {
            try { await _connectLoop; }
            catch (OperationCanceledException) { }
        }

        var socket = _socket;
        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
            {
                // already gone
            }
            socket.Dispose();
        }

        _cts.Dispose();
        _sendLock.Dispose();
    }
}
