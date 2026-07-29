using Docket.ControlPlane.Auth;

namespace Docket.Mcp.Dashboard;

/// <summary>
/// The dashboard's own caller-resolution step (§12 human surface), kept out of the
/// <see cref="DocketAuthenticationHandler"/> so the browser path never trips the
/// MCP RFC-9728 challenge. A dashboard request authenticates from <b>either</b> an
/// <c>Authorization: Bearer</c> header (a Lead consuming the JSON twin with its
/// token) <b>or</b> the <c>docket_session</c> HttpOnly cookie (a human in a
/// browser). Both resolve through the same <see cref="TokenService.ValidateAsync"/>
/// the rest of §5 uses, so revocation stays instant here too. Only a
/// <see cref="Principal.Human"/> or a live <see cref="Principal.Lead"/> may view
/// the dashboard.
///
/// The cookie is set by the token paste-box on <c>/dashboard/login</c> — the
/// deliberate v1 seam the in-flight OAuth 2.1 human flow (§5) replaces with a
/// redirect that drops exactly this cookie after a verified callback. Nothing else
/// about the dashboard changes when that lands.
/// </summary>
internal static class DashboardAuth
{
    /// <summary>The HttpOnly session cookie carrying an opaque human-session token.</summary>
    public const string CookieName = "docket_session";

    /// <summary>
    /// Resolves the caller from a bearer header first, then the session cookie, and
    /// returns the principal only if it is a human or a live Lead — otherwise null,
    /// which the endpoints turn into a redirect (HTML) or 401 (JSON).
    /// </summary>
    public static async Task<Principal?> ResolveAsync(HttpContext http, TokenService tokens, CancellationToken ct)
    {
        var token = BearerToken(http) ?? http.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return await tokens.ValidateAsync(token, ct) switch
        {
            Principal.Human h => h,
            Principal.Lead l => l,
            _ => null,
        };
    }

    /// <summary>
    /// Writes the session cookie: HttpOnly (no script access), SameSite=Lax (the
    /// dashboard is same-origin; Lax still allows the top-level redirect back from a
    /// future OAuth callback), and Secure whenever the request arrived over HTTPS.
    /// Scoped to <c>/dashboard</c> so it never rides along on MCP or runner calls.
    /// </summary>
    public static void SetSessionCookie(HttpContext http, string token, DateTimeOffset? expiresAt)
    {
        http.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/dashboard",
            Expires = expiresAt,
        });
    }

    /// <summary>Clears the session cookie (logout). Options must match the set call to delete.</summary>
    public static void ClearSessionCookie(HttpContext http)
    {
        http.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/dashboard",
        });
    }

    private static string? BearerToken(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("Authorization", out var header))
            return null;
        var value = header.ToString();
        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].Trim()
            : null;
    }
}
