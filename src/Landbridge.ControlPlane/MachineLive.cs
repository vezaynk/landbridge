using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
namespace Landbridge.ControlPlane;

/// <summary>
/// Live machine facts (ready, profiles, last spoke, processes) as last-value
/// rows. The registry is the socket. Non-guid test ids ("m1") have no row and
/// keep an overlay on the registry.
/// </summary>
public static class MachineLive
{
    public static async Task<IReadOnlyList<(string Id, MachineSnapshot Snapshot)>> ReadyAsync(
        LandbridgeDbContext db,
        RunnerConnectionRegistry registry,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken ct)
    {
        var connected = await ConnectedAsync(db, registry, ct);
        return connected
            .Where(c => c.Snapshot.Ready && c.LastSpoke is { } at && now - at <= window)
            .Select(c => (c.Id, c.Snapshot))
            .ToList();

    }

    public static async Task<ProfileRoutingView> RoutingAsync(
        LandbridgeDbContext db,
        RunnerConnectionRegistry registry,
        CancellationToken ct)
    {
        var connected = await ConnectedAsync(db, registry, ct);
        var byProfile = new Dictionary<string, List<ProfileMachineView>>(StringComparer.Ordinal);
        foreach (var c in connected)
        {
            var machine = new ProfileMachineView(
                c.Id, c.Snapshot.Ready, c.Snapshot.UnderBackPressure, c.LastSpoke);
            foreach (var profile in c.Snapshot.DeclaredProfiles)
            {
                if (!byProfile.TryGetValue(profile, out var machines))
                    byProfile[profile] = machines = [];
                machines.Add(machine);
            }
        }

        var profiles = byProfile
            .Select(p => new ProfileRoutingEntry(
                p.Key,
                p.Value.Any(m => m.Ready),
                p.Value.OrderBy(m => m.MachineId, StringComparer.Ordinal).ToList()))
            .OrderBy(p => p.Profile, StringComparer.Ordinal)
            .ToList();
        return new ProfileRoutingView(profiles, connected.Count);
    }

    public static async Task<IReadOnlyList<string>> DeclaringAsync(
        LandbridgeDbContext db,
        RunnerConnectionRegistry registry,
        string profile,
        CancellationToken ct)
    {
        var connected = await ConnectedAsync(db, registry, ct);
        return connected
            .Where(c => c.Snapshot.DeclaredProfiles.Contains(profile))
            .Select(c => c.Id)
            .ToList();
    }

    private static async Task<IReadOnlyList<Connected>> ConnectedAsync(
        LandbridgeDbContext db, RunnerConnectionRegistry registry, CancellationToken ct)
    {
        var ids = registry.MachineIds();
        var guids = ids
            .Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null)
            .OfType<Guid>()
            .ToArray();
        var rows = guids.Length == 0
            ? new Dictionary<Guid, MachineRow>()
            : await db.Machines.AsNoTracking()
                .Where(m => guids.Contains(m.Id) && !m.Revoked)
                .ToDictionaryAsync(m => m.Id, ct);


        var list = new List<Connected>(ids.Count);
        foreach (var id in ids)
        {
            if (Guid.TryParse(id, out var g) && rows.TryGetValue(g, out var row) && row.LastSpokeAt is not null)
            {
                list.Add(new Connected(
                    id,
                    new MachineSnapshot(
                        id, row.Ready, row.UnderBackPressure,
                        new HashSet<string>(row.Profiles, StringComparer.Ordinal)),
                    row.LastSpokeAt));
                continue;
            }

            if (registry.SnapshotFor(id) is { } snap)
                list.Add(new Connected(id, snap, registry.LastHeartbeatFor(id)));
        }
        return list;
    }

    private readonly record struct Connected(string Id, MachineSnapshot Snapshot, DateTimeOffset? LastSpoke);
}
