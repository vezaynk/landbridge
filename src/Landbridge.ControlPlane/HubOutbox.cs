using Landbridge.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane;

/// <summary>
/// Transactional hub outbox: stage <see cref="HubQueueRow"/>s on the same
/// context as the domain write, then <c>pg_notify</c>. Session-channel notify
/// keeps dispatch/inbox working; <see cref="LandbridgeDbContext.HubChannel"/>
/// is for wakes that are not a session id (machines).
///
/// A heartbeat upserts <see cref="Auth.MachineRow"/> liveness columns and
/// <see cref="MachineProcessRow"/>s. <c>hub_queue</c> only doorbells.
/// </summary>
public static class HubOutbox
{
    public static void Stage(
        LandbridgeDbContext db, TimeProvider clock, string topic, Guid entityId) =>
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
    /// Upsert liveness columns and the process set, then doorbell machines /
    /// processes. No-op when <paramref name="machineId"/> is not a Guid or the
    /// machine is not enrolled.
    /// </summary>
    public static async Task WriteHeartbeatAsync(
        LandbridgeDbContext db, TimeProvider clock, string machineId, MachineHeartbeat heartbeat,
        CancellationToken ct)
    {
        if (!Guid.TryParse(machineId, out var id))
            return;
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == id && !m.Revoked, ct);
        if (machine is null)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        machine.LastSpokeAt = now;
        machine.Ready = heartbeat.Ready && !heartbeat.UnderBackPressure;
        machine.UnderBackPressure = heartbeat.UnderBackPressure;
        machine.Profiles = heartbeat.Profiles.ToArray();
        Stage(db, clock, HubQueueRow.MachinesTopic, id);

        if (heartbeat.Processes is not null)
        {
            var existing = await db.MachineProcesses.Where(p => p.MachineId == id).ToListAsync(ct);
            var byName = existing.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in heartbeat.Processes)
            {
                seen.Add(p.Name);
                if (byName.TryGetValue(p.Name, out var row))
                {
                    row.State = p.State.ToString();
                    row.DeclaredBySession = p.DeclaredBySession;
                    row.StartedAt = p.StartedAt;
                    row.ExitCode = p.ExitCode;
                    row.ExitedAt = p.ExitedAt;
                    row.StdinOpen = p.StdinOpen;
                    Stage(db, clock, HubQueueRow.ProcessTopic, row.Id);
                }
                else
                {
                    var added = new MachineProcessRow
                    {
                        Id = Guid.NewGuid(),
                        MachineId = id,
                        Name = p.Name,
                        State = p.State.ToString(),
                        DeclaredBySession = p.DeclaredBySession,
                        StartedAt = p.StartedAt,
                        ExitCode = p.ExitCode,
                        ExitedAt = p.ExitedAt,
                        StdinOpen = p.StdinOpen,
                    };
                    db.MachineProcesses.Add(added);
                    Stage(db, clock, HubQueueRow.ProcessTopic, added.Id);
                }
            }

            foreach (var row in existing.Where(r => !seen.Contains(r.Name)))
            {
                Stage(db, clock, HubQueueRow.ProcessTopic, row.Id);
                db.MachineProcesses.Remove(row);
            }

            Stage(db, clock, HubQueueRow.ProcessesTopic, id);
        }

        await db.SaveChangesAsync(ct);
        await NotifyHubAsync(db, id, ct);
        await tx.CommitAsync(ct);
    }

    public static Task<bool> IsLiveAsync(
        LandbridgeDbContext db, Guid machineId, DateTimeOffset now, TimeSpan window, CancellationToken ct) =>
        db.Machines.AsNoTracking().AnyAsync(
            m => m.Id == machineId && !m.Revoked && m.LastSpokeAt != null && m.LastSpokeAt >= now - window, ct);
}
