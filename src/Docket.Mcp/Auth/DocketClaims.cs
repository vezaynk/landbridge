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
            _ => null,
        };

    /// <summary>The worker caller if this principal is a worker, else null.</summary>
    public static WorkerCaller? AsWorker(ClaimsPrincipal user) =>
        ToPrincipal(user) is Principal.Worker w ? w.Caller : null;
}
