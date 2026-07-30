using Docket.Verifier;

namespace Docket.Verifier.Tests;

/// <summary>
/// Config parsing/validation (§10 discipline): every problem reported at once,
/// the secret read from the environment and never a flag, and clear defaults.
/// </summary>
public sealed class VerifierConfigTests
{
    private static Func<string, string?> Env(string? token) =>
        name => name == VerifierConfig.TokenEnvVar ? token : null;

    private const string Token = "dkt_v_" + "0123456789abcdef";

    [Fact]
    public void Happy_path_parses_url_interval_pattern_and_env_token()
    {
        Assert.True(VerifierConfig.TryParse(
            ["--plane", "http://localhost:5000", "--interval", "5", "--accept-pattern", "^ok:"],
            Env(Token), out var config, out _));

        Assert.Equal(new Uri("http://localhost:5000"), config.PlaneUrl);
        Assert.Equal(TimeSpan.FromSeconds(5), config.Interval);
        Assert.Equal("^ok:", config.AcceptPatternSource);
        Assert.Matches(config.AcceptPattern, "ok: done");
        Assert.Equal(Token, config.Token);
    }

    [Fact]
    public void Defaults_apply_when_only_plane_is_given()
    {
        Assert.True(VerifierConfig.TryParse(
            ["--plane", "https://plane.example"], Env(Token), out var config, out _));

        Assert.Equal(TimeSpan.FromSeconds(VerifierConfig.DefaultIntervalSeconds), config.Interval);
        Assert.Equal(VerifierConfig.DefaultAcceptPattern, config.AcceptPatternSource);
        // The default accepts any non-blank reference.
        Assert.Matches(config.AcceptPattern, "anything");
    }

    [Fact]
    public void Missing_plane_is_an_error()
    {
        Assert.False(VerifierConfig.TryParse([], Env(Token), out _, out var errors));
        Assert.Contains(errors, e => e.Contains("--plane"));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://plane")]
    [InlineData("/relative/only")]
    public void A_non_http_plane_url_is_an_error(string plane)
    {
        Assert.False(VerifierConfig.TryParse(["--plane", plane], Env(Token), out _, out var errors));
        Assert.Contains(errors, e => e.Contains("--plane"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    public void A_non_positive_or_unparseable_interval_is_an_error(string interval)
    {
        Assert.False(VerifierConfig.TryParse(
            ["--plane", "http://p", "--interval", interval], Env(Token), out _, out var errors));
        Assert.Contains(errors, e => e.Contains("--interval"));
    }

    [Fact]
    public void An_invalid_regex_is_an_error()
    {
        Assert.False(VerifierConfig.TryParse(
            ["--plane", "http://p", "--accept-pattern", "("], Env(Token), out _, out var errors));
        Assert.Contains(errors, e => e.Contains("--accept-pattern"));
    }

    [Fact]
    public void A_missing_token_env_is_an_error_and_the_token_is_never_read_from_argv()
    {
        // Even with a token-looking flag present, absence of the env var fails —
        // argv is never consulted for the secret.
        Assert.False(VerifierConfig.TryParse(
            ["--plane", "http://p", "--token", Token], Env(null), out _, out var errors));
        Assert.Contains(errors, e => e.Contains(VerifierConfig.TokenEnvVar));
    }

    [Fact]
    public void All_problems_are_reported_at_once()
    {
        Assert.False(VerifierConfig.TryParse(
            ["--interval", "nope", "--accept-pattern", "["], Env(null), out _, out var errors));
        // missing plane, bad interval, bad regex, missing token.
        Assert.Equal(4, errors.Count);
    }
}
