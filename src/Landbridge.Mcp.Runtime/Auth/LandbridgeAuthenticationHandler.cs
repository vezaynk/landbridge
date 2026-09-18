using System.Text.Encodings.Web;
using Landbridge.ControlPlane.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Landbridge.Mcp.Auth;

/// <summary>
/// Validates Landbridge's opaque bearer tokens (spec §5) inside the ASP.NET auth
/// pipeline: pull the token, hand it to <see cref="TokenService.ValidateAsync"/>,
/// and on success attach the typed principal's claims to the request. No JWT,
/// no signature math — validation is a store lookup, which is what makes
/// revocation instant. Rejection is a clean "no result" (not a failure), so
/// the MCP challenge scheme can emit the RFC 9728 resource-metadata challenge.
/// </summary>
public sealed class LandbridgeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LandbridgeToken";

    /// <summary>
    /// The RFC 9728 §5.1 challenge on a 401 (spec §5): point the MCP client at
    /// this resource server's protected-resource metadata so it can discover the
    /// authorization server and run the OAuth 2.1 flow. This is the whole of the
    /// OAuth-related change to the handler — token validation
    /// (<see cref="HandleAuthenticateAsync"/>) stays a store lookup, untouched.
    ///
    /// <para><see cref="OAuthServerConfig"/> is resolved from request services
    /// rather than the constructor so the handler keeps its existing dependency
    /// surface (hosts that don't wire the OAuth endpoints still construct it). The
    /// canonical resource-metadata URI is used when configured; otherwise it is
    /// derived from the request so the challenge is always well-formed.</para>
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var oauth = Context.RequestServices.GetService<OAuthServerConfig>();
        var resourceMetadata = oauth?.ResourceMetadataUri
            ?? $"{Request.Scheme}://{Request.Host}/.well-known/oauth-protected-resource";

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", $"Bearer resource_metadata=\"{resourceMetadata}\"");
        return Task.CompletedTask;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return AuthenticateResult.NoResult();

        var value = header.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = value[prefix.Length..].Trim();
        var principal = await tokens.ValidateAsync(token, Context.RequestAborted);
        if (principal is null)
            return AuthenticateResult.Fail("invalid or revoked token");

        var user = LandbridgeClaims.ToClaimsPrincipal(principal);
        return AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName));
    }
}
