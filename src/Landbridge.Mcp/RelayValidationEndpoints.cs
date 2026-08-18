using System.Security.Cryptography;
using System.Text;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;

namespace Landbridge.Mcp;

/// <summary>
/// The relay-facing grant-validation endpoint (spec §8.3). Plain HTTP, in the
/// narrow non-MCP style of <see cref="EnrollmentEndpoints"/>: the relay asks the
/// control plane whether a grant a tunnel presented is valid for a
/// <c>{forwardId, role}</c>, and the plane answers from
/// <see cref="RelayGrantService"/>.
///
/// <para><b>Auth is a shared bearer, kept out of the Principal system.</b> The
/// relay is not an agent, a machine, or any §5 credential class — the simplest
/// correct v1 is a configured shared secret (<c>Landbridge:RelayValidation:Bearer</c>)
/// compared in constant time. It is <b>fail-closed</b>: with no bearer configured
/// the endpoint validates nothing and refuses every call with 503, loudly, rather
/// than answering an unauthenticated caller. A first-class relay credential class
/// is a possible follow-up.</para>
///
/// <para>Because this endpoint is deliberately outside <c>RequireAuthorization</c>,
/// the ambient Landbridge auth handler does not gate it; the handler reads and checks
/// the bearer itself.</para>
/// </summary>
public static class RelayValidationEndpoints
{
    /// <summary>Config key for the shared bearer the relay must present (§8.3).</summary>
    public const string BearerConfigKey = "Landbridge:RelayValidation:Bearer";

    /// <summary>The POST /relay/validate body: what tunnel is being opened (§8.3).</summary>
    public sealed record ValidateRequest(string? Grant, string? ForwardId, string? Role);

    /// <summary>One forward's byte delta in a POST /relay/usage batch (§9.10).</summary>
    public sealed record UsageEntry(string? ForwardId, long Bytes);

    /// <summary>The POST /relay/usage body: byte deltas since the relay's last report (§9.10).</summary>
    public sealed record UsageRequest(UsageEntry[]? Reports);

    public static IEndpointRouteBuilder MapRelayValidationEndpoint(this IEndpointRouteBuilder app)
    {
        // No .RequireAuthorization(): the relay authenticates with a shared bearer,
        // not a Principal (§8.3). The handler checks it in constant time.
        app.MapPost("/relay/validate", HandleAsync);
        // §9.10 byte accounting, on the same plane-facing contract and the same bearer —
        // deliberately NOT the frozen runner wire (§10), which the relay does not speak and
        // which must not grow a member for a component that is not a runner.
        app.MapPost("/relay/usage", HandleUsageAsync);
        return app;
    }

    /// <summary>
    /// POST /relay/usage {reports:[{forwardId, bytes}]} → 200 {"attributed":n} (§9.10). Byte
    /// deltas since the relay's last report, attributed to Teams through the forward ids the
    /// plane itself minted — the relay never learns which Team it moves bytes for.
    ///
    /// <para>Same auth and same failure vocabulary as <c>/relay/validate</c>: 503 with no
    /// bearer configured, 401 on a wrong one, 400 on a malformed body. A batch naming forward
    /// ids the plane never issued is <b>200 with a lower <c>attributed</c> count</b>, not an
    /// error — this is a best-effort report from a component racing grant lifetimes, and the
    /// count is how an operator sees that reports were dropped rather than silently counted.</para>
    ///
    /// <para>Unlike validation, nothing is gated on this call: it records a number a human
    /// reads. Losing it costs visibility, never correctness, which is why the relay treats a
    /// failure here as fire-and-forget rather than failing closed.</para>
    /// </summary>
    private static async Task<IResult> HandleUsageAsync(
        UsageRequest? body,
        HttpContext http,
        // Explicit, not inferred: minimal APIs cannot tell a service from a body parameter for
        // an unregistered type, and guessing wrong throws at MAP time — taking down every route
        // in the host, not just this one. Saying [FromServices] turns a host that forgot to
        // register the service into a clear resolution failure on this call alone.
        [Microsoft.AspNetCore.Mvc.FromServices] TeamForwardUsageService usage,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Landbridge.Mcp.RelayValidation");

        var configured = config[BearerConfigKey];
        if (string.IsNullOrEmpty(configured))
        {
            logger.LogError(
                "relay usage reporting was called but no shared bearer is configured ({Key}); refusing.",
                BearerConfigKey);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryReadBearer(http.Request.Headers.Authorization.ToString(), out var presented)
            || !FixedTimeEquals(presented, configured))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        if (body?.Reports is null)
            return Results.BadRequest(new { reason = "reports (array of {forwardId, bytes}) is required" });

        var reports = new List<ForwardByteReport>(body.Reports.Length);
        foreach (var entry in body.Reports)
        {
            if (Guid.TryParse(entry.ForwardId, out var forwardId))
                reports.Add(new ForwardByteReport(forwardId, entry.Bytes));
        }

        var attributed = await usage.RecordAsync(reports, ct);
        return Results.Ok(new { attributed });
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
        var logger = loggerFactory.CreateLogger("Landbridge.Mcp.RelayValidation");

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
