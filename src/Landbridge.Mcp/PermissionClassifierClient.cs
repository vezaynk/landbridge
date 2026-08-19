using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.Core;

namespace Landbridge.Mcp;

/// <summary>
/// HTTP client for the permission-classifier sidecar. Any non-allow response,
/// transport failure, or timeout is Ask — the plane never fail-opens.
/// </summary>
public sealed class PermissionClassifierClient(HttpClient http) : IPermissionClassifier
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<PermissionDisposition> ClassifyAsync(
        SessionId session, string tool, string proposedInput,
        IReadOnlyList<string> leadMessages, CancellationToken ct)
    {
        JsonElement? input = null;
        if (!string.IsNullOrWhiteSpace(proposedInput))
        {
            try
            {
                input = JsonSerializer.Deserialize<JsonElement>(proposedInput);
            }
            catch (JsonException)
            {
                input = JsonSerializer.SerializeToElement(proposedInput);
            }
        }

        using var response = await http.PostAsJsonAsync(
            "classify",
            new ClassifyRequest(
                tool, input, session.Value.ToString("N"),
                leadMessages.Count == 0 ? null : leadMessages),
            Json,
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return PermissionDisposition.Ask;

        var body = await response.Content
            .ReadFromJsonAsync<ClassifyResponse>(Json, ct)
            .ConfigureAwait(false);
        if (body is null)
            return PermissionDisposition.Ask;
        if (string.Equals(body.Disposition, "allow", StringComparison.OrdinalIgnoreCase))
            return PermissionDisposition.AutoAllow;
        return PermissionDisposition.Ask;
    }

    private sealed record ClassifyRequest(
        string Tool, JsonElement? Input, string Session, IReadOnlyList<string>? Messages);

    private sealed record ClassifyResponse(string? Disposition, string? Via, string? Reason);
}
