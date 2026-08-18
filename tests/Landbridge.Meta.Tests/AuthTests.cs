using Landbridge.Meta.Auth;
using Landbridge.Meta.Provisioning;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Meta.Tests;

/// <summary>Operator auth (design note §5): fail-closed verifier + in-memory session lifetimes.</summary>
public class AuthTests
{
    [Fact]
    public void Verifier_is_fail_closed_when_unconfigured()
    {
        var v = new MetaOperatorVerifier((string?)null);
        Assert.False(v.IsConfigured);
        Assert.False(v.Verify("anything"));
    }

    [Fact]
    public void Verifier_treats_malformed_hash_as_unconfigured()
    {
        var v = new MetaOperatorVerifier("not-a-sha256");
        Assert.False(v.IsConfigured);
        Assert.False(v.Verify("anything"));
    }

    [Fact]
    public void Verifier_accepts_only_the_configured_passphrase()
    {
        var hash = SecretGenerator.Hash("correct horse");
        var v = new MetaOperatorVerifier(hash);
        Assert.True(v.IsConfigured);
        Assert.True(v.Verify("correct horse"));
        Assert.False(v.Verify("wrong"));
        Assert.False(v.Verify(null));
        Assert.False(v.Verify(""));
    }

    [Fact]
    public void Session_is_valid_until_revoked()
    {
        var store = new MetaSessionStore(new FakeTimeProvider());
        var (token, _) = store.Create();
        Assert.True(store.IsValid(token));
        store.Revoke(token);
        Assert.False(store.IsValid(token));
        Assert.False(store.IsValid("mst_unknown"));
        Assert.False(store.IsValid(null));
    }

    [Fact]
    public void Session_expires_after_its_ttl()
    {
        var clock = new FakeTimeProvider();
        var store = new MetaSessionStore(clock, TimeSpan.FromHours(1));
        var (token, _) = store.Create();
        Assert.True(store.IsValid(token));
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.False(store.IsValid(token));
    }
}
