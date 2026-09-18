using System.Text.Json.Serialization;
using Landbridge.ControlPlane.Auth;

namespace Landbridge.Mcp;

/// <summary>
/// The two OAuth 2.1 discovery documents an MCP client fetches to learn how to
/// authenticate to this Instance (spec §5). Plain anonymous HTTP in the narrow,
/// non-MCP style of <see cref="EnrollmentEndpoints"/> — a client must be able to
/// read them <em>before</em> it has a token, so neither is behind
/// <c>RequireAuthorization</c>.
///
/// <para><b>They are served by different hosts.</b> RFC 9728 §3 puts the
/// protected-resource document on the resource server; RFC 8414 §3 puts the
/// authorization-server document at the issuer. Those are one origin only when one
/// process is both, so each has its own Map method and each host maps the one it
/// owns. Mapping the wrong one is how a client ends up fetching metadata whose
/// <c>issuer</c> does not match the URL it came from, which it MUST reject.</para>
///
/// <list type="bullet">
/// <item><c>GET /.well-known/oauth-protected-resource</c> — RFC 9728 Protected
///   Resource Metadata. This is the document the RFC 9728 §5.1
///   <c>WWW-Authenticate</c> challenge (emitted by
///   <c>LandbridgeAuthenticationHandler</c>) points at.</item>
/// <item><c>GET /.well-known/oauth-authorization-server</c> — RFC 8414
///   Authorization Server Metadata. Advertises the authorization/token endpoints,
///   S256 PKCE, public-client auth, and CIMD support.</item>
/// </list>
///
/// <para><b>DCR is intentionally absent.</b> There is no <c>registration_endpoint</c>
/// and no <c>/register</c> route: the spec tracks the MCP release candidate that
/// deprecates Dynamic Client Registration in favour of Client ID Metadata
/// Documents (advertised here via <c>client_id_metadata_document_supported</c>),
/// which Claude Code supports today. A client without CIMD support would, per the
/// MCP registration-priority order, fall back to DCR (unavailable here) and then
/// to prompting the user — supporting that pre-registration path is the documented
/// follow-up seam.</para>
/// </summary>
public static class OAuthMetadataEndpoints
{
    /// <summary>Emit only populated fields; absent optional members stay off the wire.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The resource server's half (RFC 9728): what this resource is, and which
    /// authorization server speaks for it. Mapped by every host that answers a 401
    /// with a resource-metadata challenge.
    /// </summary>
    public static IEndpointRouteBuilder MapOAuthResourceMetadata(this IEndpointRouteBuilder app)
    {
        // Anonymous by construction (no .RequireAuthorization()): discovery
        // precedes authentication (RFC 9728 §3).
        app.MapGet("/.well-known/oauth-protected-resource", (OAuthServerConfig server) =>
            Results.Json(new ProtectedResourceMetadata
            {
                // RFC 9728's one required field; MCP binds tokens to this audience (§5).
                Resource = server.ResourceId,
                // MCP upgrades this to required: at least one AS issuer. This is
                // the pointer that lets the authorization server live elsewhere (§5).
                AuthorizationServers = [server.Issuer],
                // Informational (RFC 9728 §2): Landbridge only ever reads the
                // Authorization: Bearer header.
                BearerMethodsSupported = ["header"],
                ScopesSupported = [OAuthScopes.Landbridge],
            }, JsonOptions));
        return app;
    }
}

/// <summary>
/// RFC 9728 Protected Resource Metadata. Only the members Landbridge populates are
/// modelled; snake_case JSON names are explicit rather than convention-derived so
/// the wire shape is unambiguous.
/// </summary>
public sealed record ProtectedResourceMetadata
{
    [JsonPropertyName("resource")]
    public required string Resource { get; init; }

    [JsonPropertyName("authorization_servers")]
    public required IReadOnlyList<string> AuthorizationServers { get; init; }

    [JsonPropertyName("bearer_methods_supported")]
    public IReadOnlyList<string>? BearerMethodsSupported { get; init; }

    [JsonPropertyName("scopes_supported")]
    public IReadOnlyList<string>? ScopesSupported { get; init; }
}
