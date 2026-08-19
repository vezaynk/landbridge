using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The in-memory one-time setup-link store. The capability redeems once into
/// the Lead instructions; unknown, replayed, and expired are the same miss.
/// </summary>
public sealed class LeadSetupLinkStoreTests
{
    [Fact]
    public void Redeems_once_then_misses()
    {
        var store = new LeadSetupLinkStore(new FakeTimeProvider());
        var minted = store.Mint("lbr_l_abc", Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "http://127.0.0.1:5050");

        Assert.StartsWith("lbr_s_", minted.Code, StringComparison.Ordinal);
        var first = store.Redeem(minted.Code);
        Assert.NotNull(first);
        Assert.Equal("lbr_l_abc", first.LeadToken);
        Assert.Equal("http://127.0.0.1:5050", first.McpUrl);

        Assert.Null(store.Redeem(minted.Code));
        Assert.Null(store.Redeem("lbr_s_nope"));
        Assert.Null(store.Redeem(null));
    }

    [Fact]
    public void Expired_capability_misses()
    {
        var clock = new FakeTimeProvider();
        var store = new LeadSetupLinkStore(clock);
        var minted = store.Mint("lbr_l_abc", Guid.NewGuid(), "http://127.0.0.1:5050");

        clock.Advance(LeadSetupLinkStore.Ttl + TimeSpan.FromSeconds(1));
        Assert.Null(store.Redeem(minted.Code));
    }
}
