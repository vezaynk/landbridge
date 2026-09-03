using Landbridge.ControlPlane;

namespace Landbridge.ControlPlane.Tests;

public sealed class HaikuSlugTests
{
    [Fact]
    public void Mint_is_well_formed_and_not_constant()
    {
        var a = HaikuSlug.Mint();
        var b = HaikuSlug.Mint();
        Assert.True(HaikuSlug.IsWellFormed(a));
        Assert.True(HaikuSlug.IsWellFormed(b));
        // Two draws can collide; a handful of draws must not all match.
        var set = new HashSet<string>(StringComparer.Ordinal) { a, b };
        for (var i = 0; i < 16; i++)
            set.Add(HaikuSlug.Mint());
        Assert.True(set.Count > 1);
    }

    [Theory]
    [InlineData("quiet-river-0001")]
    [InlineData("row-sess-0042")]
    [InlineData("a-b-9999")]
    public void IsWellFormed_accepts_adjective_noun_digits(string slug) =>
        Assert.True(HaikuSlug.IsWellFormed(slug));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("quiet-river-01")]
    [InlineData("quiet-river-00001")]
    [InlineData("Quiet-river-0001")]
    [InlineData("e09cfd93-eb81-4b32-97f2-af808581d94f")]
    [InlineData("quiet_river_0001")]
    public void IsWellFormed_rejects_other_shapes(string? slug) =>
        Assert.False(HaikuSlug.IsWellFormed(slug));
}
