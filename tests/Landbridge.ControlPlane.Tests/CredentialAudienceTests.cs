using Landbridge.ControlPlane.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// RFC 8707 audience binding on a human session (spec §5). The authorization server
/// already refuses a <c>resource</c> that is not its own, so the binding recorded here
/// changes nothing for a deployment with one resource id. It is what lets a resource
/// server refuse a token minted for a <em>different</em> one — which is the whole of
/// what "one instance, one audience" was standing in for while both ends were one
/// process, and what has to be real before the MCP hosts become separate edges.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CredentialAudienceTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string ThisResource = "https://mcp.example.com";
    private const string OtherResource = "https://other.example.com";

    [SkippableFact]
    public async Task A_session_minted_for_this_resource_authenticates_here()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = Tokens(db, ThisResource);

        var session = await tokens.IssueHumanSessionAsync(ThisResource);

        Assert.IsType<Principal.Human>(await tokens.ValidateAsync(session.Token));
    }

    /// <summary>
    /// The point of the column. The token is live, unrevoked and unexpired — it is simply
    /// not a credential for this server, and a resource server that accepted it would be
    /// honouring an audience it was never granted.
    /// </summary>
    [SkippableFact]
    public async Task A_session_minted_for_another_resource_does_not_authenticate_here()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        var elsewhere = await Tokens(db, OtherResource).IssueHumanSessionAsync(OtherResource);

        Assert.Null(await Tokens(db, ThisResource).ValidateAsync(elsewhere.Token));
        // ...and still works where it was minted, so this is a scoping rule, not a revoke.
        Assert.IsType<Principal.Human>(await Tokens(db, OtherResource).ValidateAsync(elsewhere.Token));
    }

    /// <summary>
    /// An unbound session is every credential minted inside the plane, and every one
    /// minted before the column existed. Refusing those would revoke the fleet on deploy.
    /// </summary>
    [SkippableFact]
    public async Task An_unbound_session_authenticates_anywhere()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        var unbound = await Tokens(db, ThisResource).IssueHumanSessionAsync();

        Assert.IsType<Principal.Human>(await Tokens(db, ThisResource).ValidateAsync(unbound.Token));
        Assert.IsType<Principal.Human>(await Tokens(db, OtherResource).ValidateAsync(unbound.Token));
    }

    /// <summary>
    /// A host that was never told its resource id — the Hub, a test host — has no audience
    /// to compare against, so it accepts as it did before. Making that a refusal would
    /// take down every host that does not wire OAuth.
    /// </summary>
    [SkippableFact]
    public async Task A_host_with_no_resource_id_of_its_own_accepts_a_bound_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        var bound = await Tokens(db, OtherResource).IssueHumanSessionAsync(OtherResource);

        Assert.IsType<Principal.Human>(
            await new TokenService(db, new FakeTimeProvider()).ValidateAsync(bound.Token));
    }

    /// <summary>Same normalisation the resource comparison uses, so a trailing slash
    /// or a shouted host does not become a different audience.</summary>
    [SkippableTheory]
    [InlineData("https://mcp.example.com/")]
    [InlineData("HTTPS://MCP.EXAMPLE.COM")]
    public async Task The_recorded_audience_is_compared_canonically(string minted)
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = Tokens(db, ThisResource);

        var session = await tokens.IssueHumanSessionAsync(minted);

        Assert.IsType<Principal.Human>(await tokens.ValidateAsync(session.Token));
    }

    private static TokenService Tokens(LandbridgeDbContext db, string resourceId) =>
        new(db, new FakeTimeProvider(), OAuthServerConfig.FromPublicMcpUrl(resourceId));
}
