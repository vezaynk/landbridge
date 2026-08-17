namespace Docket.HarnessBump.Tests;

/// <summary>
/// The comparison rules, exercised against the version strings the three harnesses really
/// publish — because the two ways to get this wrong are both live on the registry today.
/// </summary>
public class SemVerTests
{
    private static SemVer Parse(string text)
    {
        Assert.True(SemVer.TryParse(text, out var version), $"'{text}' should parse");
        return version;
    }

    [Theory]
    [InlineData("2.1.229", 2, 1, 229, "")]
    [InlineData("0.147.0", 0, 147, 0, "")]
    [InlineData("1.18.18", 1, 18, 18, "")]
    [InlineData("0.148.0-alpha.12", 0, 148, 0, "alpha.12")]
    [InlineData("0.0.0-dev-202608131633", 0, 0, 0, "dev-202608131633")]
    [InlineData("0.147.0-linux-x64", 0, 147, 0, "linux-x64")]
    [InlineData("1.2.3+build.5", 1, 2, 3, "")]
    public void Parses_the_shapes_these_packages_publish(
        string text, int major, int minor, int patch, string prerelease)
    {
        var version = Parse(text);
        Assert.Equal((major, minor, patch, prerelease),
            (version.Major, version.Minor, version.Patch, version.Prerelease));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("01.2.3-ok-but-leading-zero-is-not")]
    public void Refuses_what_is_not_a_semver(string? text)
    {
        // The planner turns a parse failure into "refusing to guess", which is the whole point:
        // a pin the tool cannot understand must never be rewritten on a guess.
        Assert.False(SemVer.TryParse(text, out _));
    }

    [Fact]
    public void A_newer_patch_is_newer()
    {
        // The live claude case: 2.1.229 is pinned, 2.1.231 is `latest`.
        Assert.True(SemVer.Compare(Parse("2.1.231"), Parse("2.1.229")) > 0);
        Assert.True(SemVer.Compare(Parse("1.18.18"), Parse("1.18.17")) > 0);
    }

    [Fact]
    public void An_alpha_above_latest_outranks_it_numerically_but_is_still_a_prerelease()
    {
        // @openai/codex really does publish 0.148.0-alpha.12 while `latest` is 0.147.0. A
        // triple-only comparison would call this an upgrade; the planner's prerelease guard is
        // what stops it, so both halves of the claim are asserted here.
        var alpha = Parse("0.148.0-alpha.12");
        Assert.True(SemVer.Compare(alpha, Parse("0.147.0")) > 0);
        Assert.True(alpha.IsPrerelease);
    }

    [Fact]
    public void An_opencode_dev_snapshot_is_the_newest_by_time_and_the_oldest_by_semver()
    {
        // The trap the whole dist-tags.latest decision exists for: 0.0.0-dev-<stamp> is the most
        // recently published thing on opencode-ai, and it is BELOW the pinned 1.18.17.
        var snapshot = Parse("0.0.0-dev-202608131633");
        Assert.True(SemVer.Compare(snapshot, Parse("1.18.17")) < 0);
        Assert.True(SemVer.Compare(snapshot, Parse("1.18.18")) < 0);
    }

    [Fact]
    public void Prerelease_ordering_follows_the_spec_example()
    {
        string[] ascending =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
        ];
        for (var i = 0; i < ascending.Length - 1; i++)
            Assert.True(
                SemVer.Compare(Parse(ascending[i]), Parse(ascending[i + 1])) < 0,
                $"{ascending[i]} should precede {ascending[i + 1]}");
    }

    [Fact]
    public void Build_metadata_does_not_affect_ordering()
    {
        Assert.Equal(0, SemVer.Compare(Parse("1.2.3+a"), Parse("1.2.3+b")));
        Assert.Equal(0, SemVer.Compare(Parse("1.2.3"), Parse("1.2.3+b")));
    }

    [Fact]
    public void Equal_versions_compare_equal_so_an_up_to_date_pin_is_not_a_bump()
    {
        Assert.Equal(0, SemVer.Compare(Parse("0.147.0"), Parse("0.147.0")));
    }
}
