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

/// <summary>
/// Spec §7: who adjudicates a task's completion. <see cref="Lead"/> (the default)
/// lets the Lead session's verdict complete the task autonomously — orchestrator
/// judgment, the Claude Code shape; <see cref="Review"/> additionally requires human
/// confirmation. Either way a task's own worker can never complete it (§9 check 4,
/// doer/judge split). There is no automated-verifier mode — CI and tests are
/// evidence the Lead gathers itself, not a verdict-issuing actor.
/// </summary>
public enum CompletionMode
{
    Lead,
    Review,
}

/// <summary>
/// Spec §9 check 4: who supplied the completing verdict, recorded on the task so
/// the completion is legible (rendered on the §12 dashboard task view). Null until
/// a task reaches <see cref="TaskState.Completed"/>.
/// </summary>
public enum VerdictProvenance
{
    /// <summary>A Lead session adjudicated (lead mode, autonomous).</summary>
    LeadSession,

    /// <summary>A human adjudicated (a human session, or a human-confirmed review verdict).</summary>
    Human,
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

/// <summary>
/// Why the control plane requeued a task (infrastructure counter, §6). Recorded on
/// the requeue — on the task row and its event row — because otherwise every requeue
/// in the trail looks identical and a diagnosing operator cannot tell a wedged agent
/// from a dying machine (#73). The values name the signal that fired, not a severity:
/// nothing here decides anything, the two clocks and the runner events do.
/// </summary>
public enum LivenessLossReason
{
    /// <summary>The dispatch command never reached the machine, so nothing is running.</summary>
    AckTimeout,

    /// <summary>
    /// docketd stopped asserting the harness process is alive (§10's aliveness clock,
    /// ~60s). The process died without an <c>exited</c>, or the daemon itself is wedged.
    /// </summary>
    LivenessTimeout,

    /// <summary>
    /// The process is alive but produced no progress signal past the no-progress
    /// ceiling (§10's second clock, ~30min): the wedged-agent case. Distinct from
    /// <see cref="LivenessTimeout"/> because the remedies differ — a wedged agent is a
    /// task/harness problem, a silent daemon is a machine problem.
    /// </summary>
    NoProgress,

    /// <summary>
    /// The harness process exited while the task was still <c>working</c> (a runner
    /// <c>exited</c> event, §10) — it died rather than reporting a result.
    /// </summary>
    ProcessExited,

    /// <summary>The runner restarted adopting nothing, or its machine went silent (§10).</summary>
    MachineReboot,
}

/// <summary>
/// What a continuation task (§6/§11) does when the machine that held its
/// predecessor's transcript is gone at dispatch. <see cref="Degrade"/> cold-starts
/// a fresh session on any profile-matching machine (conversational memory is lost;
/// the plane logs an event so the Lead knows); <see cref="Pin"/> waits in
/// <see cref="TaskState.Submitted"/> for that machine to return, like a pinned
/// profile. Null on the row for a non-continuation task.
/// </summary>
public enum MachineGonePolicy
{
    Degrade,
    Pin,
}
