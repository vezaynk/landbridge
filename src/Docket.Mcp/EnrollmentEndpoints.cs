using Docket.ControlPlane.Auth;

namespace Docket.Mcp;

/// <summary>
/// The machine bootstrap surface (spec §5 Bootstrap, §11, §13). Plain HTTP, in
/// the narrow non-MCP style of <see cref="VerifierEndpoints"/> and
/// <see cref="RelayValidationEndpoints"/>: docketd exchanges a human-issued
/// enrollment token for machine credentials at <c>/enroll</c>, then keeps its
/// short-lived access token fresh at <c>/machine/refresh</c>.
///
/// <para><b>Both endpoints are anonymous at the ASP.NET layer</b> — deliberately
/// outside <c>RequireAuthorization</c>. The presented token <i>is</i> the
/// credential here, and its validity is a <see cref="TokenService"/> semantic,
/// not a <see cref="Principal"/>: an enrollment token authenticates nothing on
/// its own (it exists only to be exchanged), and neither does a refresh token
/// (it exists only to mint access). Gating these with the ambient Docket auth
/// handler would reject every caller before the one method that knows how to
/// consume them ever ran.</para>
///
/// <para><b>A refusal is a single generic 401</b>, never a reason. A dead, used,
/// expired, wrong-class, or simply unknown token all return exactly the same
/// 401 with no body, so the endpoint is no oracle for which failure occurred
/// (§5: revocation-first, opaque tokens). Only a malformed request — a missing
/// field — is a 400, because that is the caller's own bug, not a token verdict.</para>
/// </summary>
public static class EnrollmentEndpoints
{
    /// <summary>POST /enroll body: the bootstrap token plus the machine's server-bound declaration (§11, §13).</summary>
    public sealed record EnrollRequest(
        string? EnrollmentToken, string? Name, string? Purpose, string? Os, string? PermissionLevel);

    /// <summary>POST /machine/refresh body: the long-lived refresh token, the only secret a box keeps (§13).</summary>
    public sealed record RefreshRequest(string? RefreshToken);

    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        // No .RequireAuthorization(): the token in the body is the credential, and
        // its class authenticates no Principal, so the handlers validate it via
        // TokenService directly (§5 Bootstrap).
        app.MapPost("/enroll", HandleEnrollAsync);
        app.MapPost("/machine/refresh", HandleRefreshAsync);
        return app;
    }

    /// <summary>
    /// POST /enroll {enrollmentToken, name, purpose, os, permissionLevel} → the
    /// one exchange in the system (§9 check 13): a live, unused enrollment token
    /// becomes a machine identity plus its access/refresh pair.
    /// <list type="bullet">
    /// <item>200 {machineId, accessToken, accessExpiresAt, refreshToken, refreshExpiresAt}.</item>
    /// <item>400 — a required field is missing (the caller's bug, not a token verdict).</item>
    /// <item>401 — dead/used/expired/wrong-class/unknown token; one generic refusal, no oracle.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult> HandleEnrollAsync(
        EnrollRequest? body, TokenService tokens, CancellationToken ct)
    {
        if (body is null
            || string.IsNullOrWhiteSpace(body.EnrollmentToken)
            || string.IsNullOrWhiteSpace(body.Name)
            || string.IsNullOrWhiteSpace(body.Purpose)
            || string.IsNullOrWhiteSpace(body.Os)
            || string.IsNullOrWhiteSpace(body.PermissionLevel))
            return Results.BadRequest(new { reason = "enrollmentToken, name, purpose, os, and permissionLevel are all required" });

        // Purpose/OS/permission level are bound server-side here — a machine
        // cannot re-declare its own privileges (§13).
        var declaration = new MachineDeclaration(body.Name, body.Purpose, body.Os, body.PermissionLevel);
        var credentials = await tokens.ExchangeEnrollmentAsync(body.EnrollmentToken, declaration, ct);
        if (credentials is null)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        return Results.Ok(new
        {
            machineId = credentials.MachineId,
            accessToken = credentials.Access.Token,
            accessExpiresAt = credentials.Access.ExpiresAt,
            refreshToken = credentials.Refresh.Token,
            refreshExpiresAt = credentials.Refresh.ExpiresAt,
        });
    }

    /// <summary>
    /// POST /machine/refresh {refreshToken} → a fresh access token from a live
    /// refresh token (§13: cheap eviction — access is short and re-minted, the
    /// refresh is bound to its machine so a revoked machine mints nothing).
    /// <list type="bullet">
    /// <item>200 {accessToken, accessExpiresAt}.</item>
    /// <item>400 — refreshToken missing.</item>
    /// <item>401 — dead/expired/wrong-class/unknown refresh, or a revoked machine; one generic refusal.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult> HandleRefreshAsync(
        RefreshRequest? body, TokenService tokens, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.RefreshToken))
            return Results.BadRequest(new { reason = "refreshToken is required" });

        var access = await tokens.RefreshMachineAccessAsync(body.RefreshToken, ct);
        if (access is null)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        return Results.Ok(new
        {
            accessToken = access.Token,
            accessExpiresAt = access.ExpiresAt,
        });
    }
}
