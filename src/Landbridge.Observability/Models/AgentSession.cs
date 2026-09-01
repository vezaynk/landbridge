namespace Landbridge.Observability.Models;

/// <summary>
/// A live runner session ("lane"), with everything the lane board row and the
/// detail panel need. Mutated in place by the simulator on each tick.
/// </summary>
public sealed class AgentSession
{
    public required int Id { get; init; }
    public required string Ns { get; init; }
    public required string Profile { get; init; } // codex, claude, grok, opencode, goose
    public required string Machine { get; init; }
    public SessionState State { get; set; }
    public required string Now { get; set; }
    public int ElapsedSeconds { get; set; }
    public string Attempt { get; set; } = "a1";
    public int Unread { get; set; }
    public required int Seed { get; init; }
    public List<PortInfo> Ports { get; init; } = [];
    public List<TimelineMark> Events { get; init; } = [];
    public List<ChatMessage> Messages { get; init; } = [];
    public List<TranscriptLine> Transcript { get; init; } = [];

    // Usage
    public long Tokens { get; set; }
    public double? CostUsd { get; set; }
    public double UsageInPct { get; set; }
    public double UsageOutPct { get; set; }
    public double UsageCachePct { get; set; }
    public double? RateTokPerMin { get; set; }
    public int ToolCalls { get; set; }
    public int MinutesSinceReport { get; set; }

    /// <summary>Scratch space the simulator uses to restore <see cref="Now"/> after a permission/question resolves.</summary>
    public string? SavedNow { get; set; }

    // Derived presentation helpers
    public StateMeta Meta => StateMeta.Of(State);
    public bool ReportsDollars => Profile is not ("codex" or "grok");
    public bool HasDispatch => State != SessionState.Submitted;

    public string Sub => $"{Profile} · {Machine}";

    public string Elapsed
    {
        get
        {
            var ts = TimeSpan.FromSeconds(ElapsedSeconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}h{ts.Minutes:00}m"
                : ts.Minutes > 0 ? $"{ts.Minutes}m{ts.Seconds:00}s" : $"{ts.Seconds}s";
        }
    }

    public string TokensLabel
    {
        get
        {
            if (!HasDispatch) return "—";
            var k = Tokens / 1000.0;
            return $"{k:0.0}k";
        }
    }

    public string CostLabel
    {
        get
        {
            if (!HasDispatch) return "";
            if (!ReportsDollars) return "$ —";
            return CostUsd is { } c ? $"${c:0.00}" : "$ —";
        }
    }

    public string RateLabel
    {
        get
        {
            if (!HasDispatch) return "no dispatch yet";
            if (Meta.Live && State != SessionState.Failed && RateTokPerMin is { } r)
                return $"{r:0.0}k tok/min · {ToolCalls} calls";
            return $"last report {Math.Max(1, MinutesSinceReport)}m ago";
        }
    }

    public string RateColorVar => HasDispatch && Meta.Live && State != SessionState.Failed
        ? "--state-live"
        : "--color-neutral-700";

    public string NameColorVar => State == SessionState.Completed ? "--color-neutral-600" : "--color-neutral-200";

    public string NowColorVar => State switch
    {
        SessionState.Failed => "--state-error",
        SessionState.Completed => "--color-neutral-700",
        _ => "--color-neutral-500",
    };

    public string EdgeColorVar => Meta.Live ? Meta.ColorVar : "--color-surface";

    public string UnreadLabel => Unread > 0 ? Unread.ToString() : "—";
}
