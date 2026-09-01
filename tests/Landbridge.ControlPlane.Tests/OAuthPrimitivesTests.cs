using System.Security.Cryptography;
using System.Text;
using Landbridge.ControlPlane.Auth;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The pure-logic OAuth primitives (spec §5), no database: PKCE S256 verification,
/// the config-backed operator verifier's fail-closed behaviour, and the canonical
/// resource-id normalisation that backs the RFC 8707 audience binding.
/// </summary>
public sealed class OAuthPrimitivesTests
{
    // ── PKCE (RFC 7636, S256 only) ────────────────────────────────────────────

    private static string S256(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Pkce_verifies_a_correct_s256_pair()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.True(Pkce.VerifyS256(verifier, S256(verifier)));
    }

    [Fact]
    public void Pkce_rejects_a_wrong_verifier()
    {
        var challenge = S256("the-real-verifier");
        Assert.False(Pkce.VerifyS256("a-different-verifier", challenge));
    }

    [Fact]
    public void Pkce_rejects_empty_inputs()
    {
        Assert.False(Pkce.VerifyS256("", S256("x")));
        Assert.False(Pkce.VerifyS256("x", ""));
    }

    [Theory]
    [InlineData("S256", true)]
    [InlineData("plain", false)]   // OAuth 2.1: no downgrade
    [InlineData("s256", false)]    // case-sensitive per RFC 7636
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Pkce_supports_s256_only(string? method, bool expected) =>
        Assert.Equal(expected, Pkce.IsSupportedMethod(method));

    // ── Operator verifier (fail-closed, Identity PBKDF2 at rest) ──────────────

    [Fact]
    public void Verifier_is_unconfigured_and_fails_closed_when_no_hash_is_set()
    {
        var v = new ConfiguredOperatorVerifier((string?)null);
        Assert.False(v.IsConfigured);
        Assert.False(v.Verify("anything")); // never true against an absent credential
    }

    [Fact]
    public void Verifier_treats_a_blank_or_malformed_hash_as_unconfigured()
    {
        Assert.False(new ConfiguredOperatorVerifier("   ").IsConfigured);
        Assert.False(new ConfiguredOperatorVerifier("not-hex").IsConfigured);
        Assert.False(new ConfiguredOperatorVerifier("abcd").IsConfigured);
        // Leftover SHA-256 hex is not a password hash — fail closed, don't treat as a wrong guess.
        Assert.False(new ConfiguredOperatorVerifier(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dev")))).IsConfigured);
    }

    [Fact]
    public void Verifier_accepts_the_right_passphrase_and_rejects_others()
    {
        var v = new ConfiguredOperatorVerifier(OperatorPassphrase.Hash("correct horse battery staple"));
        Assert.True(v.IsConfigured);
        Assert.True(v.Verify("correct horse battery staple"));
        Assert.False(v.Verify("wrong"));
        Assert.False(v.Verify(""));
        Assert.False(v.Verify(null));
    }

    [Fact]
    public void Attempt_limiter_caps_per_key()
    {
        using var limiter = new OperatorAttemptLimiter();
        for (var i = 0; i < OperatorAttemptLimiter.PermitsPerWindow; i++)
            Assert.True(limiter.TryAcquire("10.0.0.1"));
        Assert.False(limiter.TryAcquire("10.0.0.1"));
        Assert.True(limiter.TryAcquire("10.0.0.2"));
    }

    // ── Canonical resource id (RFC 8707 audience binding) ─────────────────────

    [Fact]
    public void Resource_matches_normalise_trailing_slash_and_case()
    {
        var server = new OAuthServerConfig("https://mcp.example.com");
        Assert.Equal("https://mcp.example.com", server.ResourceId);
        Assert.True(server.ResourceMatches("https://mcp.example.com"));
        Assert.True(server.ResourceMatches("https://mcp.example.com/"));   // trailing slash normalised
        Assert.True(server.ResourceMatches("HTTPS://MCP.EXAMPLE.COM"));    // scheme/host case-insensitive
        Assert.False(server.ResourceMatches("https://other.example.com"));
        Assert.False(server.ResourceMatches("https://mcp.example.com/server/mcp")); // different path
    }

    [Fact]
    public void Config_derives_all_endpoints_from_one_public_url_and_strips_trailing_slash()
    {
        var server = OAuthServerConfig.FromPublicMcpUrl("https://mcp.example.com/");
        Assert.Equal("https://mcp.example.com", server.Issuer);
        Assert.Equal("https://mcp.example.com/oauth/authorize", server.AuthorizationEndpoint);
        Assert.Equal("https://mcp.example.com/oauth/token", server.TokenEndpoint);
        Assert.Equal("https://mcp.example.com/.well-known/oauth-protected-resource", server.ResourceMetadataUri);
    }
}
