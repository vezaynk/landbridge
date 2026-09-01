using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;

namespace Landbridge.Mcp.Tests;

internal static class LeadFactory
{
    public static async Task<Principal.Lead> SeedAsync(PostgresFixture pg, TeamId team, TimeProvider clock)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, clock);
        var human = await tokens.IssueHumanSessionAsync();
        var claim = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, team);
        return (Principal.Lead)(await tokens.ValidateAsync(claim.Token.Token))!;
    }

    public static string Id(TeamId team) => team.Value.ToString("D");
}
