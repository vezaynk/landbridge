using Microsoft.Extensions.Configuration;

namespace Landbridge.Classifier;

/// <summary>
/// Per-stage model + prompt, loaded from config / env. Each stage names a
/// <c>provider/model</c> slug (<c>anthropic/haiku</c>, <c>openai/gpt-5-nano</c>).
/// </summary>
public sealed class ClassifierSettings
{
    public const string Section = "Classifier";

    public required JudgeStage Fast { get; init; }
    public required JudgeStage Review { get; init; }
    public required LiteLlmSettings LiteLlm { get; init; }

    public static ClassifierSettings Load(IConfiguration config, string contentRoot)
    {
        if (!TryLoad(config, contentRoot, out var settings, out var error))
            throw new InvalidOperationException(error);
        return settings;
    }

    public static bool TryLoad(
        IConfiguration config, string contentRoot,
        out ClassifierSettings settings, out string error)
    {
        settings = null!;
        error = "";

        var section = config.GetSection(Section);
        var fallbackModel = First(config, "LANDBRIDGE_CLASSIFIER_MODEL", "Classifier:Model");

        if (!TryStage(config, section.GetSection("Fast"), "Fast",
                fallbackModel, Prompt.Fast, contentRoot, 10_000, 256, out var fast, out error))
            return false;
        if (!TryStage(config, section.GetSection("Review"), "Review",
                fallbackModel, Prompt.Review, contentRoot, 30_000, 4096, out var review, out error))
            return false;

        var url = First(config,
            "Classifier:LiteLlm:Url", "LANDBRIDGE_CLASSIFIER_LITELLM_URL");
        var key = First(config,
            "Classifier:LiteLlm:ApiKey", "LANDBRIDGE_CLASSIFIER_LITELLM_KEY",
            "LANDBRIDGE_CLASSIFIER_API_KEY");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
        {
            error =
                "landbridge-classifier: Classifier:LiteLlm:Url and Classifier:LiteLlm:ApiKey are required (the local LiteLLM gateway)";
            return false;
        }

        settings = new ClassifierSettings
        {
            Fast = fast,
            Review = review,
            LiteLlm = new LiteLlmSettings(url.Trim().TrimEnd('/'), key.Trim()),
        };
        return true;
    }

    private static bool TryStage(
        IConfiguration config, IConfiguration section, string name,
        string? fallbackModel, string defaultPrompt, string contentRoot,
        int timeoutMs, int maxTokens,
        out JudgeStage stage, out string error)
    {
        stage = null!;
        error = "";
        var raw = FirstNonEmpty(
            config[$"LANDBRIDGE_CLASSIFIER_{name.ToUpperInvariant()}_MODEL"],
            section["Model"],
            fallbackModel);
        if (!ModelSlug.TryParse(raw, out var slug))
        {
            error =
                $"landbridge-classifier: Classifier:{name}:Model (or LANDBRIDGE_CLASSIFIER_{name.ToUpperInvariant()}_MODEL / LANDBRIDGE_CLASSIFIER_MODEL) is required as a provider/model slug, e.g. anthropic/haiku";
            return false;
        }

        var prompt = FirstNonEmpty(
            config[$"LANDBRIDGE_CLASSIFIER_{name.ToUpperInvariant()}_PROMPT"],
            section["Prompt"]);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            var file = FirstNonEmpty(section["PromptFile"], config[$"LANDBRIDGE_CLASSIFIER_{name.ToUpperInvariant()}_PROMPT_FILE"]);
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!TryReadPromptFile(contentRoot, file, out prompt, out error))
                    return false;
            }
        }
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = defaultPrompt;

        stage = new JudgeStage(slug, prompt.Trim(), timeoutMs, maxTokens);
        return true;
    }

    private static bool TryReadPromptFile(string contentRoot, string file, out string prompt, out string error)
    {
        prompt = "";
        error = "";
        var path = Path.IsPathRooted(file) ? file : Path.Combine(contentRoot, file);
        if (!File.Exists(path))
            path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path))
        {
            error = $"landbridge-classifier: prompt file '{file}' was not found";
            return false;
        }
        prompt = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            error = $"landbridge-classifier: prompt file '{file}' is empty";
            return false;
        }
        return true;
    }

    private static string? First(IConfiguration config, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = config[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return null;
    }
}

public sealed record JudgeStage(ModelSlug Slug, string Prompt, int TimeoutMs, int MaxTokens);

public sealed record LiteLlmSettings(string Url, string ApiKey);
