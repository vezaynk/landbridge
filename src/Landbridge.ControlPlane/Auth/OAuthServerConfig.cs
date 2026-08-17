namespace Landbridge.ControlPlane.Auth;

/// <summary>
/// The single source of truth for this Instance's OAuth 2.1 identity (spec §5).
/// The control plane is both the authorization server and the resource server for
/// its Instance, so one canonical URL drives everything: the RFC 9728 resource
/// id, the RFC 8414 issuer, the two endpoint URLs, and the resource-metadata URI
/// the 401 challenge points at.
///
/// <para>Resolved once at startup from <c>Landbridge:PublicMcpUrl</c> — the same
/// public URL a worker dials the plane with (§13) — so the challenge handler
/// (<c>LandbridgeAuthenticationHandler</c>), the two well-known metadata endpoints,
/// and the authorize/token validation can never disagree about who this server
/// is. Registered as a singleton; injected wherever a canonical URL is needed.</para>
///
/// <para><b>Single instance = single audience.</b> Because there is exactly one
/// resource id, the RFC 8707 <c>resource</c> parameter is audience-bound by a
/// string equality against <see cref="ResourceId"/> — enforcing that equality
/// <em>is</em> the audience binding (there is no multi-resource routing to do).</para>
/// </summary>
public sealed class OAuthServerConfig(string resourceId)
{
    /// <summary>
    /// The canonical resource identifier (RFC 9728 §2, RFC 8707 §2): an absolute
    /// URL with no trailing slash and no fragment. Tokens minted here are bound to
    /// this audience; the <c>resource</c> parameter, when presented, must equal it.
    /// </summary>
    public string ResourceId { get; } = Normalize(resourceId);

    /// <summary>
    /// RFC 8414 issuer. Served at the root well-known path, so the issuer is the
    /// origin itself — identical to <see cref="ResourceId"/>. The draft MCP spec
    /// requires a client to reject metadata whose <c>issuer</c> differs from the
    /// URL it was fetched from, so this value must match exactly.
    /// </summary>
    public string Issuer => ResourceId;

    public string AuthorizationEndpoint => $"{ResourceId}/oauth/authorize";
    public string TokenEndpoint => $"{ResourceId}/oauth/token";

    /// <summary>
    /// RFC 9728 §3: the protected-resource metadata document lives at the root
    /// well-known path. This is the URI the RFC 9728 §5.1 <c>WWW-Authenticate</c>
    /// challenge advertises so an MCP client can discover the authorization server.
    /// </summary>
    public string ResourceMetadataUri => $"{ResourceId}/.well-known/oauth-protected-resource";

    /// <summary>
    /// Builds the config from the plane's configured public URL, falling back to
    /// the same default <see cref="DispatchService.DefaultPublicMcpUrl"/> the
    /// dispatcher uses, so a standalone run without configuration still serves a
    /// coherent (if loopback) OAuth surface.
    /// </summary>
    public static OAuthServerConfig FromPublicMcpUrl(string? publicMcpUrl) =>
        new(string.IsNullOrWhiteSpace(publicMcpUrl) ? DispatchService.DefaultPublicMcpUrl : publicMcpUrl);

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
