using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The human → Lead credential path of §4/§5: a human session claims a Team,
/// one Lead per Team is a conditional claim, and takeover evicts the incumbent
/// with an explicit, logged reason.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LeadClaimTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();

    [SkippableFact]
    public async Task Human_session_issues_and_validates()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        var human = await tokens.IssueHumanSessionAsync();
        Assert.StartsWith("dkt_h_", human.Token);

        var principal = Assert.IsType<Principal.Human>(await tokens.ValidateAsync(human.Token));
        Assert.Equal(human.CredentialId, principal.HumanId);
    }

    [SkippableFact]
    public async Task Expired_human_session_validates_to_null()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var tokens = new TokenService(db, clock);

        var human = await tokens.IssueHumanSessionAsync();
        clock.Advance(TokenService.HumanSessionTtl + TimeSpan.FromMinutes(1));

        Assert.Null(await tokens.ValidateAsync(human.Token));
    }

    [SkippableFact]
    public async Task Claiming_a_leadless_team_succeeds_silently_and_validates_as_lead()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        var human = await tokens.IssueHumanSessionAsync();
        var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, Team));
        Assert.StartsWith("dkt_l_", claim.Token.Token);

        var lead = Assert.IsType<Principal.Lead>(await tokens.ValidateAsync(claim.Token.Token));
        Assert.Equal(Team, lead.Team);

        // A silent claim is still logged as a claim event (§12).
        var ev = await db.LeadEvents.AsNoTracking().SingleAsync();
        Assert.Equal(LeadEventKind.Claimed, ev.Kind);
        Assert.Equal(human.CredentialId, ev.HumanId);
    }

    [SkippableFact]
    public async Task A_lead_claim_requires_a_live_human_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        // A worker token is not a human session; the derivation is asymmetric (§5).
        var worker = await tokens.MintWorkerTokenAsync(Team, TaskId.New(), WorkerInstanceId.New());
        Assert.IsType<LeadClaimResult.NoHumanSession>(await tokens.ClaimLeadAsync(worker.Token, Team));
    }

    [SkippableFact]
    public async Task Second_claimant_is_refused_without_takeover_and_shown_the_holder()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        var alice = await tokens.IssueHumanSessionAsync();
        var bob = await tokens.IssueHumanSessionAsync();

        await tokens.ClaimLeadAsync(alice.Token, Team);
        var refused = Assert.IsType<LeadClaimResult.Refused>(await tokens.ClaimLeadAsync(bob.Token, Team));

        // §4: an actively-held Team shows who holds it before a takeover decision.
        Assert.Equal(alice.CredentialId, refused.HeldByHuman);
    }

    [SkippableFact]
    public async Task Takeover_evicts_the_incumbent_who_learns_who_and_when_and_the_event_is_logged()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var tokens = new TokenService(db, clock);

        var alice = await tokens.IssueHumanSessionAsync();
        var bob = await tokens.IssueHumanSessionAsync();

        var aliceClaim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(alice.Token, Team));
        Assert.IsType<Principal.Lead>(await tokens.ValidateAsync(aliceClaim.Token.Token));

        clock.Advance(TimeSpan.FromMinutes(5));
        var evictionTime = clock.GetUtcNow();
        var bobClaim = Assert.IsType<LeadClaimResult.Claimed>(
            await tokens.ClaimLeadAsync(bob.Token, Team, takeover: true));

        // The successor is a live lead.
        Assert.IsType<Principal.Lead>(await tokens.ValidateAsync(bobClaim.Token.Token));

        // §4: the evicted session's next call is not a bare null — it resolves to
        // an explicit reason naming who evicted it and when.
        var evicted = Assert.IsType<Principal.EvictedLead>(await tokens.ValidateAsync(aliceClaim.Token.Token));
        Assert.Equal(Team, evicted.Team);
        Assert.Equal(bob.CredentialId, evicted.EvictedByHuman);
        Assert.Equal(evictionTime, evicted.EvictedAt);

        // §4/§12: every takeover is a logged event, with both parties named.
        var takeover = await db.LeadEvents.AsNoTracking().SingleAsync(e => e.Kind == LeadEventKind.TakenOver);
        Assert.Equal(bob.CredentialId, takeover.HumanId);
        Assert.Equal(alice.CredentialId, takeover.PriorHumanId);
    }

    [SkippableFact]
    public async Task Release_makes_the_team_claimable_again_and_the_token_stops_authenticating()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, new FakeTimeProvider());

        var alice = await tokens.IssueHumanSessionAsync();
        var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(alice.Token, Team));

        Assert.True(await tokens.ReleaseLeadAsync(claim.Token.Token));

        // A voluntary release is a clean null, not an eviction reason.
        Assert.Null(await tokens.ValidateAsync(claim.Token.Token));

        // The Team is claimable again — a fresh human claims it silently.
        var bob = await tokens.IssueHumanSessionAsync();
        Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(bob.Token, Team));
    }
}
