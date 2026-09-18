using System.Text.Json.Serialization;
using Landbridge.ControlPlane.Auth;

namespace Landbridge.Auth;

/// <summary>
/// RFC 8414 Authorization Server Metadata, served at the issuer's root well-known
/// path. Anonymous by construction — discovery precedes authentication (RFC 8414
/// §3) — and mapped only here, because every endpoint it advertises exists on this
/// origin and nowhere else. Its counterpart, the RFC 9728 protected-resource
/// document, belongs to the resource server; a client reaches this document by
/// following the <c>authorization_servers</c> pointer in that one.
/// </summary>
public static class AuthorizationServerMetadataEndpoints
{
    /// <summary>Emit only populated fields; absent optional members stay off the wire.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapOAuthAuthorizationServerMetadata(this IEndpointRouteBuilder app)
    {
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
                ScopesSupported = [OAuthScopes.Landbridge],
                // Advertise CIMD support so clients present a URL client_id (§5).
                ClientIdMetadataDocumentSupported = true,
            }, JsonOptions));
        return app;
    }
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
