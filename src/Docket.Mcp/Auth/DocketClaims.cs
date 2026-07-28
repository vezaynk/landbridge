using System.Security.Claims;
using Docket.ControlPlane.Auth;
using Docket.Core;

namespace Docket.Mcp.Auth;

/// <summary>
/// Bridges the token service's typed <see cref="Principal"/> and the ASP.NET
/// <see cref="ClaimsPrincipal"/> the auth pipeline carries. The typed principal
/// stays the source of truth; claims are just its wire form on
/// <c>HttpContext.User</c>, and tools reconstruct the principal from them so
/// authority reaches the engine as a real <see cref="Docket.Core.Actor"/>.
/// </summary>
public static class DocketClaims
{
    public const string AuthenticationType = "docket";

    private const string Kind = "docket:kind";
    private const string Team = "docket:team";
    private const string Task = "docket:task";
    private const string Instance = "docket:instance";
    private const string Machine = "docket:machine";
    private const string Human = "docket:human";
    private const string EvictedBy = "docket:evicted_by";
    private const string EvictedAt = "docket:evicted_at";

    public static ClaimsPrincipal ToClaimsPrincipal(Principal principal)
    {
        var claims = new List<Claim>();
        switch (principal)
        {
            case Principal.Worker w:
                claims.Add(new Claim(Kind, nameof(Principal.Worker)));
                claims.Add(new Claim(Team, w.Caller.Team.Value.ToString()));
                claims.Add(new Claim(Task, w.Caller.Task.Value.ToString()));
                claims.Add(new Claim(Instance, w.Caller.Instance.Value.ToString()));
                break;
            case Principal.Machine m:
                claims.Add(new Claim(Kind, nameof(Principal.Machine)));
                claims.Add(new Claim(Machine, m.MachineId.ToString()));
                break;
            case Principal.Verifier:
                claims.Add(new Claim(Kind, nameof(Principal.Verifier)));
                break;
            case Principal.Human h:
                claims.Add(new Claim(Kind, nameof(Principal.Human)));
                claims.Add(new Claim(Human, h.HumanId.ToString()));
                break;
            case Principal.Lead l:
                claims.Add(new Claim(Kind, nameof(Principal.Lead)));
                claims.Add(new Claim(Team, l.Team.Value.ToString()));
                break;
            case Principal.EvictedLead e:
                claims.Add(new Claim(Kind, nameof(Principal.EvictedLead)));
                claims.Add(new Claim(Team, e.Team.Value.ToString()));
                claims.Add(new Claim(EvictedBy, e.EvictedByHuman.ToString()));
                claims.Add(new Claim(EvictedAt, e.EvictedAt.ToString("O")));
                break;
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
    }

    /// <summary>Reconstructs the typed principal, or null if unauthenticated / unrecognized.</summary>
    public static Principal? ToPrincipal(ClaimsPrincipal user) =>
        user.FindFirst(Kind)?.Value switch
        {
            nameof(Principal.Worker) => new Principal.Worker(new WorkerCaller(
                new TeamId(Guid.Parse(user.FindFirst(Team)!.Value)),
                new TaskId(Guid.Parse(user.FindFirst(Task)!.Value)),
                new WorkerInstanceId(Guid.Parse(user.FindFirst(Instance)!.Value)))),
            nameof(Principal.Machine) => new Principal.Machine(Guid.Parse(user.FindFirst(Machine)!.Value)),
            nameof(Principal.Verifier) => new Principal.Verifier(),
            nameof(Principal.Human) => new Principal.Human(Guid.Parse(user.FindFirst(Human)!.Value)),
            nameof(Principal.Lead) => new Principal.Lead(new TeamId(Guid.Parse(user.FindFirst(Team)!.Value))),
            nameof(Principal.EvictedLead) => new Principal.EvictedLead(
                new TeamId(Guid.Parse(user.FindFirst(Team)!.Value)),
                Guid.Parse(user.FindFirst(EvictedBy)!.Value),
                DateTimeOffset.Parse(user.FindFirst(EvictedAt)!.Value,
                    null, System.Globalization.DateTimeStyles.RoundtripKind)),
            _ => null,
        };

    /// <summary>The worker caller if this principal is a worker, else null.</summary>
    public static WorkerCaller? AsWorker(ClaimsPrincipal user) =>
        ToPrincipal(user) is Principal.Worker w ? w.Caller : null;

    /// <summary>The engine's human-session actor if this principal is a human, else null.</summary>
    public static HumanSession? AsHuman(ClaimsPrincipal user) =>
        ToPrincipal(user) is Principal.Human ? new HumanSession() : null;

    /// <summary>The engine's lead claim if this principal is a live lead, else null.</summary>
    public static LeadClaim? AsLead(ClaimsPrincipal user) =>
        ToPrincipal(user) is Principal.Lead l ? new LeadClaim(l.Team) : null;

    /// <summary>The eviction facts if this principal is an evicted lead, else null (§4).</summary>
    public static Principal.EvictedLead? AsEvictedLead(ClaimsPrincipal user) =>
        ToPrincipal(user) as Principal.EvictedLead;
}
