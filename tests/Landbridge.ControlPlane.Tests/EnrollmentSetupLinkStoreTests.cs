using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The in-memory one-time enrollment setup-link store. The capability redeems
/// once into the enrollment instructions; unknown, replayed, and expired are
/// the same miss.
/// </summary>
public sealed class EnrollmentSetupLinkStoreTests
{
    [Fact]
    public void Redeems_once_then_misses()
    {
        var store = new EnrollmentSetupLinkStore(new FakeTimeProvider());
        var minted = store.Mint("lbr_e_abc", "https://auth.example", DateTimeOffset.Parse("2026-10-21T12:00:00Z"));

        Assert.StartsWith("lbr_es_", minted.Code, StringComparison.Ordinal);
        var first = store.Redeem(minted.Code);
        Assert.NotNull(first);
        Assert.Equal("lbr_e_abc", first.Token);
        Assert.Equal("https://auth.example", first.AuthUrl);

        Assert.Null(store.Redeem(minted.Code));
        Assert.Null(store.Redeem("lbr_es_nope"));
        Assert.Null(store.Redeem(null));
    }

    [Fact]
    public void Expired_capability_misses()
    {
        var clock = new FakeTimeProvider();
        var store = new EnrollmentSetupLinkStore(clock);
        var minted = store.Mint("lbr_e_abc", "https://auth.example", null);

        clock.Advance(EnrollmentSetupLinkStore.Ttl + TimeSpan.FromSeconds(1));
        Assert.Null(store.Redeem(minted.Code));
    }
}
