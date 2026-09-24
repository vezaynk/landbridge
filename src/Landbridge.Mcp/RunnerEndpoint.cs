using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Landbridge.Contracts;
using Microsoft.Extensions.Configuration;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Mvc;
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
        app.MapPost("/runner/ingest", IngestAsync).RequireAuthorization();
        app.MapGet("/runner/events", EventsAsync).RequireAuthorization();
        app.MapPost("/runner/commands/{id:long}/ack", AckAsync).RequireAuthorization();
    }

    /// <summary>
    /// A machine's command stream, and its connection for as long as it is open.
    ///
    /// <para>This is where a machine becomes reachable: registering here is what puts it
    /// in <see cref="MachineLive.ReadyAsync"/>'s view, so a box that heartbeats without
    /// holding a stream is visible and not dispatchable — which is right, because there
    /// would be no way to hand it the command. Ending the stream requeues what it held
    /// (§10), in the order #87 requires.</para>
    /// </summary>
    private static async Task EventsAsync(
        HttpContext context,
        [FromServices] RunnerOutbox outbox,
        [FromServices] RunnerConnectionRegistry registry,
        [FromServices] RunnerEventSink sink,
        [FromServices] DispatchService dispatch,
        [FromServices] IConfiguration config,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Landbridge.Mcp.RunnerEndpoint");
        if (LandbridgeClaims.ToPrincipal(context.User) is not Principal.Machine machine)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var machineId = machine.MachineId;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.StartAsync(ct);
        await context.Response.WriteAsync(": open\n\n", ct);
        await context.Response.Body.FlushAsync(ct);

        // Subscribe before registering, so a command sent the instant this machine becomes
        // dispatchable is already being listened for. The snapshot below closes the other
        // side of that window.
        var reader = outbox.Subscribe(machineId, out var unsubscribe);

        // The plane's own hang-up on this stream (§13 revoke): cancelling ends the loop,
        // the finally runs, and the response completes. Linked to the request so a client
        // disconnect and a shutdown both land here too.
        using var hangUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hungUpByPlane = false;

        // No send delegate: commands reach this machine through the loop below, which is
        // the only writer on this response. A delegate as well would interleave frames and
        // deliver every durable command twice — once from the publish that wakes the loop,
        // once from the delegate.
        var connection = registry.Register(
            machineId, new HashSet<string>(StringComparer.Ordinal),
            send: null,
            close: _ =>
            {
                hungUpByPlane = true;
                hangUp.Cancel();
                return Task.CompletedTask;
            });
        logger.LogInformation(
            "runner stream opened: machine={Machine} connection={Generation}",
            machineId, connection.Token.Generation);
        if (connection.SupersededLiveConnection)
            // The machine held two streams at once: one the plane believed live had stopped
            // carrying bytes without ending (§17.8, the closed laptop). Benign — the older
            // one's teardown touches nothing — but an operator fact.
            logger.LogWarning(
                "runner {Machine} opened a stream while an earlier one was still registered; " +
                "connection {Generation} supersedes it", machineId, connection.Token.Generation);

        var streamCt = hangUp.Token;
        try
        {
            // §10/#86: dispatch tracking is plane memory, so a reconnecting machine may
            // still be running work this process knows nothing about — after a plane
            // restart, always. Re-adopt from committed state before any command goes out,
            // so its first `alive` lands on a tracked task and both clocks resume from now.
            await dispatch.RehydrateMachineAsync(machineId, streamCt);
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
            foreach (var row in await outbox.UnackedAsync(machineId, streamCt))
            {
                fromSnapshot.Add(row.Id);
                await WriteCommandAsync(context, row, streamCt);
            }

            while (true)
            {
                // Waiting with a deadline rather than racing a Task.Delay: an abandoned
                // wait would leave a registered waiter behind on every quiet pass.
                using var beat = CancellationTokenSource.CreateLinkedTokenSource(streamCt);
                beat.CancelAfter(KeepAliveFor(config));
                try
                {
                    if (!await reader.WaitToReadAsync(beat.Token))
                        break;
                }
                catch (OperationCanceledException) when (!streamCt.IsCancellationRequested)
                {
                    // Nothing to send. Say so anyway: a stream that is silent for long
                    // enough is one an idle proxy will close out from under both ends.
                    await context.Response.WriteAsync(": keepalive\n\n", streamCt);
                    await context.Response.Body.FlushAsync(streamCt);
                    continue;
                }

                while (reader.TryRead(out var row))
                {
                    // Already sent in the snapshot above — the overlap between subscribing
                    // and reading it, which is the only way a row can arrive twice.
                    if (fromSnapshot.Remove(row.Id))
                        continue;
                    await WriteCommandAsync(context, row, streamCt);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            unsubscribe.Dispose();

            // §10 requeue-on-disconnect: the stream ending is the machine gone, so
            // everything it held goes back on the queue.
            //
            // Unregister FIRST (#87). The requeue commits a pg_notify that wakes the
            // dispatch loop; while this dying connection is still registered and ready, a
            // pass could claim one of these very tasks and hand it straight back to a
            // machine that is no longer listening. Unregister returns what was held
            // precisely so this order does not lose it — asking afterwards yields nothing.
            //
            // By CONNECTION, not by machine (#94). If this stream was already superseded,
            // the registry entry belongs to its replacement, and requeueing here would
            // abandon a healthy machine's running work and leave its live stream
            // registered nowhere. A superseded teardown touches the registry not at all.
            var teardown = registry.Unregister(connection.Token);
            await sink.HandleDisconnectAsync(machineId, teardown.Held, CancellationToken.None);
            if (teardown.Unregistered)
                logger.LogInformation(
                    "runner stream closed: machine={Machine} connection={Generation}",
                    machineId, connection.Token.Generation);
            else if (hungUpByPlane)
                logger.LogInformation(
                    "revoked runner stream closed: machine={Machine} connection={Generation}",
                    machineId, connection.Token.Generation);
            else
                logger.LogInformation(
                    "superseded runner stream closed: machine={Machine} connection={Generation} " +
                    "(registry untouched; a newer stream holds this machine)",
                    machineId, connection.Token.Generation);
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
            frame, machine.MachineId, sink, dispatch, dbFactory, clock,
            loggerFactory.CreateLogger("Landbridge.Mcp.RunnerEndpoint"), ct);
        return known
            ? Results.NoContent()
            : Results.BadRequest(new { error = "unrecognized runner frame" });
    }

    /// <summary>
    /// One §10 frame. Heartbeats upsert last-value columns; events go to
    /// <see cref="RunnerEventSink"/>.
    ///
    /// <para>A heartbeat is not gated on the machine holding a live stream. The facts it
    /// carries — ready, profiles, last-spoke — are columns on <c>machines</c>, keyed by
    /// the box rather than by a connection, so a beat that arrives while a machine's
    /// stream is being replaced is simply a slightly old beat from that same box. What a
    /// connection-keyed gate protected (#94) was registry state, and none of this is.</para>
    /// </summary>
    internal static async Task<bool> IngestFrameAsync(
        string message,
        Guid machineId,
        RunnerEventSink sink,
        DispatchService dispatch,
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct)
    {
        if (RunnerWire.DecodeHeartbeat(message) is { } heartbeat)
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

}
