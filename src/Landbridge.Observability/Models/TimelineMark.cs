namespace Landbridge.Observability.Models;

/// <summary>
/// One mark on a session's timeline track, matching the runner-wire event kinds
/// the mockup draws: tool-calls as ticks, asks/answers as triangles, forwards as
/// bars, errors as diamonds, park/dispatch/done as dots.
/// </summary>
public enum MarkType
{
    Tool,
    Ask,
    Answer,
    Forward,
    Error,
    Park,
    Dispatch,
    Done,
}

public sealed class TimelineMark
{
    public required MarkType Type { get; init; }

    /// <summary>Position along the timeline window, 0-100.</summary>
    public required double PositionPct { get; set; }

    /// <summary>Only used by <see cref="MarkType.Forward"/> — how wide the bar is, 0-100.</summary>
    public double WidthPct { get; init; }

    public string Label => Type switch
    {
        MarkType.Tool => "tool-call",
        MarkType.Ask => "worker → Lead",
        MarkType.Answer => "Lead → worker",
        MarkType.Forward => "forward open",
        MarkType.Error => "auth-failed",
        MarkType.Park => "parked",
        MarkType.Dispatch => "dispatch",
        MarkType.Done => "report_result",
        _ => "",
    };
}
