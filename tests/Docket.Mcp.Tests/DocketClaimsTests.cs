using System.Security.Claims;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Docket.Mcp.Auth;

namespace Docket.Mcp.Tests;

/// <summary>
/// The claims bridge is the one piece of the MCP auth path with logic of its
/// own (the tools are thin adapters over already-tested store transitions), so
/// it carries the round-trip guarantees: a validated principal survives the
/// trip through HttpContext.User and reaches a tool as the same typed actor.
/// </summary>
public class DocketClaimsTests
{
    [Fact]
    public void Worker_principal_round_trips_with_all_claims()
    {
        var caller = new WorkerCaller(TeamId.New(), TaskId.New(), WorkerInstanceId.New());

        var restored = DocketClaims.ToPrincipal(
            DocketClaims.ToClaimsPrincipal(new Principal.Worker(caller)));

        var worker = Assert.IsType<Principal.Worker>(restored);
        Assert.Equal(caller, worker.Caller);
    }

    [Fact]
    public void Worker_principal_is_reachable_as_a_worker_caller()
    {
        var caller = new WorkerCaller(TeamId.New(), TaskId.New(), WorkerInstanceId.New());
        var user = DocketClaims.ToClaimsPrincipal(new Principal.Worker(caller));

        Assert.Equal(caller, DocketClaims.AsWorker(user));
    }

    [Fact]
    public void Machine_principal_round_trips()
    {
        var machineId = Guid.NewGuid();

        var restored = DocketClaims.ToPrincipal(
            DocketClaims.ToClaimsPrincipal(new Principal.Machine(machineId)));

        Assert.Equal(machineId, Assert.IsType<Principal.Machine>(restored).MachineId);
    }

    [Fact]
    public void Verifier_principal_round_trips()
    {
        var restored = DocketClaims.ToPrincipal(
            DocketClaims.ToClaimsPrincipal(new Principal.Verifier()));

        Assert.IsType<Principal.Verifier>(restored);
    }

    [Fact]
    public void A_non_worker_principal_is_not_a_worker_caller()
    {
        var machine = DocketClaims.ToClaimsPrincipal(new Principal.Machine(Guid.NewGuid()));
        var verifier = DocketClaims.ToClaimsPrincipal(new Principal.Verifier());

        Assert.Null(DocketClaims.AsWorker(machine));
        Assert.Null(DocketClaims.AsWorker(verifier));
    }

    [Fact]
    public void Human_principal_round_trips_and_is_reachable_as_a_human_session()
    {
        var humanId = Guid.NewGuid();
        var user = DocketClaims.ToClaimsPrincipal(new Principal.Human(humanId));

        Assert.Equal(humanId, Assert.IsType<Principal.Human>(DocketClaims.ToPrincipal(user)).HumanId);
        Assert.NotNull(DocketClaims.AsHuman(user));
        Assert.Null(DocketClaims.AsLead(user));
    }

    [Fact]
    public void Lead_principal_round_trips_and_is_reachable_as_a_lead_claim()
    {
        var team = TeamId.New();
        var user = DocketClaims.ToClaimsPrincipal(new Principal.Lead(team));

        Assert.Equal(team, Assert.IsType<Principal.Lead>(DocketClaims.ToPrincipal(user)).Team);
        Assert.Equal(new LeadClaim(team), DocketClaims.AsLead(user));
        Assert.Null(DocketClaims.AsHuman(user));
        Assert.Null(DocketClaims.AsEvictedLead(user));
    }

    [Fact]
    public void Evicted_lead_principal_round_trips_with_its_attribution()
    {
        var team = TeamId.New();
        var by = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var user = DocketClaims.ToClaimsPrincipal(new Principal.EvictedLead(team, by, at));

        var evicted = Assert.IsType<Principal.EvictedLead>(DocketClaims.ToPrincipal(user));
        Assert.Equal(team, evicted.Team);
        Assert.Equal(by, evicted.EvictedByHuman);
        Assert.Equal(at, evicted.EvictedAt);

        // An evicted claim is not a live lead claim (§4).
        Assert.Null(DocketClaims.AsLead(user));
        Assert.NotNull(DocketClaims.AsEvictedLead(user));
    }

    [Fact]
    public void An_unauthenticated_user_maps_to_no_principal()
    {
        Assert.Null(DocketClaims.ToPrincipal(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(DocketClaims.AsWorker(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(DocketClaims.AsLead(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(DocketClaims.AsHuman(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
