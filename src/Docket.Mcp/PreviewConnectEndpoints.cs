using System.Security.Cryptography;
using System.Text;
using Docket.ControlPlane;

namespace Docket.Mcp;

/// <summary>
/// The preview-frontend-facing connect endpoint (spec §8.4). Plain HTTP, in the
/// same narrow non-MCP style as <see cref="RelayValidationEndpoints"/>: the
/// preview frontend calls it once per browser connection to resolve a subdomain
/// label, authorize it, mint a fresh consumer grant + forward id, and arm the
/// producer <c>docketd</c> to dial on demand. All the logic lives in
/// <see cref="PreviewConnectService"/>; this is the transport shell that
/// authenticates the frontend and maps the typed result onto HTTP.
///
/// <para><b>Two independent auth layers, both fail-closed.</b> (1) The
/// <em>frontend</em> authenticates to the plane with a shared bearer
/// (<c>Docket:PreviewConnect:Bearer</c>), compared in constant time — with none
/// configured the endpoint refuses every call with 503, exactly like
/// <see cref="RelayValidationEndpoints"/>. (2) The <em>browser's</em> §12 operator
/// session rides in the body as <c>operatorSession</c> and is validated by the
/// plane only when the resolved mapping is gated. The frontend is not a §5
/// credential class, so like the relay it stays out of the Principal system.</para>
/// </summary>
public static class PreviewConnectEndpoints
{
    /// <summary>Config key for the shared bearer the preview frontend must present (§8.4).</summary>
    public const string BearerConfigKey = "Docket:PreviewConnect:Bearer";

    /// <summary>Config key for the relay base URL the plane hands the frontend and producer (§8.4).</summary>
    public const string RelayUrlConfigKey = "Docket:RelayUrl";

    /// <summary>Env fallback for <see cref="RelayUrlConfigKey"/>, mirroring the worker forward path.</summary>
    public const string RelayUrlEnvVar = "DOCKET_RELAY_URL";

    /// <summary>The relay URL used when neither config nor env supplies one (mirrors <c>WorkerTools.DefaultRelayUrl</c>).</summary>
    public const string DefaultRelayUrl = "http://127.0.0.1:5100";

    /// <summary>
    /// POST /preview/connect body: which label is being connected, the browser's §12
    /// operator session (bearer/tooling path), and its per-label preview session
    /// (the <c>docket_preview</c> cookie from the §8.4 redirect flow) (§8.4).
    /// </summary>
    public sealed record ConnectRequest(string? Label, string? OperatorSession, string? PreviewSession);

    /// <summary>200 body on success: the frontend dials the relay as consumer with these (§8.4).</summary>
    public sealed record ConnectResponse(string Grant, string ForwardId, string RelayUrl);

    /// <summary>POST /preview/exchange body: redeem a one-time preview-auth code for a per-label session (§8.4).</summary>
    public sealed record ExchangeRequest(string? Label, string? Code);

    /// <summary>200 body on a valid exchange: the per-label session token + its lifetime, which the frontend sets as a cookie (§8.4).</summary>
    public sealed record ExchangeResponse(string PreviewSession, int MaxAgeSeconds);

    public static IEndpointRouteBuilder MapPreviewConnectEndpoint(this IEndpointRouteBuilder app)
    {
        // No .RequireAuthorization(): the frontend authenticates with a shared
        // bearer, not a Principal (§8.4). The handlers check it in constant time.
        app.MapPost("/preview/connect", HandleAsync);
        app.MapPost("/preview/exchange", HandleExchangeAsync);
        return app;
    }

