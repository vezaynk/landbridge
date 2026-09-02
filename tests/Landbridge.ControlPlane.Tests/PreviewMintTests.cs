using Landbridge.Core;

namespace Landbridge.ControlPlane.Tests;

public sealed class PreviewMintTests
{
    [Fact]
    public void Both_policies_default_to_two_hours()
    {
        Assert.Equal(TimeSpan.FromHours(2), PreviewMint.DefaultTtl);
        Assert.Equal(PreviewMint.DefaultTtl, PreviewMint.ResolveTtl(PreviewAuthPolicy.Gated, null));
        Assert.Equal(PreviewMint.DefaultTtl, PreviewMint.ResolveTtl(PreviewAuthPolicy.Public, null));
        Assert.Equal(PreviewMint.DefaultTtl, PreviewMint.ResolveTtl(PreviewAuthPolicy.Public, 0));
    }

    [Fact]
    public void A_positive_request_is_honoured_and_public_is_capped()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), PreviewMint.ResolveTtl(PreviewAuthPolicy.Public, 10));
        Assert.Equal(TimeSpan.FromHours(3), PreviewMint.ResolveTtl(PreviewAuthPolicy.Gated, 180));
        Assert.Equal(PreviewMint.PublicMaxTtl, PreviewMint.ResolveTtl(PreviewAuthPolicy.Public, 60 * 48));
    }
}
