using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docket.Relay;

/// <summary>Options for <see cref="ControlPlaneGrantValidator"/> (config section <c>Relay:ControlPlane</c>).</summary>
public sealed class ControlPlaneGrantValidatorOptions
{
    public const string SectionName = "Relay:ControlPlane";

    /// <summary>
    /// Base URL of the control plane. When set, the relay validates grants against
    /// it (<c>{Url}/relay/validate</c>) instead of the static-secret stub. When
    /// unset, <see cref="RelayServiceCollectionExtensions.AddRelay"/> leaves the
    /// default <see cref="StaticSecretGrantValidator"/> in place.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>The shared bearer the plane's <c>/relay/validate</c> endpoint requires (§8.3).</summary>
    public string? Bearer { get; set; }

    /// <summary>How long to wait for the plane before failing closed. Short — this is the tunnel-open path.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The real <see cref="IGrantValidator"/> (spec §8.3): asks the control plane
/// whether a presented grant is valid for a <c>{forwardId, role}</c>, over a
/// plain HTTP POST authenticated with a shared bearer. Registered in place of
/// <see cref="StaticSecretGrantValidator"/> when <c>Relay:ControlPlane:Url</c> is
/// configured.
///
/// <para><b>Fail-closed by construction.</b> Any doubt — plane unreachable, a
/// timeout, a non-200, a malformed answer, an unset bearer — returns
/// <c>false</c>, so a tunnel is refused rather than spliced on a grant the plane
/// never actually blessed. The relay is the widest hole in the model (§13); the
/// safe default is to deny.</para>
/// </summary>
public sealed class ControlPlaneGrantValidator : IGrantValidator
{
    /// <summary>Named client so a host can attach its own resilience/handlers to just this traffic.</summary>
    public const string HttpClientName = "docket-relay-controlplane";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ControlPlaneGrantValidatorOptions _options;
    private readonly ILogger<ControlPlaneGrantValidator> _logger;
    private readonly string _validateUrl;

    public ControlPlaneGrantValidator(
        IHttpClientFactory httpFactory,
        IOptions<ControlPlaneGrantValidatorOptions> options,
        ILogger<ControlPlaneGrantValidator> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
        _validateUrl = (_options.Url ?? "").TrimEnd('/') + "/relay/validate";

        if (string.IsNullOrEmpty(_options.Bearer))
            _logger.LogWarning(
                "Relay control-plane validator has no bearer configured ({Section}:Bearer); the plane will " +
                "refuse every validation until one is set.", ControlPlaneGrantValidatorOptions.SectionName);
    }

    private sealed record ValidateResponse(bool Valid);

    public async Task<bool> ValidateAsync(string grant, string forwardId, RelayRole role, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.Timeout);

            var client = _httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _validateUrl)
            {
                Content = JsonContent.Create(new
                {
                    grant,
                    forwardId,
                    role = role == RelayRole.Consumer ? "consumer" : "producer",
                }),
            };
            if (!string.IsNullOrEmpty(_options.Bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Bearer);

            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "control-plane grant validation returned {Status} for forward {ForwardId} role {Role}; refusing",
                    (int)response.StatusCode, forwardId, role);
                return false;
            }

            var body = await response.Content.ReadFromJsonAsync<ValidateResponse>(timeoutCts.Token);
            return body?.Valid == true;
        }
        catch (Exception e)
        {
            // Fail-closed (§8.3, §13): unreachable plane, timeout, or a malformed
            // answer all refuse the tunnel. A grant is never accepted on doubt.
            _logger.LogWarning(
                e, "control-plane grant validation failed for forward {ForwardId} role {Role}; refusing",
                forwardId, role);
            return false;
        }
    }
}
