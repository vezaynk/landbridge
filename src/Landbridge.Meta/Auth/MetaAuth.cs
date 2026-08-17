using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Docket.Meta.Auth;

/// <summary>
/// Meta's operator session store + cookie plumbing. Unlike the plane's dashboard —
/// which resolves sessions through the control plane's <c>TokenService</c> — meta
/// has NO token database and no §5 credential classes to reuse (that separation is
/// the whole point of spec §3). So a session is just an opaque high-entropy token
/// held in memory here, minted when the operator proves the passphrase and dropped
/// as an HttpOnly cookie. Single operator, single process: an in-memory store is
/// the honest fit, and a meta restart simply requires signing in again.
/// </summary>
public sealed class MetaSessionStore
{
    /// <summary>The HttpOnly session cookie carrying the opaque meta-session token.</summary>
    public const string CookieName = "meta_session";

    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    public MetaSessionStore(TimeProvider clock) : this(clock, TimeSpan.FromHours(12)) { }

    public MetaSessionStore(TimeProvider clock, TimeSpan ttl)
    {
        _clock = clock;
        _ttl = ttl;
    }

    /// <summary>Mints a fresh session token valid for the store's TTL. Returns the token + its expiry.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Create()
    {
        var token = "mst_" + RandomNumberGenerator.GetHexString(64, lowercase: true);
        var expiresAt = _clock.GetUtcNow() + _ttl;
        _sessions[token] = expiresAt;
        return (token, expiresAt);
    }

    /// <summary>True iff the token names a live (unexpired) session. Expired tokens are swept on read.</summary>
    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var expiresAt))
            return false;
        if (expiresAt <= _clock.GetUtcNow())
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        return true;
    }

    /// <summary>Drops a session (logout).</summary>
    public void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            _sessions.TryRemove(token, out _);
    }
}

/// <summary>Cookie set/clear/resolve for the meta operator session, mirroring the dashboard's cookie discipline.</summary>
public static class MetaAuth
{
    /// <summary>Resolves the operator session from the cookie; true iff a live session is presented.</summary>
    public static bool IsAuthenticated(HttpContext http, MetaSessionStore sessions) =>
        sessions.IsValid(http.Request.Cookies[MetaSessionStore.CookieName]);

    /// <summary>Writes the session cookie: HttpOnly, SameSite=Lax, Secure over HTTPS, scoped to the app root.</summary>
    public static void SetSessionCookie(HttpContext http, string token, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(MetaSessionStore.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/",
            Expires = expiresAt,
        });

    /// <summary>Clears the session cookie (options must match the set call to delete).</summary>
    public static void ClearSessionCookie(HttpContext http) =>
        http.Response.Cookies.Delete(MetaSessionStore.CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/",
        });
}
