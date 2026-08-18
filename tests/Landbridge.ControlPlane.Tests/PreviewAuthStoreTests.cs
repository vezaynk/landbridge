using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The in-memory one-time-code + per-label preview-session store behind the §8.4
/// gated browser flow. Pure (no Postgres): codes are single-use and label-bound;
/// sessions are label-bound and expire.
/// </summary>
public sealed class PreviewAuthStoreTests
{
    [Fact]
    public void Code_redeems_once_into_a_session_that_admits_its_label()
    {
        var clock = new FakeTimeProvider();
        var store = new PreviewAuthStore(clock);

        var code = store.MintCode("abc");
        var session = store.Redeem(code, "abc");

        Assert.NotNull(session);
        Assert.True(store.ValidateSession(session, "abc"));
        // Single-use: the code cannot be redeemed a second time.
        Assert.Null(store.Redeem(code, "abc"));
    }

    [Fact]
    public void Code_is_bound_to_its_label()
    {
        var store = new PreviewAuthStore(new FakeTimeProvider());
        var code = store.MintCode("abc");
        // Redeeming against a different label fails (and consumes nothing usable).
        Assert.Null(store.Redeem(code, "xyz"));
    }

    [Fact]
    public void Code_expires()
    {
        var clock = new FakeTimeProvider();
        var store = new PreviewAuthStore(clock);
        var code = store.MintCode("abc");

        clock.Advance(PreviewAuthStore.CodeTtl + TimeSpan.FromSeconds(1));
        Assert.Null(store.Redeem(code, "abc"));
    }

    [Fact]
    public void Session_is_label_bound_and_expires()
    {
        var clock = new FakeTimeProvider();
        var store = new PreviewAuthStore(clock);
        var session = store.Redeem(store.MintCode("abc"), "abc");

        Assert.True(store.ValidateSession(session, "abc"));
        Assert.False(store.ValidateSession(session, "xyz")); // wrong label
        Assert.False(store.ValidateSession(null, "abc"));
        Assert.False(store.ValidateSession("lbr_ps_nope", "abc"));

        clock.Advance(PreviewAuthStore.SessionTtl + TimeSpan.FromSeconds(1));
        Assert.False(store.ValidateSession(session, "abc")); // expired
    }
}
