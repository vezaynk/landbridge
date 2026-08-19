using Landbridge.Classifier;

namespace Landbridge.Classifier.Tests;

public sealed class ModelSlugTests
{
    [Theory]
    [InlineData("anthropic/haiku", "anthropic", "claude-haiku-4-5-20251001")]
    [InlineData("anthropic/claude-haiku-4-5-20251001", "anthropic", "claude-haiku-4-5-20251001")]
    [InlineData("openai/gpt-5-nano", "openai", "gpt-5-nano")]
    [InlineData("gpt-5-nano", "openai", "gpt-5-nano")]
    [InlineData("xai/grok-4", "xai", "grok-4")]
    public void Parses_provider_model_slugs(string raw, string provider, string model)
    {
        Assert.True(ModelSlug.TryParse(raw, out var slug));
        Assert.Equal(provider, slug.Provider);
        Assert.Equal(model, slug.Model);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/haiku")]
    [InlineData("anthropic/")]
    public void Rejects_empty_slugs(string? raw) =>
        Assert.False(ModelSlug.TryParse(raw, out _));

    [Fact]
    public void Gpt5_family_is_detected_on_the_model_id()
    {
        Assert.True(ModelSlug.Parse("openai/gpt-5-nano").IsGpt5Family);
        Assert.False(ModelSlug.Parse("openai/gpt-4o-mini").IsGpt5Family);
        Assert.False(ModelSlug.Parse("anthropic/haiku").IsGpt5Family);
    }
}
