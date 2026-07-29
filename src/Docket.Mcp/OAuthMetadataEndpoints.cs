using System.Text.Json.Serialization;
using Docket.ControlPlane.Auth;

namespace Docket.Mcp;

/// <summary>
/// The two OAuth 2.1 discovery documents an MCP client fetches to learn how to
/// authenticate to this Instance (spec §5). Plain anonymous HTTP in the narrow,
/// non-MCP style of <see cref="VerifierEndpoints"/> — a client must be able to
/// read them <em>before</em> it has a token, so neither is behind
/// <c>RequireAuthorization</c>.
///
/// <list type="bullet">
/// <item><c>GET /.well-known/oauth-protected-resource</c> — RFC 9728 Protected
///   Resource Metadata. This is the document the RFC 9728 §5.1
///   <c>WWW-Authenticate</c> challenge (emitted by
///   <c>DocketAuthenticationHandler</c>) points at.</item>
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

    public static IEndpointRouteBuilder MapOAuthMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        // Both are anonymous by construction (no .RequireAuthorization()): discovery
        // precedes authentication (RFC 9728 §3, RFC 8414 §3).
        app.MapGet("/.well-known/oauth-protected-resource", (OAuthServerConfig server) =>
            Results.Json(new ProtectedResourceMetadata
            {
                // RFC 9728's one required field; MCP binds tokens to this audience (§5).
                Resource = server.ResourceId,
                // MCP upgrades this to required: at least one AS issuer. This
                // Instance is its own authorization server (§5).
                AuthorizationServers = [server.Issuer],
                // Informational (RFC 9728 §2): Docket only ever reads the
                // Authorization: Bearer header.
                BearerMethodsSupported = ["header"],
                ScopesSupported = [OAuthScopes.Docket],
            }, JsonOptions));

        app.MapGet("/.well-known/oauth-authorization-server", (OAuthServerConfig server) =>
            Results.Json(new AuthorizationServerMetadata
            {
                // Draft MCP: a client rejects metadata whose issuer differs from the
                // URL it fetched, so this must equal the origin it is served from.
                Issuer = server.Issuer,
                AuthorizationEndpoint = server.AuthorizationEndpoint,
                TokenEndpoint = server.TokenEndpoint,
                ResponseTypesSupported = ["code"],
                GrantTypesSupported = ["authorization_code"],
                // OAuth 2.1 + MCP: S256 only. Its presence is also how a client
                // confirms this AS supports PKCE at all (it MUST refuse otherwise).
                CodeChallengeMethodsSupported = [Pkce.S256],
                // Public clients (§5): no client authentication at the token endpoint.
                TokenEndpointAuthMethodsSupported = ["none"],
                ScopesSupported = [OAuthScopes.Docket],
                // Advertise CIMD support so clients present a URL client_id (§5).
                ClientIdMetadataDocumentSupported = true,
            }, JsonOptions));

        return app;
    }
}

/// <summary>The coarse OAuth scope vocabulary for v1 (§5).</summary>
internal static class OAuthScopes
{
    /// <summary>
    /// A single coarse scope. Docket's §5 authority model is structural
    /// (human → lead → worker), not scope-graded, so a completed flow mints the
    /// full human session regardless of requested scope; granular OAuth scopes
    /// are a documented follow-up. Advertised so a client has a concrete value to
    /// request.
    /// </summary>
    public const string Docket = "docket";
}

/// <summary>
/// RFC 9728 Protected Resource Metadata. Only the members Docket populates are
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

/// <summary>RFC 8414 Authorization Server Metadata (plus the CIMD-support flag).</summary>
public sealed record AuthorizationServerMetadata
{
    [JsonPropertyName("issuer")]
    public required string Issuer { get; init; }

    [JsonPropertyName("authorization_endpoint")]
    public required string AuthorizationEndpoint { get; init; }

    [JsonPropertyName("token_endpoint")]
    public required string TokenEndpoint { get; init; }

    [JsonPropertyName("response_types_supported")]
    public required IReadOnlyList<string> ResponseTypesSupported { get; init; }

    [JsonPropertyName("grant_types_supported")]
    public required IReadOnlyList<string> GrantTypesSupported { get; init; }

    [JsonPropertyName("code_challenge_methods_supported")]
    public required IReadOnlyList<string> CodeChallengeMethodsSupported { get; init; }

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public required IReadOnlyList<string> TokenEndpointAuthMethodsSupported { get; init; }

    [JsonPropertyName("scopes_supported")]
    public IReadOnlyList<string>? ScopesSupported { get; init; }

    [JsonPropertyName("client_id_metadata_document_supported")]
    public bool? ClientIdMetadataDocumentSupported { get; init; }
}
