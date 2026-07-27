namespace Docket.Core;

/// <summary>
/// Written when a task parks, spec §11. Redispatch prefers this machine and
/// directory because harness transcripts are machine- and directory-local.
/// All fields are opaque to the control plane; it stores and returns them,
/// never dereferences them.
/// </summary>
public sealed record ParkRecord(
    string Machine,
    string Directory,
    string HarnessSessionRef,
    int Attempt);

/// <summary>
/// What dispatch needs to know about a machine, as reported by its runner.
/// Ready/back-pressure are derived by docketd (§10); profiles are declared
/// names the control plane never interprets.
/// </summary>
public sealed record MachineSnapshot(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    IReadOnlySet<string> DeclaredProfiles)
{
    public const string DefaultProfile = "default";
}

/// <summary>
/// The typed task record, spec §7. Prose fields (description, result_summary,
/// blocker_note) and the opaque blobs (workspace, completion.criteria content)
/// live at the storage layer; nothing here requires interpreting them.
/// </summary>
public sealed record TaskRecord
{
    public required TaskId Id { get; init; }
    public required TeamId Team { get; init; }

    /// <summary>Server-assigned team-{id}/task-{id}; uniqueness is structural (§9 check 2).</summary>
    public required string Namespace { get; init; }

    public required CompletionMode CompletionMode { get; init; }

    public TaskState State { get; init; } = TaskState.Submitted;

    /// <summary>Optional runner profile name; exact-match routing, never interpreted (§7).</summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Dispatches so far, incremented on every dispatch (§7). Visible to the
    /// worker so a successor knows it may inherit a dirty workspace.
    /// </summary>
    public int Attempt { get; init; }

    /// <summary>Requeues from ack/liveness/reboot. Never drives rejection (§6).</summary>
    public int InfrastructureRequeues { get; init; }

    /// <summary>Failed verification verdicts. The only counter that drives rejection (§6).</summary>
    public int VerificationFailures { get; init; }

    public int VerificationRetryLimit { get; init; } = 3;

    /// <summary>The incumbent worker instance, if one is dispatched (§9 check 14).</summary>
    public WorkerInstanceId? CurrentInstance { get; init; }

    public ParkRecord? Park { get; init; }
}
