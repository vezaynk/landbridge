using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Landbridge.Hub;

/// <summary>
/// Session membership and per-row SSE. Unauthenticated on purpose — nothing
/// calls this host yet; the dashboard still polls Core. Auth is the JSON twin's
/// gate when a client is wired.
/// </summary>
public static class HubEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapHub(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sessions/events", StreamMembership);
        app.MapGet("/sessions/{id:guid}/events", StreamSession);
        return app;
    }

    private static IResult StreamMembership(
        HttpContext http,
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        HubWaiters waiters,
        IOptions<HubOptions> options,
        long? after,
        CancellationToken ct)
    {
        http.Response.Headers["X-Accel-Buffering"] = "no";
        return TypedResults.ServerSentEvents(
            Enumerate(dbFactory, waiters, HubQueueRow.SessionsTopic, entityId: null, after, options.Value.PingInterval, ct));
    }

    private static IResult StreamSession(
        HttpContext http,
        IDbContextFactory<LandbridgeDbContext> dbFactory,
        HubWaiters waiters,
        IOptions<HubOptions> options,
        Guid id,
        long? after,
        CancellationToken ct)
    {
        http.Response.Headers["X-Accel-Buffering"] = "no";
        return TypedResults.ServerSentEvents(
            Enumerate(dbFactory, waiters, HubQueueRow.SessionTopic, id, after, options.Value.PingInterval, ct));
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
        q = entityId is { } id
            ? q.Where(r => r.EntityId == id)
            : q.Where(r => r.EntityId == null);
        await foreach (var row in q.OrderBy(r => r.Id).AsAsyncEnumerable().WithCancellation(ct))
            yield return row;
    }

    private static SseItem<string> Item(HubQueueRow row)
    {
        using var payload = JsonDocument.Parse(row.Payload);
        var json = JsonSerializer.Serialize(new
        {
            queueId = row.Id,
            topic = row.Topic,
            entityId = row.EntityId,
            createdAt = row.CreatedAt,
            payload = payload.RootElement,
        }, Json);
        return new SseItem<string>(json, eventType: "snapshot") { EventId = row.Id.ToString() };
    }
}
