using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Landbridge.Hub;

/// <summary>
/// Wake-only SSE: <c>event: change</c> names what to refetch over HTTP.
/// Catch-up is <c>hub_queue</c>. NOTIFY only unblocks the wait.
/// Unauthenticated on purpose — nothing calls this host yet.
/// </summary>
public static class HubEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sessions/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.SessionsTopic, null, after, ct));
        app.MapGet("/sessions/{id:guid}/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.SessionTopic, id, after, ct));
        app.MapGet("/sessions/{id:guid}/events/log", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.EventsTopic, id, after, ct));
        app.MapGet("/sessions/{id:guid}/events/exchange", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.ExchangeTopic, id, after, ct));
        app.MapGet("/services/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.ServicesTopic, null, after, ct));
        app.MapGet("/sessions/{id:guid}/services/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.ServicesTopic, id, after, ct));
        app.MapGet("/forwards/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.ForwardsTopic, null, after, ct));
        app.MapGet("/forwards/{id:guid}/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.ForwardsTopic, id, after, ct));
        app.MapGet("/previews/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.PreviewsTopic, null, after, ct));
        app.MapGet("/previews/{id:guid}/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.PreviewsTopic, id, after, ct));
        app.MapGet("/machines/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.MachinesTopic, null, after, ct));
        app.MapGet("/machines/{id:guid}/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.MachinesTopic, id, after, ct));
        app.MapGet("/machines/{id:guid}/processes/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.ProcessesTopic, id, after, ct));
        app.MapGet("/processes/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.ProcessTopic, null, after, ct));
        app.MapGet("/processes/{id:guid}/events", (HttpContext http, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            Stream(http, db, w, o, HubQueueRow.ProcessTopic, id, after, ct));
        return app;

    }

    private static IResult Stream(
        HttpContext http,
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        HubWaiters waiters,
        IOptions<HubOptions> options,
        string topic,
        Guid? entityId,
        long? after,
        CancellationToken ct)
    {
        http.Response.Headers["X-Accel-Buffering"] = "no";
        return TypedResults.ServerSentEvents(
            Enumerate(dbFactory, waiters, topic, entityId, after, options.Value.PingInterval, ct));
    }

    private static async IAsyncEnumerable<SseItem<string>> Enumerate(
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        HubWaiters waiters,
        string topic,
        Guid? entityId,
        long? after,
        TimeSpan ping,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var sub = waiters.Subscribe(topic, entityId);
        var last = after ?? 0;
        await foreach (var row in CatchUpAsync(dbFactory, topic, entityId, last, ct))
        {
            last = row.Id;
            yield return Item(row);
        }

        var next = sub.Reader.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        var wait = next.MoveNextAsync().AsTask();
        while (!ct.IsCancellationRequested)
        {
            bool moved;
            bool pinged;
            try
            {
                moved = await wait.WaitAsync(ping, ct);
                pinged = false;
            }
            catch (TimeoutException)
            {
                moved = false;
                pinged = true;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (pinged)
            {
                yield return new SseItem<string>("", eventType: "ping");
                continue;
            }

            if (!moved)
                yield break;

            await foreach (var row in CatchUpAsync(dbFactory, topic, entityId, last, ct))
            {
                last = row.Id;
                yield return Item(row);
            }

            wait = next.MoveNextAsync().AsTask();
        }
    }

    private static async IAsyncEnumerable<HubQueueRow> CatchUpAsync(
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        string topic,
        Guid? entityId,
        long after,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.HubQueue.AsNoTracking().Where(r => r.Topic == topic && r.Id > after);
        if (entityId is { } id)
            q = q.Where(r => r.EntityId == id);
        await foreach (var row in q.OrderBy(r => r.Id).AsAsyncEnumerable().WithCancellation(ct))
            yield return row;
    }

    private static SseItem<string> Item(HubQueueRow row)
    {
        var json = JsonSerializer.Serialize(new
        {
            queueId = row.Id,
            topic = row.Topic,
            entityId = row.EntityId,
        }, Json);
        return new SseItem<string>(json, eventType: "change") { EventId = row.Id.ToString() };
    }
}
