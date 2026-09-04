using Landbridge.Contracts;

namespace Landbridge.ControlPlane;

/// <summary>
/// Agent-started process on a machine (§10). Last-value from the heartbeat:
/// landbridged reports the set, the plane upserts by name. The hub doorbells
/// <c>processes</c> (per machine) and <c>process</c> (this row's id).
/// </summary>
public sealed class MachineProcessRow
{
    public Guid Id { get; set; }
    public Guid MachineId { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = nameof(ProcessState.Running);
    public Guid DeclaredBySession { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset? ExitedAt { get; set; }
    public bool StdinOpen { get; set; }

    public ProcessStatus ToStatus()
    {
        Enum.TryParse<ProcessState>(State, out var state);
        return new ProcessStatus(Name, state, DeclaredBySession, StartedAt, ExitCode, ExitedAt, StdinOpen);
    }
}
