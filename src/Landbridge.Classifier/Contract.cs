using System.Text.Json;
using System.Text.Json.Serialization;

namespace Landbridge.Classifier;

public sealed record ClassifyRequest(
    string? Tool,
    JsonElement? Input,
    string? Session,
    string[]? Messages);

public sealed record ClassifyResponse(
    [property: JsonPropertyName("disposition")] string Disposition,
    [property: JsonPropertyName("via")] string Via,
    [property: JsonPropertyName("reason")] string Reason);

public static class ClassifyResult
{
    public static ClassifyResponse Allow(string via, string reason = "") =>
        new("allow", via, reason);

    public static ClassifyResponse Ask(string via, string reason = "") =>
        new("ask", via, reason);
}
