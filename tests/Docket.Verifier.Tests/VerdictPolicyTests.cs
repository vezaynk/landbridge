using System.Text.RegularExpressions;
using Docket.Verifier;

namespace Docket.Verifier.Tests;

/// <summary>
/// The verdict decision (§7) in isolation — no plane, no HTTP. The one place the
/// null/empty-reference and pattern-match rules are pinned, since the plane's
/// own invariant (§6) means an empty reference never actually reaches the wire.
/// </summary>
public sealed class VerdictPolicyTests
{
    private static VerdictPolicy Policy(string pattern) =>
        new(new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    [Theory]
    [InlineData("abc")]
    [InlineData("https://ci/run/42")]
    [InlineData("0")]
    public void Default_pattern_accepts_any_non_blank_reference(string reference)
    {
        Assert.True(Policy(".*").Accepts(reference));
        Assert.Equal("accept", Policy(".*").Decide(reference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_missing_or_blank_reference_always_fails_even_under_the_permissive_default(string? reference)
    {
        // .* matches the empty string, so the emptiness guard — not the pattern —
        // is what makes a missing reference fail.
        Assert.False(Policy(".*").Accepts(reference));
        Assert.Equal("fail", Policy(".*").Decide(reference));
    }

    [Fact]
    public void A_specific_pattern_accepts_only_a_matching_reference()
    {
        var policy = Policy("^https://ci/");
        Assert.Equal("accept", policy.Decide("https://ci/run/42"));
        Assert.Equal("fail", policy.Decide("ftp://elsewhere/run/42"));
        Assert.Equal("fail", policy.Decide("a https://ci/ prefix is not at the start"));
    }

    [Fact]
    public void The_pattern_is_a_search_not_an_implicit_full_match()
    {
        // No implicit anchoring: an unanchored pattern matches anywhere, matching
        // Regex.IsMatch semantics the operator expects.
        var policy = Policy("passed");
        Assert.Equal("accept", policy.Decide("suite passed with 0 failures"));
        Assert.Equal("fail", policy.Decide("suite failed"));
    }
}
