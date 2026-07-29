using System.Net.WebSockets;
using System.Text;
using Docket.Contracts;

namespace Docket.Runner;

/// <summary>
/// The real control-plane link, spec §10: <b>docketd dials outbound</b> to
/// <c>DOCKET_CONTROL_URL</c> (ws/wss) with its machine token and never listens.
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
    private readonly string _machineToken;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private volatile ClientWebSocket? _socket;
    private Func<RunnerCommand, CancellationToken, Task>? _onCommand;
    private Task? _connectLoop;

    public WebSocketControlPlaneChannel(Uri controlUrl, string machineToken, TimeProvider clock, Action<string>? log = null)
    {
        _controlUrl = controlUrl;
        _machineToken = machineToken;
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
            try
            {
                socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", $"Bearer {_machineToken}");
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
            }
            finally
            {
                _socket = null;
                socket?.Dispose();
            }

            if (ct.IsCancellationRequested)
                break;
            try
            {
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
                // §1 tracing: open the docketd handle span, parented on the plane's
                // dispatch span via the wire traceparent, and hold it open across
                // the handler — so a worker spawned during handling inherits it
                // through Activity.Current (→ DOCKET_TRACEPARENT in the child env).
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
