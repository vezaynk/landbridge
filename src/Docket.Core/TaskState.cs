namespace Docket.Core;

/// <summary>Task states, spec §6.</summary>
public enum TaskState
{
    Submitted,
    Working,
    Verifying,
    Completed,
    Rejected,
    BlockedOnInput,
    Parked,
    Canceled,
}

public static class TaskStateExtensions
{
    /// <summary>Spec §6: terminal states are final and never resumed.</summary>
    public static bool IsTerminal(this TaskState state) =>
        state is TaskState.Completed or TaskState.Rejected or TaskState.Canceled;
}

/// <summary>Spec §7: which verdict identity a task expects, nothing else.</summary>
public enum CompletionMode
{
    Automated,
    Review,
}

/// <summary>Spec §11: typed request kinds for blocked_on_input.</summary>
public enum InputRequestKind
{
    Question,
    SpawnRequest,
    AuthHelp,
    EndpointWait,
    Unreachable,
}

/// <summary>
/// Cancellation dispositions, spec §11. preserve_and_park is not here: it is
/// the stop path into <see cref="TaskState.Parked"/>, not a terminal cancel.
/// Budget is reserved to the control plane (§6: it may cancel only on Team
/// budget exhaustion).
/// </summary>
public enum CancelDisposition
{
    Preserve,
    Discard,
    Budget,
}

/// <summary>Why the control plane requeued a task (infrastructure counter, §6).</summary>
public enum LivenessLossReason
{
    AckTimeout,
    LivenessTimeout,
    MachineReboot,
}
