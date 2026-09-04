using System.Text.Json;
using Landbridge.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane;

/// <summary>
/// Transactional hub outbox: stage <see cref="HubQueueRow"/>s on the same
/// context as the domain write, then <c>pg_notify</c>. Session-channel notify
/// keeps dispatch/inbox working; <see cref="LandbridgeDbContext.HubChannel"/>
/// is for wakes that are not a session id (machines).
///
/// Machine liveness is the recent <c>machines</c> rows — there is no liveness
/// table. After <see cref="WaitTtlSweeper.DefaultMachineLivenessWindow"/> the
/// row is just another retained wake, not a live box.
/// </summary>
public static class HubOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Stage(
        LandbridgeDbContext db, TimeProvider clock, string topic, Guid entityId, string payload = "{}") =>
        db.HubQueue.Add(new HubQueueRow
        {
            Topic = topic,
            EntityId = entityId,
            Payload = payload,
            CreatedAt = clock.GetUtcNow(),
        });

    public static void StageSession(LandbridgeDbContext db, TimeProvider clock, Guid sessionId)
    {
        Stage(db, clock, HubQueueRow.SessionTopic, sessionId);
        Stage(db, clock, HubQueueRow.SessionsTopic, sessionId);
        Stage(db, clock, HubQueueRow.EventsTopic, sessionId);
        Stage(db, clock, HubQueueRow.ExchangeTopic, sessionId);
    }

    public static Task NotifyAsync(LandbridgeDbContext db, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT pg_notify({LandbridgeDbContext.EventChannel}, {id.ToString()})", ct);

    public static Task NotifyHubAsync(LandbridgeDbContext db, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT pg_notify({LandbridgeDbContext.HubChannel}, {id.ToString()})", ct);

    /// <summary>
    /// Heartbeat → machines + processes outbox on the hub channel, payload is
    /// the live snapshot. No-op when <paramref name="machineId"/> is not a Guid
    /// (test fixtures use "m1").
    /// </summary>
    public static async Task WriteHeartbeatAsync(
        LandbridgeDbContext db, TimeProvider clock, string machineId, MachineHeartbeat heartbeat,
        CancellationToken ct)
    {
        if (!Guid.TryParse(machineId, out var id))
            return;
        var payload = JsonSerializer.Serialize(new MachineLivePayload(
            heartbeat.Ready && !heartbeat.UnderBackPressure,
            heartbeat.UnderBackPressure,
            heartbeat.Profiles,
            heartbeat.Processes,
            clock.GetUtcNow()), Json);
        Stage(db, clock, HubQueueRow.MachinesTopic, id, payload);
        Stage(db, clock, HubQueueRow.ProcessesTopic, id, payload);
        await db.SaveChangesAsync(ct);
        await NotifyHubAsync(db, id, ct);
    }

    public static async Task<IReadOnlyDictionary<Guid, MachineLivePayload>> LiveAsync(
        LandbridgeDbContext db, DateTimeOffset now, TimeSpan window, CancellationToken ct)
    {
        var cutoff = now - window;
        var rows = await db.HubQueue.AsNoTracking()
            .Where(r => r.Topic == HubQueueRow.MachinesTopic && r.EntityId != null && r.CreatedAt >= cutoff)
            .OrderByDescending(r => r.Id)
            .ToListAsync(ct);
        var latest = new Dictionary<Guid, MachineLivePayload>();
        foreach (var row in rows)
        {
            if (row.EntityId is not { } id || latest.ContainsKey(id))
                continue;
            try
            {
                var live = JsonSerializer.Deserialize<MachineLivePayload>(row.Payload, Json);
                if (live is not null)
                    latest[id] = live;
            }
            catch (JsonException)
            {
            }
        }
        return latest;
    }

    public static Task<bool> IsLiveAsync(
        LandbridgeDbContext db, Guid machineId, DateTimeOffset now, TimeSpan window, CancellationToken ct) =>
        db.HubQueue.AsNoTracking().AnyAsync(
            r => r.Topic == HubQueueRow.MachinesTopic
                 && r.EntityId == machineId
                 && r.CreatedAt >= now - window, ct);
}

public sealed record MachineLivePayload(
    bool Ready,
    bool UnderBackPressure,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<ProcessStatus>? Processes,
    DateTimeOffset At);
