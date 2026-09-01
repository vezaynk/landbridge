using System.Security.Claims;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Auth;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The claims bridge is the one piece of the MCP auth path with logic of its
/// own (the tools are thin adapters over already-tested store transitions), so
/// it carries the round-trip guarantees: a validated principal survives the
/// trip through HttpContext.User and reaches a tool as the same typed actor.
/// </summary>
public class LandbridgeClaimsTests
{
    [Fact]
    public void Worker_principal_round_trips_with_all_claims()
    {
        var caller = new WorkerCaller(TeamId.New(), SessionId.New(), WorkerInstanceId.New());

        var restored = LandbridgeClaims.ToPrincipal(
            LandbridgeClaims.ToClaimsPrincipal(new Principal.Worker(caller)));

        var worker = Assert.IsType<Principal.Worker>(restored);
        Assert.Equal(caller, worker.Caller);
    }

    [Fact]
    public void Worker_principal_is_reachable_as_a_worker_caller()
    {
        var caller = new WorkerCaller(TeamId.New(), SessionId.New(), WorkerInstanceId.New());
        var user = LandbridgeClaims.ToClaimsPrincipal(new Principal.Worker(caller));

        Assert.Equal(caller, LandbridgeClaims.AsWorker(user));
    }

    [Fact]
    public void Machine_principal_round_trips()
    {
        var machineId = Guid.NewGuid();

        var restored = LandbridgeClaims.ToPrincipal(
            LandbridgeClaims.ToClaimsPrincipal(new Principal.Machine(machineId)));

        Assert.Equal(machineId, Assert.IsType<Principal.Machine>(restored).MachineId);
    }

    [Fact]
    public void A_non_worker_principal_is_not_a_worker_caller()
    {
        var machine = LandbridgeClaims.ToClaimsPrincipal(new Principal.Machine(Guid.NewGuid()));
        var human = LandbridgeClaims.ToClaimsPrincipal(new Principal.Human(Guid.NewGuid()));

        Assert.Null(LandbridgeClaims.AsWorker(machine));
        Assert.Null(LandbridgeClaims.AsWorker(human));
    }

    [Fact]
    public void Human_principal_round_trips_and_is_reachable_as_a_human_session()
    {
        var humanId = Guid.NewGuid();
        var user = LandbridgeClaims.ToClaimsPrincipal(new Principal.Human(humanId));

        Assert.Equal(humanId, Assert.IsType<Principal.Human>(LandbridgeClaims.ToPrincipal(user)).HumanId);
        Assert.NotNull(LandbridgeClaims.AsHuman(user));
        Assert.Null(LandbridgeClaims.AsLead(user));
    }

    [Fact]
    public void Lead_principal_round_trips_and_is_reachable_as_a_lead_claim()
    {
        var credentialId = Guid.NewGuid();
        var user = LandbridgeClaims.ToClaimsPrincipal(new Principal.Lead(credentialId));

        Assert.Equal(credentialId, Assert.IsType<Principal.Lead>(LandbridgeClaims.ToPrincipal(user)).CredentialId);
        Assert.Equal(credentialId, LandbridgeClaims.AsLead(user)!.CredentialId);
        Assert.Null(LandbridgeClaims.AsHuman(user));
        Assert.Null(LandbridgeClaims.AsEvictedLead(user));
        // No human attribution on this claim, so no binding can be owned (§8.3).
        Assert.Null(LandbridgeClaims.AsLeadPrincipal(user)!.HumanId);
    }

    [Fact]
    public void Lead_principal_carries_the_claiming_human_when_the_credential_attributes_one()
    {
        var credentialId = Guid.NewGuid();
        var human = Guid.NewGuid();
        var user = LandbridgeClaims.ToClaimsPrincipal(new Principal.Lead(credentialId, human));

        // The human rides the lead claim so the §8.3 lead↔machine binding can key on
        // the person; the engine actor stays Team-only.
        var lead = Assert.IsType<Principal.Lead>(LandbridgeClaims.ToPrincipal(user));
        Assert.Equal(credentialId, lead.CredentialId);
        Assert.Equal(human, lead.HumanId);
        Assert.Equal(human, LandbridgeClaims.AsLeadPrincipal(user)!.HumanId);
        Assert.Equal(credentialId, LandbridgeClaims.AsLead(user)!.CredentialId);
        // Still not a human session — a lead claim is its own credential class (§5).
        Assert.Null(LandbridgeClaims.AsHuman(user));
    }

    [Fact]
    public void Evicted_lead_principal_round_trips_with_its_attribution()
    {
        var team = TeamId.New();
        var by = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var user = LandbridgeClaims.ToClaimsPrincipal(new Principal.EvictedLead(team, by, at));

        var evicted = Assert.IsType<Principal.EvictedLead>(LandbridgeClaims.ToPrincipal(user));
        Assert.Equal(team, evicted.Team);
        Assert.Equal(by, evicted.EvictedByHuman);
        Assert.Equal(at, evicted.EvictedAt);

        // An evicted claim is not a live lead claim (§4).
        Assert.Null(LandbridgeClaims.AsLead(user));
        Assert.NotNull(LandbridgeClaims.AsEvictedLead(user));
    }

    [Fact]
    public void An_unauthenticated_user_maps_to_no_principal()
    {
        Assert.Null(LandbridgeClaims.ToPrincipal(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(LandbridgeClaims.AsWorker(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(LandbridgeClaims.AsLead(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(LandbridgeClaims.AsHuman(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
