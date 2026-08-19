using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Landbridge.ControlPlane;

/// <summary>
/// One-time delivery of Lead-setup markdown (the Connect page's "setup link").
/// The URL carries an opaque capability (<c>lbr_s_</c>); the Lead bearer lives
/// only in the redeemed body. In-memory, short TTL, single-instance (§3) — a
/// restart drops unredeemed links, same as preview codes. The first successful
/// redeem removes the row; every other outcome is indistinguishable.
/// </summary>
public sealed class LeadSetupLinkStore(TimeProvider clock)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _links = new(StringComparer.Ordinal);

    private sealed record Entry(string LeadToken, Guid TeamId, string McpUrl, DateTimeOffset ExpiresAt);

    /// <summary>The payload a successful redeem hands the markdown renderer.</summary>
    public sealed record Instructions(string LeadToken, Guid TeamId, string McpUrl);

    /// <summary>
    /// Mint a capability bound to this Lead token. The plaintext is returned
    /// once and is the only secret in the URL; the store keeps the hash.
    /// </summary>
    public IssuedLink Mint(string leadToken, Guid teamId, string mcpUrl)
    {
        var code = $"lbr_s_{RandomNumberGenerator.GetHexString(64)}";
        var expiresAt = clock.GetUtcNow() + Ttl;
        _links[Hash(code)] = new Entry(leadToken, teamId, mcpUrl, expiresAt);
        return new IssuedLink(code, expiresAt);
    }

    public sealed record IssuedLink(string Code, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Consume the capability. Unknown, expired, or already redeemed all return
    /// null — the endpoint answers those with one generic 404.
    /// </summary>
    public Instructions? Redeem(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        if (!_links.TryRemove(Hash(code), out var entry))
            return null;
        if (entry.ExpiresAt <= clock.GetUtcNow())
            return null;
        return new Instructions(entry.LeadToken, entry.TeamId, entry.McpUrl);
    }

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
