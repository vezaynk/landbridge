using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane.Auth;

/// <summary>
/// Mints and single-use-consumes OAuth 2.1 authorization codes (spec §5),
/// alongside <see cref="TokenService"/> and in the same clock/db-injection style.
/// A code is the short-lived bearer that the authorize step hands the client and
/// the token step redeems for a human session — it authenticates nothing on its
/// own, so it lives in its own table (<see cref="OAuthAuthorizationCodeRow"/>)
/// with its own <c>dkt_c_</c> prefix, hashed at rest exactly like every §5
/// credential.
///
/// <para><b>Single-use is enforced atomically.</b> Consumption is one conditional
/// update whose WHERE encodes "unconsumed and unexpired" and whose affected-row
/// count is the verdict — the same pattern as
/// <see cref="RelayGrantService.ValidateAsync"/> — so a replay or two concurrent
/// exchanges of the same code can never both succeed (RFC 6749 §4.1.2: a code
/// MUST be single-use).</para>
/// </summary>
public sealed class OAuthAuthorizationCodeService(DocketDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Authorization codes are short-lived (RFC 6749 §4.1.2 recommends ≤10
    /// minutes; a code binds a browser round-trip that completes in seconds, so a
    /// minute is generous). Long enough for the client to POST the token request,
    /// no longer.
    /// </summary>
    public static readonly TimeSpan CodeTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Mints a code bound to the authorize request's parameters (RFC 6749 §4.1.2,
    /// RFC 7636 §4.4, RFC 8707 §2). The plaintext is returned once; only its hash
    /// is stored. The caller places it in the 302 <c>code</c> parameter.
    /// </summary>
    public async Task<IssuedAuthorizationCode> MintAsync(
        string clientId, string redirectUri, string codeChallenge,
        string? resource, string? scope, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var (code, hash) = NewCode();
        db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCodeRow
        {
            Id = Guid.NewGuid(),
            CodeHash = hash,
            ClientId = clientId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            Resource = resource,
            Scope = scope,
            CreatedAt = now,
            ExpiresAt = now + CodeTtl,
        });
        await db.SaveChangesAsync(ct);
        return new IssuedAuthorizationCode(code, now + CodeTtl);
    }

    /// <summary>
    /// Redeems a code at the token endpoint (RFC 6749 §4.1.3). Every mismatch is
    /// reported as <see cref="CodeExchangeResult.InvalidGrant"/> with a single
    /// reason string — never a distinguishing detail — because the client is
    /// unauthenticated (public client, <c>token_endpoint_auth_method=none</c>) and
    /// must not learn which specific bound value failed.
    ///
    /// <para>Order matters for safety: the bound fields (client_id, redirect_uri,
    /// PKCE, resource) are checked against the row <em>before</em> the atomic
    /// consume, so a wrong-verifier or wrong-redirect attempt does not burn a code
    /// a legitimate client could still redeem. The final step is the conditional
    /// update: it, not the read, is what makes the code single-use — a replay
    /// fails the <c>consumed_at IS NULL</c> read here, and a concurrent second
    /// exchange loses the update race and gets the same <c>invalid_grant</c>.</para>
    /// </summary>
    public async Task<CodeExchangeResult> ConsumeAsync(
        string code, string clientId, string redirectUri, string codeVerifier,
        string? resource, OAuthServerConfig server, CancellationToken ct = default)
    {
        var hash = Hash(code);
        var now = clock.GetUtcNow();

        var row = await db.OAuthAuthorizationCodes.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CodeHash == hash, ct);

        // Unknown, expired, or already redeemed → invalid_grant. Replay lands here:
        // the first exchange stamped ConsumedAt, so the second read sees it set.
        if (row is null)
            return new CodeExchangeResult.InvalidGrant("authorization code not found");
        if (row.ConsumedAt is not null)
            return new CodeExchangeResult.InvalidGrant("authorization code already used");
        if (row.ExpiresAt <= now)
            return new CodeExchangeResult.InvalidGrant("authorization code expired");

        // The code is bound to the client and redirect it was issued to
        // (RFC 6749 §4.1.3): both must match exactly.
        if (!string.Equals(row.ClientId, clientId, StringComparison.Ordinal))
            return new CodeExchangeResult.InvalidGrant("client_id does not match the authorization code");
        if (!string.Equals(row.RedirectUri, redirectUri, StringComparison.Ordinal))
            return new CodeExchangeResult.InvalidGrant("redirect_uri does not match the authorization code");

        // RFC 8707 §2: if the code was bound to a resource, the token request must
        // present the same one. A single instance is a single audience, so a match
        // against the bound value is the audience binding.
        if (row.Resource is not null)
        {
            if (resource is null || !server.ResourceMatches(resource))
                return new CodeExchangeResult.InvalidGrant("resource does not match the authorization code");
        }
        else if (resource is not null && !server.ResourceMatches(resource))
        {
            // The client bound no resource at authorize but now presents one that
            // is not this Instance — refuse rather than silently ignore.
            return new CodeExchangeResult.InvalidGrant("resource does not match this server");
        }

        // RFC 7636 §4.6: BASE64URL(SHA256(verifier)) must equal the bound S256
        // challenge, compared in constant time.
        if (!Pkce.VerifyS256(codeVerifier, row.CodeChallenge))
            return new CodeExchangeResult.InvalidGrant("PKCE verification failed");

        // The gate: consume atomically. The WHERE re-asserts unconsumed+unexpired,
        // so between our read and here a concurrent exchange or expiry flips the
        // verdict to 0 rows — invalid_grant — and exactly one caller can ever win.
        var consumed = await db.OAuthAuthorizationCodes
            .Where(c => c.Id == row.Id && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);

        return consumed == 1
            ? new CodeExchangeResult.Exchanged(row.Scope)
            : new CodeExchangeResult.InvalidGrant("authorization code already used");
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static (string Code, string Hash) NewCode()
    {
        // Same shape as TokenService's opaque credentials — 256 bits, URL-safe —
        // but its own class prefix so a code is never mistaken for a bearer token.
        var code = $"dkt_c_{RandomNumberGenerator.GetHexString(64)}";
        return (code, Hash(code));
    }

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
}
