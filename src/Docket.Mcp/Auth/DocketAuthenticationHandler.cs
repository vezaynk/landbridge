using System.Text.Encodings.Web;
using Docket.ControlPlane.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Docket.Mcp.Auth;

/// <summary>
/// Validates Docket's opaque bearer tokens (spec §5) inside the ASP.NET auth
/// pipeline: pull the token, hand it to <see cref="TokenService.ValidateAsync"/>,
/// and on success attach the typed principal's claims to the request. No JWT,
/// no signature math — validation is a store lookup, which is what makes
/// revocation instant. Rejection is a clean "no result" (not a failure), so
/// the MCP challenge scheme can emit the RFC 9728 resource-metadata challenge.
/// </summary>
public sealed class DocketAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TokenService tokens)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DocketToken";

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

        var user = DocketClaims.ToClaimsPrincipal(principal);
        return AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName));
    }
}
