namespace Landbridge.Observability.Models;

/// <summary>A registered service / relay forward exposed by a session.</summary>
public sealed class PortInfo
{
    public required string Name { get; init; }
    public required int Port { get; init; }
    public bool Live { get; set; }

    public string Label => $"{Name}:{Port}";
}

/// <summary>One machine in the fleet, shown in the left rail.</summary>
public sealed class Machine
{
    public required string Id { get; init; }
    public bool BackPressure { get; set; }
    public required string LoadText { get; set; }
}

/// <summary>One line of the worker/Lead exchange shown in the detail panel.</summary>
public sealed class ChatMessage
{
    public required string From { get; init; } // "Worker" | "Lead"
    public required string At { get; init; }
    public required string Text { get; init; }

    public bool FromLead => From == "Lead";
}

/// <summary>One line of the tailing transcript.</summary>
public sealed class TranscriptLine
{
    public required string Time { get; init; }
    public required string Text { get; init; }

    /// <summary>True for narration text; false for dimmed tool-call / event lines.</summary>
    public TranscriptLineKind Kind { get; init; } = TranscriptLineKind.ToolCall;
}

public enum TranscriptLineKind
{
    ToolCall,
    Narration,
    Waiting,
}
