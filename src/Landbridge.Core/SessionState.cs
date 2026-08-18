namespace Landbridge.Core;

/// <summary>Task states, spec §6.</summary>
public enum SessionState
{
    Submitted,
    Working,
    Verifying,
    Completed,
    Rejected,
    BlockedOnInput,
    Parked,
    Canceled,

    /// <summary>
    /// Infrastructure gave up on this attempt (handshake, process death, turn
    /// ended with no report, machine gone). Same release as
    /// <see cref="Parked"/> — token revoked, process gone, session ref kept —
    /// but the Lead did not choose it. Not terminal: resume is
    /// <c>session/load</c> with a note. Inbox, unlike a deliberate park.
    /// </summary>
    Failed,
}

public static class SessionStateExtensions
{
    /// <summary>Spec §6: terminal states are final and never resumed.</summary>
    public static bool IsTerminal(this SessionState state) =>
        state is SessionState.Completed or SessionState.Rejected or SessionState.Canceled;
}

/// <summary>
/// Spec §9 check 4: who supplied the completing verdict, recorded on the task so
/// the completion is legible (rendered on the §12 dashboard task view). Null until
/// a task reaches <see cref="SessionState.Completed"/>.
/// </summary>
public enum VerdictProvenance
{
    /// <summary>A Lead session adjudicated (lead mode, autonomous).</summary>
    LeadSession,

    /// <summary>A human adjudicated (a human session).</summary>
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

    /// <summary>
    /// A harness permission prompt relayed into the plane (§11 permission bridge): the
    /// worker's harness wants to use a tool its allowlist does not cover, and Landbridge is
    /// standing in for the human who would have answered the dialog. The one kind whose
    /// wait is <em>live</em> — the asking process stays up inside its tool call, because
    /// the harness contract has nowhere to put a resumed answer — so it is answered with
    /// a <see cref="PermissionVerdict"/> back into <c>working</c> rather than through the
    /// park→redispatch path every other kind takes.
    /// </summary>
    Permission,
}

/// <summary>
/// The answer to an <see cref="InputRequestKind.Permission"/> request (§11): whether the
/// harness may make the tool call it asked about. Maps 1:1 onto the two arms of the
/// harness's own permission result, so the bridge translates rather than interprets.
/// </summary>
public enum PermissionVerdict
{
    /// <summary>The tool call proceeds, with the input the harness proposed.</summary>
    Allow,

    /// <summary>
    /// The tool call is refused. Always carries a message
    /// (<see cref="Rule.PermissionDenialCarriesMessage"/>): a denial the agent cannot
    /// read is a wall it will walk into again, so the message is the guidance that makes
    /// it adapt instead of retry.
    /// </summary>
    Deny,
}

/// <summary>
/// Who decided a permission request (§11/§12), recorded on the deciding event so the
/// audit trail distinguishes a Lead's routine approval from a human's. Derived from the
/// answering actor, never supplied by a caller — the same shape as
/// <see cref="VerdictProvenance"/>.
/// </summary>
public enum PermissionAnswerer
{
    /// <summary>A Lead session triaged it (the routine case).</summary>
    Lead,

    /// <summary>A human answered from the §12 dashboard.</summary>
    Human,

    /// <summary>
    /// The plane's classifier allowed it before a wait opened. Not a Lead or a
    /// human — recorded so "who approved this" stays answerable.
    /// </summary>
    Plane,
}

/// <summary>
/// Cancellation dispositions, spec §11. preserve_and_park is not here: it is
/// the stop path into <see cref="SessionState.Parked"/>, not a terminal cancel.
///
/// <para><c>Budget</c> was a third value, reserved to the control plane for the dollar
/// ceiling's exhaustion cancel; both went with the budget subsystem (2026-08-12, §9's
/// note). Nothing in the plane ever sent it — the ceiling's containment sweep chose
/// <c>stop(preserve)</c> over cancelling, exactly so a raised ceiling could resume the
/// task — so its removal takes no path with it.</para>
/// </summary>
public enum CancelDisposition
{
    Preserve,
    Discard,
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
    /// landbridged stopped asserting the harness process is alive (§10's aliveness clock,
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

    /// <summary>
    /// An ACP worker's turn ended while the task was still <c>working</c> — it stopped
    /// talking rather than reporting a result or asking a question (a runner
    /// <c>turn-ended</c> event, §10).
    ///
    /// <para>The session-model counterpart of <see cref="ProcessExited"/>, and it exists
    /// because those two stopped being the same observation. Under the stream protocol a
    /// worker that finished without reporting had also exited, so its silence arrived as a
    /// death and requeued on that reason. An ACP session outlives its turn by design
    /// (<c>ideas/sessions.md</c> stage 1), so the process is still there, still alive, still
    /// heartbeating — and without this the task would sit in <c>working</c> until a human
    /// noticed. Kept distinct from <see cref="ProcessExited"/> because the remedies differ:
    /// nothing crashed here, the agent simply stopped, and the <c>stopReason</c> on the
    /// turn-ended event usually says why.</para>
    /// </summary>
    TurnEndedWithoutResult,
}

/// <summary>
/// What a continuation task (§6/§11) does when the machine that held its
/// predecessor's transcript is gone at dispatch. <see cref="Degrade"/> cold-starts
/// a fresh session on any profile-matching machine (conversational memory is lost;
/// the plane logs an event so the Lead knows); <see cref="Pin"/> waits in
/// <see cref="SessionState.Submitted"/> for that machine to return, like a pinned
/// profile. Null on the row for a non-continuation task.
/// </summary>
public enum MachineGonePolicy
{
    Degrade,
    Pin,
}
