namespace Landbridge.Core;

/// <summary>
/// Credential classes, spec §5. Authority is structural: what a caller may do
/// is a function of which class its token belongs to and the claims it
/// carries, never of an if-statement over free text.
/// </summary>
public abstract record Actor;

/// <summary>A human's own session credential.</summary>
public sealed record HumanSession : Actor;

/// <summary>A human session's lead claim, scoped to one Team.</summary>
public sealed record LeadClaim(TeamId Team) : Actor;

/// <summary>
/// A dispatched worker. Minted at dispatch, scoped to
/// {team, task, worker, instance}; a worker never authenticates (§5).
/// </summary>
public sealed record WorkerCaller(TeamId Team, SessionId Session, WorkerInstanceId Instance) : Actor;

/// <summary>The control plane acting on its own timers and dispatch decisions.</summary>
public sealed record ControlPlaneActor : Actor
{
    public static readonly ControlPlaneActor Instance = new();
}
