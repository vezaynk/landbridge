using Landbridge.Classifier;
using Microsoft.Extensions.Configuration;

namespace Landbridge.Classifier.Tests;

public sealed class ClassifierSettingsTests
{
    [Fact]
    public void Loads_per_stage_model_and_prompt_from_config()
    {
        var settings = Load(new Dictionary<string, string?>
        {
            ["Classifier:Fast:Model"] = "openai/gpt-5-nano",
            ["Classifier:Review:Model"] = "anthropic/haiku",
            ["Classifier:Fast:Prompt"] = "fast-template",
            ["Classifier:Review:Prompt"] = "review-template",
            ["Classifier:LiteLlm:Url"] = "http://127.0.0.1:4000/v1",
            ["Classifier:LiteLlm:ApiKey"] = "sk-test",
        });

        Assert.Equal("openai", settings.Fast.Slug.Provider);
        Assert.Equal("gpt-5-nano", settings.Fast.Slug.Model);
        Assert.Equal("fast-template", settings.Fast.Prompt);
        Assert.Equal("anthropic", settings.Review.Slug.Provider);
        Assert.Equal("claude-haiku-4-5-20251001", settings.Review.Slug.Model);
        Assert.Equal("review-template", settings.Review.Prompt);
        Assert.Equal("http://127.0.0.1:4000/v1", settings.LiteLlm.Url);
        Assert.Equal("sk-test", settings.LiteLlm.ApiKey);
    }

    [Fact]
    public void Env_stage_model_wins_over_appsettings()
    {
        var settings = Load(new Dictionary<string, string?>
        {
            ["Classifier:Fast:Model"] = "openai/gpt-4o-mini",
            ["LANDBRIDGE_CLASSIFIER_FAST_MODEL"] = "anthropic/haiku",
            ["Classifier:Review:Model"] = "openai/gpt-4o-mini",
            ["Classifier:Fast:Prompt"] = "f",
            ["Classifier:Review:Prompt"] = "r",
            ["Classifier:LiteLlm:Url"] = "http://127.0.0.1:4000/v1",
            ["Classifier:LiteLlm:ApiKey"] = "sk-test",
        });
        Assert.Equal("anthropic/claude-haiku-4-5-20251001", settings.Fast.Slug.Wire);
        Assert.Equal("openai/gpt-4o-mini", settings.Review.Slug.Wire);
    }

    [Fact]
    public void Falls_back_to_a_bare_legacy_model_name()
    {
        var settings = Load(new Dictionary<string, string?>
        {
            ["LANDBRIDGE_CLASSIFIER_MODEL"] = "gpt-4o-mini",
            ["Classifier:LiteLlm:Url"] = "http://127.0.0.1:4000/v1",
            ["Classifier:LiteLlm:ApiKey"] = "sk-legacy",
            ["Classifier:Fast:Prompt"] = "f",
            ["Classifier:Review:Prompt"] = "r",
        });
        Assert.Equal("openai/gpt-4o-mini", settings.Fast.Slug.Wire);
        Assert.Equal("openai/gpt-4o-mini", settings.Review.Slug.Wire);
    }

    [Fact]
    public void Reads_a_prompt_file()
    {
        var dir = Directory.CreateTempSubdirectory("classifier-prompts");
        var file = Path.Combine(dir.FullName, "fast.txt");
        File.WriteAllText(file, "from-file");
        var settings = Load(new Dictionary<string, string?>
        {
            ["Classifier:Fast:Model"] = "openai/gpt-4o-mini",
            ["Classifier:Review:Model"] = "openai/gpt-4o-mini",
            ["Classifier:Fast:PromptFile"] = file,
            ["Classifier:Review:Prompt"] = "review",
            ["Classifier:LiteLlm:Url"] = "http://127.0.0.1:4000/v1",
            ["Classifier:LiteLlm:ApiKey"] = "sk-test",
        }, dir.FullName);
        Assert.Equal("from-file", settings.Fast.Prompt);
    }

    [Fact]
    public void Missing_litellm_url_fails()
    {
        Assert.False(ClassifierSettings.TryLoad(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Classifier:Fast:Model"] = "anthropic/haiku",
                ["Classifier:Review:Model"] = "anthropic/haiku",
                ["Classifier:Fast:Prompt"] = "f",
                ["Classifier:Review:Prompt"] = "r",
            }).Build(),
            ".",
            out _,
            out var error));
        Assert.Contains("LiteLlm", error, StringComparison.Ordinal);
    }

    private static ClassifierSettings Load(Dictionary<string, string?> values, string root = ".")
    {
        Assert.True(ClassifierSettings.TryLoad(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            root, out var settings, out var error), error);
        return settings;
    }
}