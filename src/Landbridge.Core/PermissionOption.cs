using System.Text.Json;
using System.Text.Json.Serialization;

namespace Landbridge.Core;

/// <summary>
/// One ACP <c>session/request_permission</c> option, stored and shown as the
/// harness offered it. The engine still decides in <see cref="PermissionVerdict"/>;
/// this is how a Lead or human picks among the agent's own buttons.
/// </summary>
public sealed record PermissionOption(
    [property: JsonPropertyName("optionId")] string OptionId,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("kind")] string? Kind = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsReject =>
        string.Equals(Kind, "reject_once", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Kind, "reject_always", StringComparison.OrdinalIgnoreCase);

    public PermissionVerdict Verdict =>
        IsReject ? PermissionVerdict.Deny : PermissionVerdict.Allow;

    public static IReadOnlyList<PermissionOption> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var parsed = JsonSerializer.Deserialize<List<PermissionOption>>(json, Json);
            if (parsed is null || parsed.Count == 0)
                return [];
            return parsed
                .Where(o => !string.IsNullOrWhiteSpace(o.OptionId))
                .Select(o => o with { OptionId = o.OptionId.Trim() })
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Match a Lead/human choice against the offered list: optionId first, then
    /// kind, then the <c>allow</c>/<c>deny</c> aliases. Null when nothing fits.
    /// </summary>
    public static PermissionOption? Resolve(IReadOnlyList<PermissionOption> options, string? choice)
    {
        if (options.Count == 0 || string.IsNullOrWhiteSpace(choice))
            return null;
        var want = choice.Trim();

        foreach (var o in options)
        {
            if (string.Equals(o.OptionId, want, StringComparison.Ordinal))
                return o;
        }

        foreach (var o in options)
        {
            if (string.Equals(o.Kind, want, StringComparison.OrdinalIgnoreCase))
                return o;
        }

        if (string.Equals(want, "allow", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var o in options)
            {
                if (string.Equals(o.Kind, "allow_once", StringComparison.OrdinalIgnoreCase))
                    return o;
            }
            foreach (var o in options)
            {
                if (!o.IsReject)
                    return o;
            }
            return null;
        }

        if (string.Equals(want, "deny", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var o in options)
            {
                if (string.Equals(o.Kind, "reject_once", StringComparison.OrdinalIgnoreCase))
                    return o;
            }
            foreach (var o in options)
            {
                if (string.Equals(o.Kind, "reject_always", StringComparison.OrdinalIgnoreCase))
                    return o;
            }
            foreach (var o in options)
            {
                if (o.IsReject)
                    return o;
            }
        }

        return null;
    }
}
