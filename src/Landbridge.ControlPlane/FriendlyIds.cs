using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane;

/// <summary>
/// Public identifiers are allocated slugs. Guids stay the PK and the runner
/// principal. Every MCP/HTTP boundary resolves slug-or-Guid on the way in and
/// emits the slug on the way out.
/// </summary>
public sealed class FriendlyIds(LandbridgeDbContext db)
{
    public async Task<TeamId?> TryTeamAsync(string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (Guid.TryParse(text, out var g))
            return new TeamId(g);
        if (!HaikuSlug.IsWellFormed(text))
            return null;
        var id = await db.LeadTeams.AsNoTracking()
            .Where(t => t.Slug == text)
            .Select(t => (Guid?)t.TeamId)
            .FirstOrDefaultAsync(ct);
        return id is { } tid ? new TeamId(tid) : null;
    }

    public async Task<SessionId?> TrySessionAsync(string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (Guid.TryParse(text, out var g))
            return new SessionId(g);
        if (!HaikuSlug.IsWellFormed(text))
            return null;
        var id = await db.Sessions.AsNoTracking()
            .Where(s => s.Slug == text)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        return id is { } sid ? new SessionId(sid) : null;
    }

    public async Task<Guid?> TryMachineAsync(string? text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (Guid.TryParse(text, out var g))
            return g;
        if (!HaikuSlug.IsWellFormed(text))
            return null;
        return await db.Set<MachineRow>().AsNoTracking()
            .Where(m => m.Slug == text)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string> TeamAsync(Guid id, CancellationToken ct = default)
    {
        var slug = await db.LeadTeams.AsNoTracking()
            .Where(t => t.TeamId == id)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrEmpty(slug) ? id.ToString("D") : slug;
    }

    public async Task<string> SessionAsync(Guid id, CancellationToken ct = default)
    {
        var slug = await db.Sessions.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => s.Slug)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrEmpty(slug) ? id.ToString("D") : slug;
    }

    public async Task<string> MachineAsync(Guid id, CancellationToken ct = default)
    {
        var slug = await db.Set<MachineRow>().AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => m.Slug)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrEmpty(slug) ? id.ToString("D") : slug;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> TeamSlugsAsync(
        IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToArray();
        if (list.Length == 0)
            return new Dictionary<Guid, string>();
        var found = await db.LeadTeams.AsNoTracking()
            .Where(t => list.Contains(t.TeamId))
            .ToDictionaryAsync(t => t.TeamId, t => t.Slug, ct);
        return WithFallback(list, found);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> SessionSlugsAsync(
        IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var list = ids.Distinct().ToArray();
        if (list.Length == 0)
            return new Dictionary<Guid, string>();
        var found = await db.Sessions.AsNoTracking()
            .Where(s => list.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Slug, ct);
        return WithFallback(list, found);
    }

    public async Task<IReadOnlyDictionary<string, string>> MachineSlugsByWireIdAsync(
        IEnumerable<string> wireIds, CancellationToken ct = default)
    {
        var texts = wireIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
        var guids = texts
            .Select(id => Guid.TryParse(id, out var g) ? g : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        if (guids.Length == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = await db.Set<MachineRow>().AsNoTracking()
            .Where(m => guids.Contains(m.Id))
            .Select(m => new { m.Id, m.Slug })
            .ToListAsync(ct);
        var byGuid = rows.ToDictionary(m => m.Id.ToString(), m => m.Slug, StringComparer.Ordinal);
        return texts.ToDictionary(
            t => t,
            t => byGuid.GetValueOrDefault(t) is { Length: > 0 } slug ? slug : t,
            StringComparer.Ordinal);
    }

    private static Dictionary<Guid, string> WithFallback(Guid[] ids, Dictionary<Guid, string> found)
    {
        var map = new Dictionary<Guid, string>(ids.Length);
        foreach (var id in ids)
            map[id] = found.TryGetValue(id, out var slug) && slug.Length > 0 ? slug : id.ToString("D");
        return map;
    }
}
