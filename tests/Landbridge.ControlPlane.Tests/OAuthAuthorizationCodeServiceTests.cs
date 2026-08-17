using System.Security.Cryptography;
using System.Text;
using Landbridge.ControlPlane.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The OAuth 2.1 authorization-code service (spec §5): mint a single-use code
/// bound to {client_id, redirect_uri, code_challenge, resource}, then redeem it
/// at the token endpoint with the matching PKCE verifier. Backed by the same
/// ephemeral Postgres fixture as the rest of the store, so it runs the real
/// schema (the AddOAuthAuthorizationCodes migration) — in particular the atomic
/// single-use consume, whose correctness is a property of Postgres row locking,
/// not of C#.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OAuthAuthorizationCodeServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!pg.Available) return;
        await pg.ResetAsync();
        // The shared fixture's reset predates this table and does not truncate it;
        // clear it here so each test in this suite starts from an empty code table.
        await using var db = pg.NewContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE oauth_authorization_codes");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string ClientId = "https://app.example.com/oauth/client-metadata.json";
    private const string RedirectUri = "https://app.example.com/callback";
    private static readonly OAuthServerConfig Server = new("https://mcp.example.com");

    // A real S256 verifier/challenge pair (RFC 7636 §4.2/§4.6).
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Happy path ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Mint_then_consume_with_the_right_verifier_exchanges_once()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var codes = new OAuthAuthorizationCodeService(db, clock);

        var issued = await codes.MintAsync(ClientId, RedirectUri, Challenge(Verifier),
            resource: Server.ResourceId, scope: "landbridge");

        Assert.StartsWith("lbr_c_", issued.Code);
        Assert.Equal(clock.GetUtcNow() + OAuthAuthorizationCodeService.CodeTtl, issued.ExpiresAt);

        // Only the hash is at rest (§5): the plaintext never lands in the row.
        var row = await db.OAuthAuthorizationCodes.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(issued.Code, row.CodeHash);

        var result = await codes.ConsumeAsync(
            issued.Code, ClientId, RedirectUri, Verifier, Server.ResourceId, Server);
        var ok = Assert.IsType<CodeExchangeResult.Exchanged>(result);
        Assert.Equal("landbridge", ok.Scope);
    }

    [SkippableFact]
    public async Task A_code_bound_to_no_resource_may_be_redeemed_without_one()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var codes = new OAuthAuthorizationCodeService(db, new FakeTimeProvider());

        var issued = await codes.MintAsync(ClientId, RedirectUri, Challenge(Verifier), resource: null, scope: null);
        var result = await codes.ConsumeAsync(issued.Code, ClientId, RedirectUri, Verifier, resource: null, Server);

        Assert.IsType<CodeExchangeResult.Exchanged>(result);
    }

    // ── Single-use ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_replayed_code_is_invalid_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var codes = new OAuthAuthorizationCodeService(db, new FakeTimeProvider());

        var issued = await codes.MintAsync(ClientId, RedirectUri, Challenge(Verifier), Server.ResourceId, null);
        Assert.IsType<CodeExchangeResult.Exchanged>(
            await codes.ConsumeAsync(issued.Code, ClientId, RedirectUri, Verifier, Server.ResourceId, Server));

        // Second redemption of the same code is refused (RFC 6749 §4.1.2: single-use).
        var replay = await codes.ConsumeAsync(issued.Code, ClientId, RedirectUri, Verifier, Server.ResourceId, Server);
        Assert.IsType<CodeExchangeResult.InvalidGrant>(replay);
    }

    [SkippableFact]
    public async Task Concurrent_double_exchange_lets_exactly_one_win()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var seed = pg.NewContext();
        var issued = await new OAuthAuthorizationCodeService(seed, new FakeTimeProvider())
            .MintAsync(ClientId, RedirectUri, Challenge(Verifier), Server.ResourceId, null);

        // Two independent contexts/connections race to consume the one code. The
        // atomic conditional update (Postgres row lock) is the gate, so exactly one
        // sees Exchanged and the other invalid_grant — never both.
        await using var db1 = pg.NewContext();
        await using var db2 = pg.NewContext();
        var c1 = new OAuthAuthorizationCodeService(db1, new FakeTimeProvider());
        var c2 = new OAuthAuthorizationCodeService(db2, new FakeTimeProvider());

        var t1 = c1.ConsumeAsync(issued.Code, ClientId, RedirectUri, Verifier, Server.ResourceId, Server);
        var t2 = c2.ConsumeAsync(issued.Code, ClientId, RedirectUri, Verifier, Server.ResourceId, Server);
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, results.Count(r => r is CodeExchangeResult.Exchanged));
        Assert.Equal(1, results.Count(r => r is CodeExchangeResult.InvalidGrant));
    }

    // ── Binding checks (all invalid_grant, no distinguishing detail) ──────────

    [SkippableFact]
    public async Task An_expired_code_is_invalid_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var codes = new OAuthAuthorizationCodeService(db, clock);

        var issued = await codes.MintAsync(ClientId, RedirectUri, Challenge(Verifier), Server.ResourceId, null);
        clock.Advance(OAuthAuthorizationCodeService.CodeTtl + TimeSpan.FromSeconds(1));

        Assert.IsType<CodeExchangeResult.InvalidGrant>(
            await codes.ConsumeAsync(issued.Code, ClientId, RedirectUri, Verifier, Server.ResourceId, Server));
    }

    [SkippableFact]
    public async Task A_wrong_verifier_is_invalid_grant_and_does_not_burn_the_code()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var codes = new OAuthAuthorizationCodeService(db, new FakeTimeProvider());

        var issued = await codes.MintAsync(ClientId, RedirectUri, Challenge(Verifier), Server.ResourceId, null);

        Assert.IsType<CodeExchangeResult.InvalidGrant>(
            await codes.ConsumeAsync(issued.Code, ClientId, RedirectUri, "wrong-verifier", Server.ResourceId, Server));

        // A failed PKCE check must not consume the code — the real client can still
        // redeem it. (The exchange fails only the attacker who lacks the verifier.)
        Assert.IsType<CodeExchangeResult.Exchanged>(
            await codes.ConsumeAsync(issued.Code, ClientId, RedirectUri, Verifier, Server.ResourceId, Server));
    }

    [SkippableFact]
    public async Task A_wrong_redirect_uri_is_invalid_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var codes = new OAuthAuthorizationCodeService(db, new FakeTimeProvider());

        var issued = await codes.MintAsync(ClientId, RedirectUri, Challenge(Verifier), Server.ResourceId, null);

        Assert.IsType<CodeExchangeResult.InvalidGrant>(
            await codes.ConsumeAsync(issued.Code, ClientId, "https://evil.example/callback", Verifier, Server.ResourceId, Server));
    }

    [SkippableFact]
    public async Task A_wrong_client_id_is_invalid_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var codes = new OAuthAuthorizationCodeService(db, new FakeTimeProvider());

        var issued = await codes.MintAsync(ClientId, RedirectUri, Challenge(Verifier), Server.ResourceId, null);

        Assert.IsType<CodeExchangeResult.InvalidGrant>(
            await codes.ConsumeAsync(issued.Code, "https://other.example/client.json", RedirectUri, Verifier, Server.ResourceId, Server));
    }

    [SkippableFact]
    public async Task A_resource_that_does_not_match_the_bound_audience_is_invalid_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var codes = new OAuthAuthorizationCodeService(db, new FakeTimeProvider());

        var issued = await codes.MintAsync(ClientId, RedirectUri, Challenge(Verifier), Server.ResourceId, null);

        // RFC 8707: the token request's resource must equal the bound audience.
        Assert.IsType<CodeExchangeResult.InvalidGrant>(
            await codes.ConsumeAsync(issued.Code, ClientId, RedirectUri, Verifier, "https://other-server.example", Server));
    }

    [SkippableFact]
    public async Task An_unknown_code_is_invalid_grant()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var codes = new OAuthAuthorizationCodeService(db, new FakeTimeProvider());

        Assert.IsType<CodeExchangeResult.InvalidGrant>(
            await codes.ConsumeAsync("lbr_c_deadbeef", ClientId, RedirectUri, Verifier, Server.ResourceId, Server));
    }
}
