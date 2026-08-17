namespace Docket.HarnessBump.Tests;

/// <summary>
/// The dist-tag read, against the real <c>dist-tags</c> objects these three packages served on
/// 2026-08-13. The fixtures are trimmed to the tags that matter, but every version string is
/// verbatim, because the point of each is that a plausible alternative implementation picks it.
/// </summary>
public class NpmRegistryTests
{
    [Fact]
    public void Claude_reads_latest_and_not_stable_or_next()
    {
        // `stable` sits BELOW `latest` here, and `next` happens to equal it. Reading either tag
        // by name would be wrong the moment they diverge.
        const string packument = """
            {"dist-tags":{"stable":"2.1.223","next":"2.1.231","latest":"2.1.231"}}
            """;
        Assert.Equal("2.1.231", NpmRegistry.ReadLatestDistTag(packument, "@anthropic-ai/claude-code"));
    }

    [Fact]
    public void Codex_reads_latest_and_not_the_alpha_that_is_numerically_above_it()
    {
        // dist-tags.alpha is 0.148.0-alpha.12 while latest is 0.147.0. A "max over dist-tags"
        // implementation installs the alpha.
        const string packument = """
            {"dist-tags":{
              "beta":"0.1.2505172116",
              "linux-x64":"0.147.0-linux-x64",
              "latest":"0.147.0",
              "alpha":"0.148.0-alpha.12",
              "alpha-linux-x64":"0.148.0-alpha.12-linux-x64"
            }}
            """;
        Assert.Equal("0.147.0", NpmRegistry.ReadLatestDistTag(packument, "@openai/codex"));
    }

    [Fact]
    public void Opencode_reads_latest_and_not_the_dev_snapshot_published_minutes_ago()
    {
        // opencode-ai carries ~40 snapshot tags plus dev/beta/next, all pointing at
        // 0.0.0-<branch>-<stamp> builds published far more recently than 1.18.18.
        const string packument = """
            {"dist-tags":{
              "latest-0":"1.0.142",
              "latest-1":"1.1.4",
              "ci":"0.0.0-ci-202601291809",
              "next":"0.0.0-next-202606270058",
              "beta":"0.0.0-beta-202608110357",
              "dev":"0.0.0-dev-202608131633",
              "latest":"1.18.18"
            }}
            """;
        Assert.Equal("1.18.18", NpmRegistry.ReadLatestDistTag(packument, "opencode-ai"));
    }

    [Fact]
    public void The_abbreviated_packument_shape_is_enough()
    {
        // The tool asks for application/vnd.npm.install-v1+json, which omits `time` — the very
        // field a newest-by-publish-date implementation would need. Assert we do not need it.
        const string packument = """
            {"name":"opencode-ai","dist-tags":{"latest":"1.18.18"},"versions":{"1.18.18":{}},"modified":"x"}
            """;
        Assert.Equal("1.18.18", NpmRegistry.ReadLatestDistTag(packument, "opencode-ai"));
    }

    [Theory]
    [InlineData("""{"versions":{}}""")]
    [InlineData("""{"dist-tags":"nope"}""")]
    [InlineData("""{"dist-tags":{"next":"1.0.0"}}""")]
    [InlineData("""{"dist-tags":{"latest":""}}""")]
    [InlineData("""{"dist-tags":{"latest":123}}""")]
    public void A_response_without_a_usable_latest_is_an_error_not_a_silent_skip(string packument)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => NpmRegistry.ReadLatestDistTag(packument, "opencode-ai"));
        Assert.Contains("opencode-ai", error.Message);
    }
}
