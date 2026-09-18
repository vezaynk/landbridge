namespace Landbridge.ControlPlane.Auth;

/// <summary>
/// The single source of truth for this Instance's OAuth 2.1 identity (spec §5).
///
/// <para>Two URLs, because they name two different things. <see cref="ResourceId"/>
/// is the resource server — the MCP surface a token is minted <em>for</em>.
/// <see cref="Issuer"/> is the authorization server that mints it. They were one
/// value while both lived in the same process; the authorization server is its own
/// host now, so they are one value only when an Instance is deployed that way.</para>
///
/// <para>Resolved once at startup: <see cref="ResourceId"/> from
/// <c>Landbridge:PublicMcpUrl</c> — the same public URL a worker dials the plane
/// with (§13) — and <see cref="Issuer"/> from <c>Landbridge:AuthUrl</c>, falling
/// back to the resource id so a single-host deployment keeps behaving as it did.
/// Registered as a singleton on both hosts, so the challenge handler
/// (<c>LandbridgeAuthenticationHandler</c>), the two well-known documents, and
/// authorize/token validation cannot disagree about who is who.</para>
///
/// <para><b>Single instance = single audience.</b> Because there is exactly one
/// resource id, the RFC 8707 <c>resource</c> parameter is audience-bound by a
/// string equality against <see cref="ResourceId"/> — enforcing that equality
/// <em>is</em> the audience binding (there is no multi-resource routing to do).</para>
/// </summary>
public sealed class OAuthServerConfig(string resourceId, string? issuer = null)
{
    /// <summary>
    /// The canonical resource identifier (RFC 9728 §2, RFC 8707 §2): an absolute
    /// URL with no trailing slash and no fragment. Tokens minted here are bound to
    /// this audience; the <c>resource</c> parameter, when presented, must equal it.
    /// </summary>
    public string ResourceId { get; } = Normalize(resourceId);

    /// <summary>
    /// RFC 8414 issuer: the authorization server's origin. Its metadata is served
    /// at that origin's root well-known path, and the draft MCP spec requires a
    /// client to reject metadata whose <c>issuer</c> differs from the URL it was
    /// fetched from — so this must be exactly the origin the AS answers on, which
    /// is why it is configured rather than derived from a request.
    /// </summary>
    public string Issuer { get; } = Normalize(
        string.IsNullOrWhiteSpace(issuer) ? resourceId : issuer);

    /// <summary>Whether one host is both ends, as a single-process Instance is.</summary>
    public bool IssuerIsResource =>
        string.Equals(Issuer, ResourceId, StringComparison.OrdinalIgnoreCase);

    public string AuthorizationEndpoint => $"{Issuer}/oauth/authorize";
    public string TokenEndpoint => $"{Issuer}/oauth/token";

    /// <summary>
    /// RFC 8414 §3: the authorization server's metadata document, at the issuer's
    /// root well-known path. The protected-resource document points a client here.
    /// </summary>
    public string AuthorizationServerMetadataUri =>
        $"{Issuer}/.well-known/oauth-authorization-server";

    /// <summary>
    /// RFC 9728 §3: the protected-resource metadata document lives at the root
    /// well-known path. This is the URI the RFC 9728 §5.1 <c>WWW-Authenticate</c>
    /// challenge advertises so an MCP client can discover the authorization server.
    /// </summary>
    public string ResourceMetadataUri => $"{ResourceId}/.well-known/oauth-protected-resource";

    /// <summary>
    /// Builds the config from the plane's configured public URL and, when the
    /// authorization server is a separate host, its URL. Falls back to the same
    /// default <see cref="DispatchService.DefaultPublicMcpUrl"/> the dispatcher
    /// uses, so a standalone run without configuration still serves a coherent
    /// (if loopback) OAuth surface with both ends on one origin.
    /// </summary>
    public static OAuthServerConfig FromPublicMcpUrl(string? publicMcpUrl, string? authUrl = null) =>
        new(string.IsNullOrWhiteSpace(publicMcpUrl) ? DispatchService.DefaultPublicMcpUrl : publicMcpUrl,
            authUrl);

    /// <summary>
    /// Canonicalises per RFC 8707 §2 as far as it matters here: drop any trailing
    /// slash and any fragment. Comparison of a presented <c>resource</c> against
    /// this is done with the same normalisation, case-insensitively on the way in.
    /// </summary>
    public static string Normalize(string url)
    {
        var trimmed = url.Trim();
        var hash = trimmed.IndexOf('#');
        if (hash >= 0)
            trimmed = trimmed[..hash];
        return trimmed.TrimEnd('/');
    }

    /// <summary>
    /// True iff a presented RFC 8707 <c>resource</c> equals this Instance's
    /// canonical resource id. Scheme and host are compared case-insensitively
    /// (canonical URIs lowercase them, but §2 says accept uppercase for
    /// robustness); the trailing slash is normalised away on both sides.
    /// </summary>
    public bool ResourceMatches(string resource) =>
        string.Equals(Normalize(resource), ResourceId, StringComparison.OrdinalIgnoreCase);
}
