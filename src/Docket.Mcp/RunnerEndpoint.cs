using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.Mcp.Auth;

namespace Docket.Mcp;

/// <summary>
/// The control plane's runner endpoint (spec §10). <c>docketd</c> dials this
/// outbound with its machine token — the control plane never dials the runner
/// and never listens for anything but this accepted upgrade. On upgrade the
/// connection registers a send delegate that writes command frames down the
/// socket; the receive loop folds inbound event/heartbeat frames into the
/// registry and the event sink. A thin transport shell: all policy lives in
/// <see cref="RunnerConnectionRegistry"/>, <see cref="RunnerEventSink"/>, and
/// <see cref="DispatchService"/>.
/// </summary>
public static class RunnerEndpoint
{
    public static void MapRunnerEndpoint(this WebApplication app) =>
        app.Map("/runner", HandleAsync).RequireAuthorization();

    private static async Task HandleAsync(
        HttpContext context,
        RunnerConnectionRegistry registry,
        RunnerEventSink sink,
        DispatchService dispatch,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Docket.Mcp.RunnerEndpoint");

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // §10: the runner endpoint is machine-only — reuse the DocketToken scheme.
        // A valid non-machine principal authenticates but is refused here.
        if (DocketClaims.ToPrincipal(context.User) is not Principal.Machine machine)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var machineId = machine.MachineId.ToString();

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var sendLock = new SemaphoreSlim(1, 1);

        async Task Send(RunnerCommand command, CancellationToken ct)
        {
            // §1 tracing: stamp the current dispatch span's W3C id onto the
            // envelope so the runner continues the same trace. Activity.Current
            // here is the DispatchService dispatch span, which flows in ambiently
            // across the await from the send call; null when nothing is tracing,
            // leaving the envelope exactly as before (backward compatible).
            var bytes = Encoding.UTF8.GetBytes(
                RunnerWire.EncodeCommand(command, Activity.Current?.Id));
            await sendLock.WaitAsync(ct);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
            finally
            {
                sendLock.Release();
            }
        }

        // Register not-ready with no profiles; the first heartbeat supplies both.
        registry.Register(machineId, new HashSet<string>(StringComparer.Ordinal), Send);
        logger.LogInformation("runner connected: machine={Machine}", machineId);

        try
        {
            // §10/#86: dispatch tracking is plane memory, so a reconnecting machine may
            // still be running work this process knows nothing about — after a plane
            // restart, always. Re-adopt what it holds from committed state BEFORE the
            // receive loop runs, so its very first `alive` lands on a tracked task
            // instead of being dropped, and both liveness clocks resume from now.
            // Inside the try: a failure here still unregisters below, and docketd's
            // reconnect backoff retries the whole handshake.
            await dispatch.RehydrateMachineAsync(machineId, context.RequestAborted);

            await ReceiveLoopAsync(socket, machineId, registry, sink, dispatch, logger, context.RequestAborted);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException e)
        {
            logger.LogInformation(e, "runner socket dropped: machine={Machine}", machineId);
        }
        finally
        {
            // §10 requeue-on-disconnect: a dropped connection is a machine gone —
            // requeue everything it held (the reboot path does exactly this).
            //
            // Unregister FIRST (#87). The requeue commits a pg_notify that wakes the
            // dispatch loop; while this dying connection is still registered and flagged
            // ready, a pass claims one of these very tasks, fails to write to the dead
            // socket, and requeues it a second time as AckTimeout — two requeues for one
            // disconnect, which with the §9 check 7 cap of 5 abandons a flapping machine's
            // task in as few as three disconnects. Dropping the registration first takes
            // the machine out of ReadyMachines/SnapshotFor, so that wake finds nothing to
            // dispatch here. Unregister returns what the connection held precisely so this
            // order does not lose it — asking the registry afterwards would yield nothing
            // and requeue nothing.
            var held = registry.Unregister(machineId);
            await sink.HandleDisconnectAsync(machineId, held, CancellationToken.None);
            logger.LogInformation("runner disconnected: machine={Machine}", machineId);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket, string machineId,
        RunnerConnectionRegistry registry, RunnerEventSink sink, DispatchService dispatch,
        ILogger logger, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveFullMessageAsync(socket, buffer, ct);
            if (message is null)
                break; // clean close

            // Heartbeat shares the channel but is not in the event enum (§10).
            if (RunnerWire.DecodeHeartbeat(message) is { } heartbeat)
            {
                // Keyed by the authenticated machine id, not the self-reported one.
                registry.ApplyHeartbeat(machineId, heartbeat);
                dispatch.Signal(); // a newly-ready machine can take work now
                continue;
            }
            if (RunnerWire.DecodeEvent(message) is { } evt)
            {
                await sink.HandleAsync(evt, ct);
                continue;
            }
            logger.LogWarning("runner {Machine} sent an unrecognized frame; ignoring", machineId);
        }
    }

    private static async Task<string?> ReceiveFullMessageAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
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
}
