using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Landbridge.Preview;

/// <summary>
/// The frontend's client for the control plane's <c>/preview/connect</c> endpoint
/// (spec §8.4). Called once per browser connection to resolve the subdomain label,
/// authorize it, and arm the producer to dial on demand. Authenticates with the
/// shared bearer (§8.4) and maps the plane's HTTP status onto a typed outcome the
/// connection handler renders to the browser as a definite status — never a hang.
/// </summary>
public sealed class PreviewControlPlaneClient
{
    /// <summary>Named client so a host can attach its own resilience/handlers to just this traffic.</summary>
    public const string HttpClientName = "landbridge-preview-controlplane";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpFactory;
    private readonly PreviewOptions _options;
    private readonly ILogger<PreviewControlPlaneClient> _logger;
    private readonly string _connectUrl;
    private readonly string _exchangeUrl;

    public PreviewControlPlaneClient(
        IHttpClientFactory httpFactory,
        IOptions<PreviewOptions> options,
        ILogger<PreviewControlPlaneClient> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
        var baseUrl = (_options.ControlPlaneUrl ?? "").TrimEnd('/');
        _connectUrl = baseUrl + "/preview/connect";
        _exchangeUrl = baseUrl + "/preview/exchange";
    }

    private sealed record ConnectRequestBody(string Label, string? OperatorSession, string? PreviewSession);

    private sealed record ConnectResponseBody(string? Grant, string? ForwardId, string? RelayUrl);

    private sealed record ExchangeRequestBody(string Label, string Code);

    private sealed record ExchangeResponseBody(string? PreviewSession, int MaxAgeSeconds);

    /// <summary>
    /// Ask the plane to authorize + arm <paramref name="label"/>. For a gated
    /// preview, admission comes from either <paramref name="previewSession"/> (the
    /// browser's <c>landbridge_preview</c> cookie) or <paramref name="operatorSession"/>
    /// (a bearer, tooling). Any transport failure or unexpected status becomes
    /// <see cref="PreviewConnect.Error"/> → 502; the frontend never hangs on the plane.
    /// </summary>
    public async Task<PreviewConnect> ConnectAsync(
        string label, string? operatorSession, string? previewSession, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);

            var client = _httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _connectUrl)
            {
                Content = JsonContent.Create(new ConnectRequestBody(label, operatorSession, previewSession)),
            };
            if (!string.IsNullOrEmpty(_options.ControlPlaneBearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ControlPlaneBearer);

            using var response = await client.SendAsync(request, timeoutCts.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var body = await response.Content.ReadFromJsonAsync<ConnectResponseBody>(timeoutCts.Token);
                    if (body is null || string.IsNullOrEmpty(body.Grant)
                        || string.IsNullOrEmpty(body.ForwardId) || string.IsNullOrEmpty(body.RelayUrl))
                    {
                        _logger.LogWarning("preview connect: 200 with an incomplete body; treating as error");
                        return PreviewConnect.Error.Instance;
                    }
                    return new PreviewConnect.Armed(body.Grant, body.ForwardId, body.RelayUrl);

                case HttpStatusCode.NotFound:
                    return PreviewConnect.NotFound.Instance;
                case HttpStatusCode.Gone:
                    return PreviewConnect.Gone.Instance;
                case HttpStatusCode.Unauthorized:
                    return PreviewConnect.Unauthorized.Instance;
                case HttpStatusCode.ServiceUnavailable:
                    return PreviewConnect.Unavailable.Instance;
                default:
                    _logger.LogWarning(
                        "preview connect: unexpected status {Status} for a label; treating as error",
                        (int)response.StatusCode);
                    return PreviewConnect.Error.Instance;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "preview connect: control plane call failed; treating as error (502)");
            return PreviewConnect.Error.Instance;
        }
    }

    /// <summary>
    /// Redeem the one-time <paramref name="code"/> the dashboard minted for
    /// <paramref name="label"/> (§8.4). On success returns the per-label preview
    /// session the frontend sets as its own cookie, and its lifetime; on any failure
    /// (bad/expired/replayed code, or a transport error) returns
    /// <see cref="PreviewExchange.Failed"/> and the frontend re-auths.
    /// </summary>
    public async Task<PreviewExchange> ExchangeAsync(string label, string code, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);

            var client = _httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _exchangeUrl)
            {
                Content = JsonContent.Create(new ExchangeRequestBody(label, code)),
            };
            if (!string.IsNullOrEmpty(_options.ControlPlaneBearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ControlPlaneBearer);

            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                return PreviewExchange.Failed.Instance;

            var body = await response.Content.ReadFromJsonAsync<ExchangeResponseBody>(timeoutCts.Token);
            return body is { PreviewSession: { Length: > 0 } session }
                ? new PreviewExchange.Ok(session, body.MaxAgeSeconds)
                : PreviewExchange.Failed.Instance;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "preview exchange: control plane call failed");
            return PreviewExchange.Failed.Instance;
        }
    }
}

/// <summary>Outcome of a <c>/preview/exchange</c> call (§8.4 gated flow).</summary>
public abstract record PreviewExchange
{
    private PreviewExchange() { }

    /// <summary>The code was valid; set <see cref="Session"/> as the per-label cookie for <see cref="MaxAgeSeconds"/>.</summary>
    public sealed record Ok(string Session, int MaxAgeSeconds) : PreviewExchange;

    /// <summary>The code was bad/expired/replayed, or the plane was unreachable — the browser re-auths.</summary>
    public sealed record Failed : PreviewExchange
    {
        public static readonly Failed Instance = new();
    }
}

/// <summary>Outcome of a <c>/preview/connect</c> call, mapped from the plane's status (§8.4).</summary>
public abstract record PreviewConnect
{
    private PreviewConnect() { }

    /// <summary>Armed: the frontend dials the relay as consumer with these.</summary>
    public sealed record Armed(string Grant, string ForwardId, string RelayUrl) : PreviewConnect;

    public sealed record NotFound : PreviewConnect
    {
        public static readonly NotFound Instance = new();
    }

    public sealed record Gone : PreviewConnect
    {
        public static readonly Gone Instance = new();
    }

    public sealed record Unauthorized : PreviewConnect
    {
        public static readonly Unauthorized Instance = new();
    }

    public sealed record Unavailable : PreviewConnect
    {
        public static readonly Unavailable Instance = new();
    }

    /// <summary>Transport failure or an unexpected status — rendered as 502 Bad Gateway.</summary>
    public sealed record Error : PreviewConnect
    {
        public static readonly Error Instance = new();
    }
}