    /// <summary>
    /// POST /preview/connect {label, operatorSession?} →
    /// <list type="bullet">
    /// <item>503 — no shared bearer configured; fail-closed refuse-all (§8.4, §13).</item>
    /// <item>401 (bearer) — frontend bearer missing or wrong (constant-time compare).</item>
    /// <item>400 — no label.</item>
    /// <item>404 — unknown label. 410 — expired mapping.</item>
    /// <item>401 (session) — gated preview without a valid operator session.</item>
    /// <item>503 — service not currently reachable (check 11) or producer machine offline.</item>
    /// <item>200 {grant, forwardId, relayUrl} — producer armed; dial the relay as consumer.</item>
    /// </list>
    /// </summary>
    private static async Task<IResult> HandleAsync(
        ConnectRequest? body,
        HttpContext http,
        PreviewConnectService connect,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Docket.Mcp.PreviewConnect");

        if (AuthenticateFrontend(http, config, logger) is { } failure)
            return failure;

        if (body is null || string.IsNullOrWhiteSpace(body.Label))
            return Results.BadRequest(new { reason = "label is required" });

        var relayUrl = config[RelayUrlConfigKey]
            ?? Environment.GetEnvironmentVariable(RelayUrlEnvVar)
            ?? DefaultRelayUrl;

        return await connect.ConnectAsync(body.Label, body.OperatorSession, body.PreviewSession, relayUrl, ct) switch
        {
            PreviewConnectResult.Established e =>
                Results.Ok(new ConnectResponse(e.Grant, e.ForwardId, e.RelayUrl)),
            PreviewConnectResult.NotFound =>
                Results.NotFound(new { reason = "no such preview" }),
            PreviewConnectResult.Expired =>
                Results.StatusCode(StatusCodes.Status410Gone),
            PreviewConnectResult.Unauthorized =>
                Results.StatusCode(StatusCodes.Status401Unauthorized),
            PreviewConnectResult.Unavailable u =>
                Results.Json(new { reason = u.Reason }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// POST /preview/exchange {label, code} → 200 {previewSession, maxAgeSeconds} |
    /// 401. The frontend redeems the one-time code the dashboard minted (single-use,
    /// short TTL, label-bound) for a per-label preview session it sets as its own
    /// cookie on the preview origin (§8.4). Same frontend shared-bearer auth as
    /// connect; a bad/expired/replayed/wrong-label code is a flat 401.
    /// </summary>
    private static IResult HandleExchange(
        ExchangeRequest? body,
        HttpContext http,
        PreviewAuthStore previewAuth,
        IConfiguration config,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Docket.Mcp.PreviewExchange");

        if (AuthenticateFrontend(http, config, logger) is { } failure)
            return failure;

        if (body is null || string.IsNullOrWhiteSpace(body.Label) || string.IsNullOrWhiteSpace(body.Code))
            return Results.BadRequest(new { reason = "label and code are required" });

        var session = previewAuth.Redeem(body.Code, body.Label);
        if (session is null)
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        return Results.Ok(new ExchangeResponse(session, (int)previewAuth.SessionLifetime.TotalSeconds));
    }

    private static Task<IResult> HandleExchangeAsync(
        ExchangeRequest? body, HttpContext http, PreviewAuthStore previewAuth,
        IConfiguration config, ILoggerFactory loggerFactory) =>
        Task.FromResult(HandleExchange(body, http, previewAuth, config, loggerFactory));

    /// <summary>
    /// Shared frontend authentication for both preview endpoints (§8.4): fail-closed
    /// 503 when no bearer is configured, 401 when the presented one is missing/wrong
    /// (constant-time). Returns the failing <see cref="IResult"/>, or null when the
    /// frontend is authenticated.
    /// </summary>
    private static IResult? AuthenticateFrontend(HttpContext http, IConfiguration config, ILogger logger)
    {
        var configured = config[BearerConfigKey];
        if (string.IsNullOrEmpty(configured))
        {
            logger.LogError(
                "a preview endpoint was called but no shared bearer is configured ({Key}); refusing every " +
                "call. Configure the bearer to enable the HTTP preview frontend.", BearerConfigKey);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryReadBearer(http.Request.Headers.Authorization.ToString(), out var presented)
            || !FixedTimeEquals(presented, configured))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        return null;
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
