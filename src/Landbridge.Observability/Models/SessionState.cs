namespace Landbridge.Observability.Models;

/// <summary>
/// Occupancy state of a runner session, per the landbridge §12 vocabulary.
/// </summary>
public enum SessionState
{
    Working,
    Permission,
    Question,
    Submitted,
    Failed,
    Parked,
    Completed,
}

/// <summary>
/// Static presentation metadata for a <see cref="SessionState"/> — the color token,
/// the label shown on the lane, the MessageState envelope it maps to, and whether
/// the lane is considered "live" (pulses, keeps ticking).
/// </summary>
public sealed record StateMeta(string ColorVar, string Label, string Envelope, bool Live, double DotOpacity)
{
    public static readonly IReadOnlyDictionary<SessionState, StateMeta> All = new Dictionary<SessionState, StateMeta>
    {
        [SessionState.Working] = new("--state-live", "working", "running", true, 1.0),
        [SessionState.Permission] = new("--state-wait", "permission", "awaiting_permission", true, 1.0),
        [SessionState.Question] = new("--state-wait", "blocked", "awaiting_lead", false, 1.0),
        [SessionState.Submitted] = new("--color-neutral-700", "submitted", "unplaced", false, 1.0),
        [SessionState.Failed] = new("--state-error", "failed", "requeue", true, 1.0),
        [SessionState.Parked] = new("--state-wait", "parked", "awaiting_lead", false, 0.5),
        [SessionState.Completed] = new("--color-neutral-500", "completed", "closed", false, 0.45),
    };

    public static StateMeta Of(SessionState state) => All[state];
}
