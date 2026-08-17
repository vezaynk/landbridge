using System.Security.Cryptography;
using System.Text;
using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane;

/// <summary>
/// Creates and resolves HTTP-preview mappings (spec §8.4), alongside
/// <see cref="Docket.ControlPlane.Auth.RelayGrantService"/> and in the same
/// clock/db-injection style. A mapping is the durable <c>{label → (team, task,
/// service, expiry, auth-policy)}</c> record a preview subdomain resolves to.
///
/// <para>The subdomain label is an opaque, unguessable random token that lives
/// only in the URL/DNS; only its SHA-256 is stored and lookups are by hash,
/// exactly like the opaque credentials of §5. The label is sized to fit a single
/// DNS label (≤63 chars) and encodes no structure (§8.4).</para>
///
/// <para><b>Fence:</b> <see cref="CreateAsync"/> is the mint primitive both mint
/// surfaces (§10 <c>open_preview</c>, §12 dashboard button) will call in #62; this
/// increment builds the model, persistence, and resolve/connect path only.</para>
/// </summary>
public sealed class PreviewMappingService(DocketDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Mint a mapping and return the plaintext label (which exists only in this
    /// value, once — the store keeps only its hash). <paramref name="ttl"/> bounds
    /// the preview's life; a public preview must always carry a short one (§8.4),
    /// enforced by the caller/mint surface. The owning task/service is <b>not</b>
    /// re-checked here — check 11 is enforced at connect (a task working now may
    /// not be at connect time), matching how a forward grant re-checks at issue.
    /// </summary>
    public async Task<PreviewMintResult> CreateAsync(
        TeamId team, SessionId task, string serviceName, PreviewAuthPolicy authPolicy, TimeSpan ttl,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var label = NewLabel();
        var row = new PreviewMappingRow
        {
            Id = Guid.NewGuid(),
            LabelHash = Hash(label),
            TeamId = team.Value,
            SessionId = task.Value,
            ServiceName = serviceName,
            AuthPolicy = authPolicy,
            // No created-at: a mapping's whole lifetime is ExpiresAt, which is what resolve
            // checks, so a creation stamp was written and never read.
            ExpiresAt = now + ttl,
        };
        db.PreviewMappings.Add(row);
        await db.SaveChangesAsync(ct);
        return new PreviewMintResult(label, row);
    }

    /// <summary>
    /// Mint on behalf of a worker (spec §8.4 <c>open_preview</c>): the worker may
    /// only preview a service <b>it registered on its own task</b> (task-scoped,
    /// like <c>open_forward</c> is worker-scoped). Returns null when the caller's
    /// task did not register <paramref name="serviceName"/> — even a same-Team
    /// service owned by another task is not this worker's to expose. On success the
    /// mapping records the caller's Team + task.
    /// </summary>
    public async Task<PreviewMintResult?> CreateForWorkerAsync(
        WorkerCaller caller, string serviceName, PreviewAuthPolicy authPolicy, TimeSpan ttl,
        CancellationToken ct = default)
    {
        var owns = await db.RegisteredServices.AsNoTracking().AnyAsync(
            s => s.TeamId == caller.Team.Value && s.SessionId == caller.Session.Value && s.Name == serviceName, ct);
        if (!owns)
            return null;
        return await CreateAsync(caller.Team, caller.Session, serviceName, authPolicy, ttl, ct);
    }

    /// <summary>
    /// Resolve a subdomain label to its mapping (spec §8.4). Looks up by hash of
    /// the normalized label; a label that resolves to nothing is
    /// <see cref="PreviewResolveResult.NotFound"/> and one past its
    /// <see cref="PreviewMappingRow.ExpiresAt"/> is
    /// <see cref="PreviewResolveResult.Expired"/> — the mapping expiry gates
    /// whether a <em>new</em> connection is admitted, distinct from a grant's
    /// open-handshake TTL (§8.3). Auth-policy enforcement and check 11 happen after
    /// resolution, in <see cref="PreviewConnectService"/>.
    /// </summary>
    public async Task<PreviewResolveResult> ResolveAsync(string label, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            return PreviewResolveResult.NotFound.Instance;

        var hash = Hash(label);
        var row = await db.PreviewMappings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.LabelHash == hash, ct);
        if (row is null)
            return PreviewResolveResult.NotFound.Instance;

        if (row.ExpiresAt <= clock.GetUtcNow())
            return PreviewResolveResult.Expired.Instance;

        return new PreviewResolveResult.Found(row);
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// A random, opaque, DNS-label-safe token: 32 lowercase hex chars = 128 bits
    /// of entropy, well within a DNS label's 63-char limit and containing only
    /// <c>[0-9a-f]</c>. Unguessable — a public preview's whole defense is that the
    /// label cannot be predicted (§8.4).
    /// </summary>
    private static string NewLabel() =>
        RandomNumberGenerator.GetHexString(32, lowercase: true);

    /// <summary>SHA-256 hex of the case-normalized label (DNS labels are case-insensitive).</summary>
    private static string Hash(string label) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(label.Trim().ToLowerInvariant())));
}

/// <summary>What <see cref="PreviewMappingService.CreateAsync"/> returns: the once-only plaintext label and its row.</summary>
public sealed record PreviewMintResult(string Label, PreviewMappingRow Mapping);

/// <summary>Outcome of resolving a preview label (§8.4).</summary>
public abstract record PreviewResolveResult
{
    private PreviewResolveResult() { }

    /// <summary>The label resolved to a live mapping.</summary>
    public sealed record Found(PreviewMappingRow Mapping) : PreviewResolveResult;

    /// <summary>No mapping for this label (never minted, or the plane was reset).</summary>
    public sealed record NotFound : PreviewResolveResult
    {
        public static readonly NotFound Instance = new();
    }

    /// <summary>The mapping exists but is past its TTL and admits no new connections.</summary>
    public sealed record Expired : PreviewResolveResult
    {
        public static readonly Expired Instance = new();
    }
}
