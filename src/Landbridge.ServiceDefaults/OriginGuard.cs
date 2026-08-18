using Microsoft.AspNetCore.Http;

namespace Landbridge.Web;

/// <summary>
/// The one same-origin test both of Landbridge's HTML surfaces apply to a mutating request: the
/// §12 plane dashboard and the landbridge-meta panel (design note §1).
///
/// <para>Both are cookie-authenticated, server-rendered forms with no bearer step, so without
/// this check any page a signed-in operator visits could POST to them and have the browser
/// attach the session. <c>SameSite=Lax</c> does not close that: it is a <em>site</em> control,
/// and a §8.4 preview host shares the dashboard's registrable domain <em>by design</em> — so a
/// worker's own preview page is same-site with the dashboard and its POSTs carry
/// <c>landbridge_session</c>. The check is therefore on the <b>origin</b>, which the browser sets
/// from the requesting document and script cannot forge.</para>
///
/// <para>It lives in this project because it is the only one both web hosts already reference.
/// Meta otherwise shares no code with the plane on purpose (see <c>MetaHtml</c>), and that
/// stance is right for a render kit — two copies of a render kit drift into two looks, two
/// copies of a security check drift into one hole.</para>
/// </summary>
public static class OriginGuard
{
    /// <summary>The header a browser sets on every mutating and every cross-origin request.</summary>
    public const string OriginHeader = "Origin";

    /// <summary>Fetch metadata's own same-origin declaration — the fallback when Origin is absent.</summary>
    public const string FetchSiteHeader = "Sec-Fetch-Site";

    /// <summary>The one <see cref="FetchSiteHeader"/> value that is evidence of a first-party request.</summary>
    private const string SameOrigin = "same-origin";

    /// <summary>
    /// True when <paramref name="request"/> demonstrably came from the surface's own origin.
    ///
    /// <para><c>Origin</c> decides it when present: it must equal either the origin the request
    /// was addressed to (scheme + host + port — in a cross-site attack that is the victim
    /// surface's own origin, never the attacker's, because the browser derives it from the URL
    /// it was told to fetch) or <paramref name="expectedOrigin"/>, for a deployment that
    /// configures its public one and serves behind a proxy that rewrites neither. When
    /// <c>Origin</c> is absent — no browser omits it on a POST, but an intermediary might strip
    /// it — the only remaining evidence is <c>Sec-Fetch-Site: same-origin</c>, which a browser
    /// sets and a document cannot.</para>
    ///
    /// <para><b>Fail-closed:</b> with neither header this is false, so a scripted client posting
    /// to one of these forms has to send its own <c>Origin</c>. An opaque origin
    /// (<c>Origin: null</c> — a sandboxed frame, a <c>file://</c> page) does not parse and is
    /// refused with it, as is a request carrying two different Origin headers.</para>
    /// </summary>
    /// <param name="request">The incoming request. Only its headers and its own scheme/host are read.</param>
    /// <param name="expectedOrigin">
    /// The surface's configured public origin, if the deployment has one. A full URL is fine —
    /// only its origin is compared. Null means "compare against the request's own host only".
    /// </param>
    public static bool IsSameOrigin(HttpRequest request, string? expectedOrigin = null)
    {
        var origin = request.Headers[OriginHeader].ToString();
        if (string.IsNullOrEmpty(origin))
            return string.Equals(
                request.Headers[FetchSiteHeader].ToString(), SameOrigin, StringComparison.OrdinalIgnoreCase);

        return OriginsMatch(origin, $"{request.Scheme}://{request.Host}")
            || (expectedOrigin is not null && OriginsMatch(origin, expectedOrigin));
    }

    /// <summary>
    /// Origin equality per RFC 6454 — scheme, host, and port, with a default port and an
    /// explicit one treated as the same origin (<see cref="Uri"/> normalizes both).
    /// </summary>
    private static bool OriginsMatch(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x)
        && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase)
        && x.Port == y.Port;
}
