namespace Landbridge.Classifier;

/// <summary>
/// A <c>provider/model</c> slug such as <c>anthropic/haiku</c> or
/// <c>openai/gpt-5-nano</c>. A bare model name (no slash) is OpenAI, matching
/// the previous <c>LANDBRIDGE_CLASSIFIER_MODEL</c> setting.
/// </summary>
public readonly record struct ModelSlug(string Provider, string Model)
{
    public string Wire => Provider + "/" + Model;

    public bool IsGpt5Family =>
        System.Text.RegularExpressions.Regex.IsMatch(
            Model.Trim(), @"^(gpt-5|o[1-9]|o4)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static bool TryParse(string? raw, out ModelSlug slug)
    {
        slug = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var trimmed = raw.Trim();
        var slash = trimmed.IndexOf('/');
        string provider, model;
        if (slash < 0)
        {
            provider = "openai";
            model = trimmed;
        }
        else
        {
            provider = trimmed[..slash].Trim().ToLowerInvariant();
            model = trimmed[(slash + 1)..].Trim();
        }
        if (provider.Length == 0 || model.Length == 0)
            return false;
        slug = new ModelSlug(provider, ExpandAlias(provider, model));
        return true;
    }

    public static ModelSlug Parse(string raw) =>
        TryParse(raw, out var slug)
            ? slug
            : throw new FormatException($"classifier model '{raw}' is not a provider/model slug");

    private static string ExpandAlias(string provider, string model)
    {
        if (provider != "anthropic")
            return model;
        return model.ToLowerInvariant() switch
        {
            "haiku" => "claude-haiku-4-5-20251001",
            "sonnet" => "claude-sonnet-4-5-20250929",
            "opus" => "claude-opus-4-5",
            _ => model,
        };
    }
}
