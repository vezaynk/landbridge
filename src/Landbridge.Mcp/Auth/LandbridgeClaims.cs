using System.Security.Claims;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;

namespace Landbridge.Mcp.Auth;

/// <summary>
/// Bridges the token service's typed <see cref="Principal"/> and the ASP.NET
/// <see cref="ClaimsPrincipal"/> the auth pipeline carries. The typed principal
/// stays the source of truth; claims are just its wire form on
/// <c>HttpContext.User</c>, and tools reconstruct the principal from them so
/// authority reaches the engine as a real <see cref="Landbridge.Core.Actor"/>.
/// </summary>
public static class LandbridgeClaims
{
    public const string AuthenticationType = "landbridge";

    private const string Kind = "landbridge:kind";
    private const string Team = "landbridge:team";
    private const string Task = "landbridge:task";
    private const string Instance = "landbridge:instance";
    private const string Machine = "landbridge:machine";
    private const string Human = "landbridge:human";
    private const string EvictedBy = "landbridge:evicted_by";
    private const string EvictedAt = "landbridge:evicted_at";

    public static ClaimsPrincipal ToClaimsPrincipal(Principal principal)
    {
        var claims = new List<Claim>();
        switch (principal)
        {
            case Principal.Worker w:
                claims.Add(new Claim(Kind, nameof(Principal.Worker)));
                claims.Add(new Claim(Team, w.Caller.Team.Value.ToString()));
                claims.Add(new Claim(Task, w.Caller.Session.Value.ToString()));
                claims.Add(new Claim(Instance, w.Caller.Instance.Value.ToString()));
                break;
            case Principal.Machine m:
                claims.Add(new Claim(Kind, nameof(Principal.Machine)));
                claims.Add(new Claim(Machine, m.MachineId.ToString()));
                break;
            case Principal.Human h:
                claims.Add(new Claim(Kind, nameof(Principal.Human)));
                claims.Add(new Claim(Human, h.HumanId.ToString()));
                break;
            case Principal.Lead l:
                claims.Add(new Claim(Kind, nameof(Principal.Lead)));
                claims.Add(new Claim(Team, l.Team.Value.ToString()));
                // The claiming human, when the credential row attributed one (§4).
                // Absent on a synthesized claim, which is why the round trip below
                // reads it optionally rather than asserting it.
                if (l.HumanId is { } leadHuman)
                    claims.Add(new Claim(Human, leadHuman.ToString()));
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
                new SessionId(Guid.Parse(user.FindFirst(Task)!.Value)),
                new WorkerInstanceId(Guid.Parse(user.FindFirst(Instance)!.Value)))),
            nameof(Principal.Machine) => new Principal.Machine(Guid.Parse(user.FindFirst(Machine)!.Value)),
            nameof(Principal.Human) => new Principal.Human(Guid.Parse(user.FindFirst(Human)!.Value)),
            nameof(Principal.Lead) => new Principal.Lead(
                new TeamId(Guid.Parse(user.FindFirst(Team)!.Value)),
                user.FindFirst(Human) is { } h ? Guid.Parse(h.Value) : null),
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

    /// <summary>
    /// The whole live-lead principal — Team <em>and</em> the claiming human (§4) —
    /// if this principal is a live lead, else null. <see cref="AsLead"/> is the
    /// engine-actor view for task transitions; this is for the lead-scoped facts
    /// the engine has no opinion about, namely the lead↔machine binding (§8.3).
    /// </summary>
    public static Principal.Lead? AsLeadPrincipal(ClaimsPrincipal user) =>
        ToPrincipal(user) as Principal.Lead;

    /// <summary>The eviction facts if this principal is an evicted lead, else null (§4).</summary>
    public static Principal.EvictedLead? AsEvictedLead(ClaimsPrincipal user) =>
        ToPrincipal(user) as Principal.EvictedLead;
}
