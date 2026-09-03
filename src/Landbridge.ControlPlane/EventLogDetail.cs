using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// What the §12 event log stores on a transition. Derived <see cref="SessionState"/>
/// often does not move after occupancy+message (<see cref="DeliverReport"/>,
/// <see cref="PullReceipt"/>, <see cref="ObserveOccupancy"/>, …); those
/// used to persist an empty effect list. This is the occupancy/message/report
/// delta, then the effect type names, then a command label when neither moved.
/// </summary>
public static class EventLogDetail
{
    public static string Describe(
        SessionCommand command,
        SessionRecord before,
        SessionRecord after,
        IReadOnlyList<Effect> effects)
    {
        if (command is LeadMessage { Text: { Length: > 0 } text })
            return text;
        if (command is ContinueSession { Answer: { Length: > 0 } answer })
            return answer;
        if (command is WakeParked { Answer: { Length: > 0 } wake })
            return wake;
        if (command is ObserveOccupancy observe)
        {
            var observed = DescribeMoved(before, after);
            return string.IsNullOrEmpty(observed) ? $"observed {observe.Observed}" : observed;
        }

        var moved = DescribeMoved(before, after);
        if (!string.IsNullOrEmpty(moved))
            return moved;

        var effect = DescribeEffects(effects);
        if (!string.IsNullOrEmpty(effect))
            return effect;

        return command switch
        {
            DeliverReport => "report delivered",
            PullReceipt => "pull received",
            ReportResult => "unread report",
            LeadMessage => "Lead follow-up",
            ContinueSession => "answered in place",
            WakeParked => "woken",
            StopSession or VerdictAccept or VerdictFail or Cancel => "session stopped",
            Park or StopPreserveAndPark or WaitTtlExpired => "deactivated",
            _ => "",
        };
    }

    internal static string DescribeMoved(SessionRecord before, SessionRecord after)
    {
        var parts = new List<string>();
        if (before.OccupancyDesired != after.OccupancyDesired)
            parts.Add($"desired {before.OccupancyDesired} to {after.OccupancyDesired}");
        if (before.OccupancyObserved != after.OccupancyObserved)
            parts.Add($"observed {before.OccupancyObserved} to {after.OccupancyObserved}");
        if (before.MessageState != after.MessageState)
            parts.Add($"{before.MessageState} to {after.MessageState}");
        if (before.ReportUnread != after.ReportUnread)
            parts.Add(after.ReportUnread ? "unread report" : "unread to read");
        if (before.Health != after.Health)
            parts.Add($"health {before.Health} to {after.Health}");
        if (before.Hidden != after.Hidden)
            parts.Add(after.Hidden ? "hidden" : "unhidden");
        if (before.PendingSpawn != after.PendingSpawn)
            parts.Add($"spawn {Spawn(before.PendingSpawn)} to {Spawn(after.PendingSpawn)}");
        return string.Join("; ", parts);
    }

    private static string Spawn(PendingSpawn? value) => value is { } spawn ? spawn.ToString() : "none";

    private static string DescribeEffects(IReadOnlyList<Effect> effects) =>
        effects.Count == 0 ? "" : string.Join(",", effects.Select(e => e.GetType().Name));
}
