using Landbridge.Meta.Data;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Meta.Provisioning;

/// <summary>
/// Placement decisions at create time (design note §1/§3): which host a new instance
/// lands on (default: least loaded) and which host ports its mcp + relay publish on.
/// Port assignment is tracked in meta's DB — not left to Docker's random pick — so
/// meta knows the edge upstream before the container starts (design note §4).
/// </summary>
public sealed class PlacementService(MetaDbContext db)
{
    /// <summary>Picks the host with the fewest live (non-destroyed) instances; null if the pool is empty.</summary>
    public async Task<HostRow?> LeastLoadedHostAsync(CancellationToken ct)
    {
        var hosts = await db.Hosts.Where(h => h.RemovedAt == null).ToListAsync(ct);
        if (hosts.Count == 0)
            return null;

        var counts = await db.Instances
            .Where(i => i.State != InstanceState.Destroyed)
            .GroupBy(i => i.HostId)
            .Select(g => new { HostId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.HostId, x => x.Count, ct);

        return hosts
            .OrderBy(h => counts.GetValueOrDefault(h.Id, 0))
            .ThenBy(h => h.CreatedAt)
            .First();
    }

    /// <summary>
    /// Allocates two distinct free host ports on <paramref name="host"/> from its
    /// configured range, avoiding ports already assigned to live instances on that
    /// host. Throws when the range is exhausted.
    ///
    /// <para><b>Precondition:</b> this reads the taken ports and the caller then inserts
    /// the row, so it is only safe under the per-host advisory lock
    /// <see cref="InstanceCreator.CreateAsync"/> holds. Called outside that lock, two
    /// concurrent creates can be handed the same port.</para>
    /// </summary>
    public async Task<(int McpPort, int RelayPort)> AllocatePortsAsync(HostRow host, CancellationToken ct)
    {
        var used = new HashSet<int>();
        var assigned = await db.Instances
            .Where(i => i.HostId == host.Id && i.State != InstanceState.Destroyed)
            .Select(i => new { i.McpPublishedPort, i.RelayPublishedPort })
            .ToListAsync(ct);
        foreach (var a in assigned)
        {
            if (a.McpPublishedPort is int m) used.Add(m);
            if (a.RelayPublishedPort is int r) used.Add(r);
        }

        int Take()
        {
            for (var p = host.PortRangeLow; p <= host.PortRangeHigh; p++)
                if (used.Add(p))
                    return p;
            throw new InvalidOperationException(
                $"host {host.Name}: no free port in {host.PortRangeLow}-{host.PortRangeHigh}");
        }

        return (Take(), Take());
    }
}
