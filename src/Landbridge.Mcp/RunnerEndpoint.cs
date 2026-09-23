using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Landbridge.Contracts;
using Microsoft.Extensions.Configuration;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp.Auth;
using Microsoft.EntityFrameworkCore;


namespace Landbridge.Mcp;

/// <summary>
/// The control plane's runner endpoint (spec §10). <c>landbridged</c> dials this
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
    public static void MapRunnerEndpoint(this WebApplication app)
    {
        app.Map("/runner", HandleAsync).RequireAuthorization();
        // Part 3 phase 1: the same inbound frames, over HTTP. Commands still
        // ride the WebSocket. WS remains the dial landbridged uses today.
        app.MapPost("/runner/ingest", IngestAsync).RequireAuthorization();
        app.MapGet("/runner/events", EventsAsync).RequireAuthorization();
        app.MapPost("/runner/commands/{id:long}/ack", AckAsync).RequireAuthorization();
    }

    private static async Task EventsAsync(
        HttpContext context,
        RunnerOutbox outbox,
        IConfiguration config,
        CancellationToken ct)
    {
        if (LandbridgeClaims.ToPrincipal(context.User) is not Principal.Machine machine)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.StartAsync(ct);
        await context.Response.WriteAsync(": open\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
        var reader = outbox.Subscribe(machine.MachineId, out var unsubscribe);
        try
        {
            // The only rows that can arrive twice are those enqueued between the
            // subscribe above and this snapshot: the channel delivers each row once, so
            // each id here can be hit at most once from the live stream. The set is
            // therefore bounded by the backlog at connect time and only shrinks.
            //
            // A high-water mark would be smaller but wrong: two concurrent inserts can
            // take ids 4 and 5 and commit out of order, leaving 4 uncommitted when the
            // snapshot reads 5. Arriving below the mark, it would be dropped having
            // never been sent.
            var fromSnapshot = new HashSet<long>();
            foreach (var row in await outbox.UnackedAsync(machine.MachineId, ct))
            {
                fromSnapshot.Add(row.Id);
                await WriteCommandAsync(context, row, ct);
            }

            while (true)
            {
                // Waiting with a deadline rather than racing a Task.Delay: an abandoned
                // wait would leave a registered waiter behind on every quiet pass.
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                beat.CancelAfter(KeepAliveFor(config));
                try
                {
                    if (!await reader.WaitToReadAsync(beat.Token))
                        break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Nothing to send. Say so anyway: a stream that is silent for long
                    // enough is one an idle proxy will close out from under both ends.
                    await context.Response.WriteAsync(": keepalive\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                    continue;
                }

                while (reader.TryRead(out var row))
                {
                    if (fromSnapshot.Remove(row.Id))
                        continue;
                    // The WebSocket acks a command the moment it writes it, so a machine
                    // holding both transports would otherwise be handed it twice. This
                    // narrows that window; it does not close it, and a runner must still
                    // treat an outbox id it has already run as a duplicate.
                    if (await outbox.IsAckedAsync(row.Id, ct))
                        continue;
                    await WriteCommandAsync(context, row, ct);
                }
            }
        }
        finally
        {
            unsubscribe.Dispose();
        }
    }

    /// <summary>
    /// How long the command stream may stay silent before it sends a comment frame to
    /// keep idle intermediaries from closing it. Override with
    /// <c>Landbridge:RunnerStreamKeepAliveMs</c>.
    /// </summary>
    private static TimeSpan KeepAliveFor(IConfiguration config) =>
        int.TryParse(config["Landbridge:RunnerStreamKeepAliveMs"], out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromSeconds(20);

    private static async Task WriteCommandAsync(HttpContext context, RunnerOutboxRow row, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(new
        {
            id = row.Id,
            kind = row.Kind,
            frame = row.Payload,
        });
        await context.Response.WriteAsync($"id: {row.Id}\nevent: command\ndata: {data}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static async Task<IResult> AckAsync(
        HttpContext context, long id, RunnerOutbox outbox, CancellationToken ct)
    {
        if (LandbridgeClaims.ToPrincipal(context.User) is not Principal.Machine machine)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await outbox.AckAsync(machine.MachineId, id, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> IngestAsync(
        HttpContext context,
        RunnerConnectionRegistry registry,
        RunnerEventSink sink,
        DispatchService dispatch,
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (LandbridgeClaims.ToPrincipal(context.User) is not Principal.Machine machine)
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var frame = await new StreamReader(context.Request.Body).ReadToEndAsync(ct);
        var known = await IngestFrameAsync(
            frame, machine.MachineId, connection: null,
            registry, sink, dispatch, dbFactory, clock,
            loggerFactory.CreateLogger("Landbridge.Mcp.RunnerEndpoint"), ct);
        return known
            ? Results.NoContent()
            : Results.BadRequest(new { error = "unrecognized runner frame" });
    }

    /// <summary>
    /// One §10 frame. Heartbeats upsert last-value columns. Events go to
    /// <see cref="RunnerEventSink"/>. <paramref name="connection"/> is set only
    /// on the WebSocket path so a superseded socket cannot count as a beat.
    /// </summary>
    internal static async Task<bool> IngestFrameAsync(
        string message,
        Guid machineId,
        RunnerConnectionRegistry.ConnectionToken? connection,
        RunnerConnectionRegistry registry,
        RunnerEventSink sink,
        DispatchService dispatch,
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct)
    {
        if (RunnerWire.DecodeHeartbeat(message) is { } heartbeat)
        {
            // On the socket path a superseded connection must not count as a beat (#94).
            // Over HTTP there is no connection to supersede: the bearer token is the
            // authority, and the facts land in `machines` either way.
            var live = connection is not { } token || registry.ApplyHeartbeat(token, heartbeat);
            if (live)
            {
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    await HubOutbox.WriteHeartbeatAsync(db, clock, machineId, heartbeat, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "hub outbox write failed for heartbeat {Machine}", machineId);
                }
            }
            dispatch.Signal();
            return true;
        }
        if (RunnerWire.DecodeEvent(message) is { } evt)
        {
            await sink.HandleAsync(evt, machineId, ct);
            return true;
        }
        return false;
    }

    private static async Task HandleAsync(
        HttpContext context,
        RunnerConnectionRegistry registry,
        RunnerEventSink sink,
        DispatchService dispatch,
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        TimeProvider clock,
        ILoggerFactory loggerFactory)

    {
        var logger = loggerFactory.CreateLogger("Landbridge.Mcp.RunnerEndpoint");

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // §10: the runner endpoint is machine-only — reuse the LandbridgeToken scheme.
        // A valid non-machine principal authenticates but is refused here.
        if (LandbridgeClaims.ToPrincipal(context.User) is not Principal.Machine machine)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var machineId = machine.MachineId;

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var sendLock = new SemaphoreSlim(1, 1);

        // The plane's own hang-up on this socket (§13 revoke): cancelling breaks the
        // receive loop out of ReceiveAsync, so the finally below runs and the `using`
        // closes the socket. Deliberately NOT a close handshake — the caller is
        // un-trusting this machine, and a courtesy close frame would have to be
        // written to a peer that may be wedged, making revocation wait on the box it
        // is revoking. landbridged reads the drop as any other disconnect and reconnects;
        // its credentials are already dead by then, so it gets a 401 instead of a
        // channel. Linked to RequestAborted so a shutdown still tears this down.
        using var hangUp = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        // Which of the two ways this connection can leave the registry without dying (#94,
        // §13) happened to it — the teardown log below says something different, and true, for
        // each. Written on the revoke path, read once in the finally, so no interlocking.
        var hungUpByPlane = false;

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
                // A hung write used to stall the dispatch loop: the claim is already
                // committed, SendAsync never returned, and the task sat Working with
                // no spawn. Bound it so the existing failed-send requeue can run.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            }
            finally
            {
                sendLock.Release();
            }
        }

        // Register not-ready with no profiles; the first heartbeat supplies both. The
        // registration names THIS connection (#94) — every later call that is about this
        // socket rather than about the machine presents it, so a connection that has been
        // replaced cannot act on the registry in its successor's name.
        var connection = registry.Register(
            machineId, new HashSet<string>(StringComparer.Ordinal), Send,
            close: _ =>
            {
                hungUpByPlane = true;
                hangUp.Cancel();
                return Task.CompletedTask;
            });
        logger.LogInformation(
            "runner connected: machine={Machine} connection={Generation}",
            machineId, connection.Token.Generation);
        if (connection.SupersededLiveConnection)
            // Worth saying out loud: the machine held two accepted /runner connections at
            // once. Benign now (the older one's teardown will touch nothing), but it means a
            // socket the plane believed live had stopped carrying bytes without closing —
            // the §17.8 closed-laptop case — and that is an operator fact, not an internal
            // detail.
            logger.LogWarning(
                "runner {Machine} dialed in while an earlier connection was still registered; " +
                "connection {Generation} supersedes it", machineId, connection.Token.Generation);

        try
        {
            // §10/#86: dispatch tracking is plane memory, so a reconnecting machine may
            // still be running work this process knows nothing about — after a plane
            // restart, always. Re-adopt what it holds from committed state BEFORE the
            // receive loop runs, so its very first `alive` lands on a tracked task
            // instead of being dropped, and both liveness clocks resume from now.
            // Inside the try: a failure here still unregisters below, and landbridged's
            // reconnect backoff retries the whole handshake.
            await dispatch.RehydrateMachineAsync(machineId, hangUp.Token);

            await ReceiveLoopAsync(
                socket, connection.Token, registry, sink, dispatch, dbFactory, clock, logger, hangUp.Token);

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
            // the machine out of MachineLive/SnapshotFor, so that wake finds nothing to

            // dispatch here. Unregister returns what the connection held precisely so this
            // order does not lose it — asking the registry afterwards would yield nothing
            // and requeue nothing.
            //
            // Unregistering by CONNECTION, not by machine (#94). If this socket was already
            // superseded — a machine reattached while the plane still believed this one live
            // — then the registry entry belongs to the newer connection, and dropping it
            // here would requeue a machine's running tasks and leave its live socket
            // registered nowhere: work abandoned mid-flight on a healthy machine, and a
            // machine invisible to dispatch until it happened to reconnect again. So the
            // teardown of a superseded connection touches the registry not at all. The
            // socket itself is still this endpoint's to close, which the `using` above does.
            var teardown = registry.Unregister(connection.Token);
            await sink.HandleDisconnectAsync(machineId, teardown.Held, CancellationToken.None);
            if (teardown.Unregistered)
                logger.LogInformation(
                    "runner disconnected: machine={Machine} connection={Generation}",
                    machineId, connection.Token.Generation);
            else if (hungUpByPlane)
                // The plane took this connection out of the registry and hung up on it: a
                // machine revocation (§13), which requeues what the connection held as part
                // of the revoke. So the requeue is already done and is not this teardown's,
                // exactly as for a superseded connection but for the opposite reason.
                logger.LogWarning(
                    "revoked runner connection closed: machine={Machine} connection={Generation} " +
                    "(the plane hung up; its requeue belongs to the revoke)",
                    machineId, connection.Token.Generation);
            else
                logger.LogInformation(
                    "superseded runner connection closed: machine={Machine} connection={Generation} " +
                    "(registry untouched; a newer connection holds this machine)",
                    machineId, connection.Token.Generation);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket, RunnerConnectionRegistry.ConnectionToken connection,
        RunnerConnectionRegistry registry, RunnerEventSink sink, DispatchService dispatch,
        IDbContextFactory<LandbridgeDbContext> dbFactory, TimeProvider clock,
        ILogger logger, CancellationToken ct)
    {
        var machineId = connection.MachineId;
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveFullMessageAsync(socket, buffer, ct);
            if (message is null)
                break; // clean close

            if (!await IngestFrameAsync(
                    message, machineId, connection, registry, sink, dispatch, dbFactory, clock, logger, ct))
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
