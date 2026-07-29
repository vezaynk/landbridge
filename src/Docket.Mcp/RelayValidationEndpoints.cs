using System.Security.Cryptography;
using System.Text;
using Docket.ControlPlane.Auth;

namespace Docket.Mcp;

/// <summary>
/// The relay-facing grant-validation endpoint (spec §8.3). Plain HTTP, in the
/// narrow non-MCP style of <see cref="VerifierEndpoints"/>: the relay asks the
/// control plane whether a grant a tunnel presented is valid for a
/// <c>{forwardId, role}</c>, and the plane answers from
/// <see cref="RelayGrantService"/>.
///
/// <para><b>Auth is a shared bearer, kept out of the Principal system.</b> The
/// relay is not an agent, a machine, or any §5 credential class — the simplest
/// correct v1 is a configured shared secret (<c>Docket:RelayValidation:Bearer</c>)
/// compared in constant time. It is <b>fail-closed</b>: with no bearer configured
/// the endpoint validates nothing and refuses every call with 503, loudly, rather
/// than answering an unauthenticated caller. A first-class relay credential class
/// is a possible follow-up.</para>
///
/// <para>Because this endpoint is deliberately outside <c>RequireAuthorization</c>,
/// the ambient Docket auth handler does not gate it; the handler reads and checks
/// the bearer itself.</para>
/// </summary>
public static class RelayValidationEndpoints
{
    /// <summary>Config key for the shared bearer the relay must present (§8.3).</summary>
    public const string BearerConfigKey = "Docket:RelayValidation:Bearer";

    /// <summary>The POST /relay/validate body: what tunnel is being opened (§8.3).</summary>
    public sealed record ValidateRequest(string? Grant, string? ForwardId, string? Role);

    public static IEndpointRouteBuilder MapRelayValidationEndpoint(this IEndpointRouteBuilder app)
    {
        // No .RequireAuthorization(): the relay authenticates with a shared bearer,
        // not a Principal (§8.3). The handler checks it in constant time.
        app.MapPost("/relay/validate", HandleAsync);
        return app;
    }

    /// <summary>
    /// POST /relay/validate {grant, forwardId, role} → 200 {"valid":true|false}.
    /// <list type="bullet">
    /// <item>503 — no shared bearer configured; fail-closed refuse-all (§8.3, §13).</item>
    /// <item>401 — bearer missing or wrong (constant-time compare).</item>
    /// <item>400 — malformed body (grant, forward-id guid, and role required).</item>
    /// <item>200 {valid:false} — a well-formed but bad/expired/revoked/replayed grant.</item>
    /// </list>
    /// A refusal is <c>valid:false</c>, never an error, so the relay's fail-closed
    /// path treats "the plane said no" and "the plane was unreachable" identically.
    /// </summary>
    private static async Task<IResult> HandleAsync(
        ValidateRequest? body,
        HttpContext http,
        RelayGrantService grants,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Docket.Mcp.RelayValidation");

        var configured = config[BearerConfigKey];
        if (string.IsNullOrEmpty(configured))
        {
            // Fail-closed: with no shared secret the relay cannot be authenticated,
            // so the plane refuses to validate anything and says so loudly (§8.3).
            logger.LogError(
                "relay validation was called but no shared bearer is configured ({Key}); refusing every " +
                "call. Configure the bearer to enable the control-plane grant validator.", BearerConfigKey);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryReadBearer(http.Request.Headers.Authorization.ToString(), out var presented)
            || !FixedTimeEquals(presented, configured))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        if (body is null
            || string.IsNullOrEmpty(body.Grant)
            || !Guid.TryParse(body.ForwardId, out var forwardId)
            || !Enum.TryParse<RelayGrantRole>(body.Role, ignoreCase: true, out var role))
            return Results.BadRequest(new { reason = "grant, forwardId (guid), and role (consumer|producer) are required" });

        var valid = await grants.ValidateAsync(body.Grant, forwardId, role, ct);
        return Results.Ok(new { valid });
    }

    private static bool TryReadBearer(string authorization, out string token)
    {
        const string prefix = "Bearer ";
        if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            token = authorization[prefix.Length..].Trim();
            return token.Length > 0;
        }

        token = "";
        return false;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
