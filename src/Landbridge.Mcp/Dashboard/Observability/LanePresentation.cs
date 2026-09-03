using Landbridge.ControlPlane;
using Landbridge.Core;

namespace Landbridge.Mcp.Dashboard.Observability;

/// <summary>
/// Presentation mapping from occupancy + the live envelope onto the 1a lane-board
/// vocabulary (color, label, pulse). Not a second state machine.
/// </summary>
public sealed record LaneMeta(string ColorVar, string Label, string Envelope, bool Live, double DotOpacity)
{
    /// <summary>
    /// Board-only: occupancy can still be Working while the runner socket is gone
    /// or the last heartbeat is older than one default cadence.
    /// </summary>
    public static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(15);

    public static bool ConnectionLost(ObservabilityLane lane, DateTimeOffset now)
    {
        if (lane.State is not (SessionState.Working or SessionState.BlockedOnInput))
            return false;
        if (lane.LastHeartbeat is not { } at)
            return true;
        return now - at > HeartbeatStaleAfter;
    }

    public static LaneMeta Of(ObservabilityLane lane, DateTimeOffset now)
    {
        if (ConnectionLost(lane, now))
            return new("--state-wait", "lost connection", EnvelopeName(lane.MessageState), false, 1.0);

        if (lane.State == SessionState.BlockedOnInput
            || (lane.State == SessionState.Working && lane.InputKind == InputRequestKind.Permission && lane.BlockedAt is not null))
            return new("--state-wait", "permission", EnvelopeName(lane.MessageState), true, 1.0);

        if (lane.State == SessionState.Working && lane.BlockedAt is not null && lane.InputKind != InputRequestKind.Permission)
            return new("--state-wait", "blocked", EnvelopeName(lane.MessageState), false, 1.0);

        return lane.State switch
        {
            SessionState.Working => new("--state-live", "working", EnvelopeName(lane.MessageState), true, 1.0),
            SessionState.Submitted => new("--color-neutral-700", "submitted", EnvelopeName(lane.MessageState), false, 1.0),
            SessionState.Failed => new("--state-error", "failed", EnvelopeName(lane.MessageState), true, 1.0),
            SessionState.Parked => new("--state-wait", "parked", EnvelopeName(lane.MessageState), false, 0.5),
            SessionState.Completed => new("--color-neutral-500", "completed", EnvelopeName(lane.MessageState), false, 0.45),
            SessionState.Canceled => new("--color-neutral-500", "canceled", EnvelopeName(lane.MessageState), false, 0.45),
            SessionState.Rejected => new("--color-neutral-500", "rejected", EnvelopeName(lane.MessageState), false, 0.45),
            _ => new("--color-neutral-700", lane.State.ToString().ToLowerInvariant(), EnvelopeName(lane.MessageState), false, 1.0),
        };
    }

    public static string EnvelopeName(MessageState state) => state switch
    {
        MessageState.Idle => "idle",
        MessageState.AwaitingLead => "awaiting_lead",
        MessageState.AwaitingPermission => "awaiting_permission",
        MessageState.AwaitingReport => "awaiting_report",
        MessageState.AwaitingPull => "awaiting_pull",
        _ => state.ToString().ToLowerInvariant(),
    };
}

public static class LaneNow
{
    public static string Text(ObservabilityLane lane, DateTimeOffset now)
    {
        if (lane.State == SessionState.Submitted)
            return "waiting for dispatch";
        if (lane.State == SessionState.Failed)
            return lane.LastRequeueReason is { } r ? r.ToString() : "infrastructure gave up";
        if (lane.State == SessionState.Parked)
            return string.IsNullOrWhiteSpace(lane.Question) ? "parked" : Truncate(lane.Question, 72)!;
        if (lane.State == SessionState.Completed)
            return string.IsNullOrWhiteSpace(lane.WorkerReport) ? "completed" : Truncate(lane.WorkerReport, 72)!;
        if (lane.State is SessionState.Canceled or SessionState.Rejected)
            return lane.State.ToString().ToLowerInvariant();
        if (LaneMeta.ConnectionLost(lane, now))
            return $"last heartbeat {DashboardFormat.Age(lane.LastHeartbeat, now)}";
        if (lane.InputKind == InputRequestKind.Permission)
            return string.IsNullOrWhiteSpace(lane.PermissionTool)
                ? Truncate(lane.Question, 72) ?? "permission"
                : $"permission: {lane.PermissionTool}";
        if (lane.BlockedAt is not null && !string.IsNullOrWhiteSpace(lane.Question))
            return Truncate(lane.Question, 72)!;
        if (lane.MessageState != MessageState.Idle)
            return LaneMeta.EnvelopeName(lane.MessageState);
        if (lane.LastProgress is { } progress)
            return $"progress {DashboardFormat.Age(progress, now)}";
        return LaneMeta.EnvelopeName(lane.MessageState);
    }

    private static string? Truncate(string? s, int n)
    {
        if (string.IsNullOrWhiteSpace(s))
            return s;
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}

public static class LaneUsage
{
    public static (double In, double Out, double Cache) Split(ObservabilityLane lane)
    {
        var total = lane.InputTokens + lane.OutputTokens + lane.CacheReadTokens + lane.CacheWriteTokens;
        if (total <= 0)
            return (0, 0, 0);
        var cache = lane.CacheReadTokens + lane.CacheWriteTokens;
        return (
            100.0 * lane.InputTokens / total,
            100.0 * lane.OutputTokens / total,
            100.0 * cache / total);
    }
}
