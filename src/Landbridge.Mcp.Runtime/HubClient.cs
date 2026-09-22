using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp;

/// <summary>
/// Passthrough to the internal hub. Copies the inbound Bearer; Hub owns
/// read authorization. Disabled when <c>Landbridge:HubUrl</c> is unset
/// (test hosts that do not start Hub).
/// </summary>
public sealed class HubClient(HttpClient http, ILogger<HubClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public bool Enabled => http.BaseAddress is not null;

    /// <summary>
    /// The hub's answer, or <c>default</c> when there is no answer to be had.
    /// A caller that falls back to a direct query cannot act on the difference
    /// between "hub says no rows" and "hub never replied", so both come back as
    /// <c>default</c> — but every non-success is logged, because the one thing
    /// worse than a degraded read is a silent one.
    /// </summary>
    public async Task<T?> GetAsync<T>(string path, string bearer, CancellationToken ct)
    {
        if (!Enabled)
            return default;
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<T>(Json, ct);

            // 404 is an ordinary answer: the entity is not there, or not there
            // for this bearer (Hub answers 404 rather than 403 where existence
            // is itself privileged). Anything else is worth a line in the log.
            if (resp.StatusCode is HttpStatusCode.NotFound)
                return default;

            // 401/403 means the bearer this host copied through is not good
            // enough for the hub. That is a wiring fault, not an empty result,
            // and it would otherwise render as a blank page.
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                logger.LogWarning(
                    "hub GET {Path} refused the forwarded bearer: {Status}", path, (int)resp.StatusCode);
            else
                logger.LogWarning("hub GET {Path} failed: {Status}", path, (int)resp.StatusCode);
            return default;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "hub GET {Path} did not complete", path);
            return default;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "hub GET {Path} timed out", path);
            return default;
        }
    }
}


