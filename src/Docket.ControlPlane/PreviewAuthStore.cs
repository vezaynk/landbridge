using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Docket.ControlPlane;

/// <summary>
/// The in-memory half of the gated-preview browser auth flow (spec §8.4). A gated
/// preview lives on <c>*.preview.&lt;domain&gt;</c>, a different origin from the §12
/// dashboard, so the operator's host-scoped <c>docket_session</c> cookie never
/// reaches it. Instead: the frontend redirects the browser to the dashboard
/// origin, the operator session confirms there and the plane mints a <b>one-time
/// short-lived code</b>; the browser carries the code back to the preview origin,
/// the frontend exchanges it here for a <b>per-label preview session</b>, and sets
/// that as its own cookie on the preview origin. The operator session itself never
/// rides the preview origin — which matters because the request bytes are spliced
/// verbatim into team workload code (the head-strip in the frontend is the other
/// half of that guarantee).
///
/// <para>Both artifacts are deliberately <b>in-memory only</b> — short TTL,
/// single-instance control plane (§3) — so no schema/migration is involved; the
/// durable mapping table (Wave A) is enough. A control-plane restart drops
/// in-flight codes/sessions, exactly like the forward waiters and the OAuth
/// authorization codes' volatile state; a browser simply re-auths.</para>
/// </summary>
public sealed class PreviewAuthStore(TimeProvider clock)
{
    /// <summary>A code is exchanged within seconds of issue; keep it tight.</summary>
    public static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(2);

    /// <summary>A per-label preview session — the cookie's lifetime on the preview origin.</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    private readonly record struct Entry(string LabelHash, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Mint a one-time code bound to <paramref name="label"/> (spec §8.4). Called by
    /// the dashboard's <c>/dashboard/preview-auth</c> endpoint only after it has
    /// confirmed a valid operator session that may access the label's Team — so a
    /// code is proof an operator authorized THIS label, nothing more.
    /// </summary>
    public string MintCode(string label)
    {
        var code = "dkt_pc_" + RandomNumberGenerator.GetHexString(48, lowercase: true);
        _codes[code] = new Entry(Hash(label), clock.GetUtcNow() + CodeTtl);
        return code;
    }

    /// <summary>
    /// Redeem a code for a per-label preview session (spec §8.4). Single-use — the
    /// code is removed on the first redeem — and it must match <paramref name="label"/>
    /// and be unexpired. Returns the session token, or null on any failure (unknown,
    /// replayed, expired, or wrong label). The frontend sets the returned token as
    /// its own per-label HttpOnly cookie on the preview origin.
    /// </summary>
    public string? Redeem(string code, string label)
    {
        if (string.IsNullOrEmpty(code) || !_codes.TryRemove(code, out var entry))
            return null; // unknown or already redeemed (single-use)
        if (entry.ExpiresAt <= clock.GetUtcNow() || entry.LabelHash != Hash(label))
            return null;

        var session = "dkt_ps_" + RandomNumberGenerator.GetHexString(48, lowercase: true);
        _sessions[session] = new Entry(entry.LabelHash, clock.GetUtcNow() + SessionTtl);
        return session;
    }

    /// <summary>How long a freshly-redeemed session is valid — the frontend sets its cookie Max-Age to match.</summary>
    public TimeSpan SessionLifetime => SessionTtl;

    /// <summary>
    /// True iff <paramref name="session"/> is a live preview session for
    /// <paramref name="label"/> (spec §8.4). Consulted by
    /// <see cref="PreviewConnectService"/> for gated previews, alongside the
    /// operator-session/bearer path used by tooling.
    /// </summary>
    public bool ValidateSession(string? session, string label)
    {
        if (string.IsNullOrEmpty(session) || !_sessions.TryGetValue(session, out var entry))
            return false;
        if (entry.ExpiresAt <= clock.GetUtcNow())
        {
            _sessions.TryRemove(session, out _);
            return false;
        }
        return entry.LabelHash == Hash(label);
    }

    /// <summary>SHA-256 hex of the case-normalized label — matches <see cref="PreviewMappingService"/>.</summary>
    private static string Hash(string label) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(label.Trim().ToLowerInvariant())));
}
