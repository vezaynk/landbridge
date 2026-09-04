using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane;

/// <summary>
/// Transactional hub outbox: stage <see cref="HubQueueRow"/>s on the same
/// context as the domain write, then <c>pg_notify</c>. Session-channel notify
/// keeps dispatch/inbox working; <see cref="LandbridgeDbContext.HubChannel"/>
/// is for wakes that are not a session id (machines).
/// </summary>
public static class HubOutbox
{
    public static void Stage(LandbridgeDbContext db, TimeProvider clock, string topic, Guid entityId) =>
        db.HubQueue.Add(new HubQueueRow
        {
            Topic = topic,
            EntityId = entityId,
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
    /// Heartbeat → machines + processes outbox on the hub channel. No-op when
    /// <paramref name="machineId"/> is not a Guid (test fixtures use "m1").
    /// </summary>
    public static async Task WriteHeartbeatAsync(
        LandbridgeDbContext db, TimeProvider clock, string machineId, CancellationToken ct)
    {
        if (!Guid.TryParse(machineId, out var id))
            return;
        Stage(db, clock, HubQueueRow.MachinesTopic, id);
        Stage(db, clock, HubQueueRow.ProcessesTopic, id);
        await db.SaveChangesAsync(ct);
        await NotifyHubAsync(db, id, ct);
    }
}

