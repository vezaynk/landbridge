using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Landbridge.Mcp;

/// <summary>
/// Passthrough to the internal hub. Copies the inbound Bearer; Hub owns
/// read authorization. Disabled when <c>Landbridge:HubUrl</c> is unset
/// (test hosts that do not start Hub).
/// </summary>
public sealed class HubClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public bool Enabled => http.BaseAddress is not null;

    public async Task<T?> GetAsync<T>(string path, string bearer, CancellationToken ct)
    {
        if (!Enabled)
            return default;
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.NotFound)
            return default;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}

/// <summary>Wire twin of the hub machine document. Routing fields only.</summary>
public sealed record HubMachine(
    Guid Id,
    string Slug,
    string Name,
    string Os,
    bool Ready,
    bool UnderBackPressure,
    bool Live,
    DateTimeOffset? LastSpokeAt,
    string[] Profiles);
