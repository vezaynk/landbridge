using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Landbridge.Hub;

/// <summary>
/// Hub HTTP: SSE wakes plus JSON twins. Catch-up is <c>hub_queue</c>.
/// NOTIFY only unblocks the wait. Bearer required; cookies are not accepted.
/// </summary>
public static class HubEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder app)
    {
        var hub = app.MapGroup("").RequireAuthorization();
        hub.MapGet("/sessions/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.SessionsTopic, null, after, ct));
        hub.MapGet("/sessions/{id:guid}/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.SessionTopic, id, after, ct));
        hub.MapGet("/sessions/{id:guid}/events/log", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.EventsTopic, id, after, ct));
        hub.MapGet("/sessions/{id:guid}/events/exchange", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.ExchangeTopic, id, after, ct));
        hub.MapGet("/services/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.ServicesTopic, null, after, ct));
        hub.MapGet("/sessions/{id:guid}/services/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.ServicesTopic, id, after, ct));
        hub.MapGet("/forwards/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.ForwardsTopic, null, after, ct));
        hub.MapGet("/forwards/{id:guid}/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.ForwardsTopic, id, after, ct));
        hub.MapGet("/previews/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.PreviewsTopic, null, after, ct));
        hub.MapGet("/previews/{id:guid}/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.PreviewsTopic, id, after, ct));
        hub.MapGet("/machines/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.MachinesTopic, null, after, ct));
        hub.MapGet("/machines/{id:guid}/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.MachinesTopic, id, after, ct));
        hub.MapGet("/machines/{id:guid}/processes/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.ProcessesTopic, id, after, ct));
        hub.MapGet("/processes/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.ProcessTopic, null, after, ct));
        hub.MapGet("/processes/{id:guid}/events", (HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w, IOptions<HubOptions> o, Guid id, long? after, CancellationToken ct) =>
            AuthorizedStream(http, tokens, db, w, o, HubQueueRow.ProcessTopic, id, after, ct));
        hub.MapHubReads();
        return app;
    }

    private static async Task<IResult> AuthorizedStream(
        HttpContext http, TokenService tokens, IDbContextFactory<LandbridgeDbContext> db, HubWaiters w,
        IOptions<HubOptions> o, string topic, Guid? entityId, long? after, CancellationToken ct)
    {
        var caller = await HubCaller.ResolveAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (!caller.MayWatch(topic, entityId))
            return HubCaller.Forbid();
        return Stream(http, db, w, o, topic, entityId, after, ct);
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
            Enumerate(dbFactory, waiters, topic, entityId, ResumeAfter(http, after), options.Value.PingInterval, ct));
    }

    /// <summary>
    /// Native EventSource reconnects to the same URL with <c>Last-Event-ID</c>.
    /// That header wins; <c>?after=</c> is the explicit first-open cursor.
    /// </summary>
    internal static long ResumeAfter(HttpContext http, long? after)
    {
        var header = http.Request.Headers["Last-Event-ID"].ToString();
        if (long.TryParse(header, out var id) && id >= 0)
            return id;
        return after ?? 0;
    }

    private static async IAsyncEnumerable<SseItem<string>> Enumerate(
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        HubWaiters waiters,
        string topic,
        Guid? entityId,
        long after,
        TimeSpan ping,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var sub = waiters.Subscribe(topic, entityId);
        var last = after;
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
