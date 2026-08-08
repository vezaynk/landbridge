namespace Docket.Core;

/// <summary>
/// Everything that can move a task through §6's table. Commands carry the
/// caller's credential and the facts the transition's checks need — the
/// engine itself holds no clock and reads no content. Timers (wait TTLs,
/// liveness) live in the control plane, which expresses their expiry as
/// commands.
/// </summary>
public abstract record TaskCommand(Actor Actor);

/// <summary>
/// → submitted. Only a lead claim may create tasks (§9 check 3).
///
/// <see cref="Description"/> (prose instructions, §7) and <see cref="Workspace"/>
/// (the Lead-assigned opaque isolation blob, §7) ride along as content the
/// <em>engine never interprets</em> — exactly like <see cref="CompletionCriteria"/>,
/// which <see cref="TaskStateMachine.Create"/> only checks for non-emptiness and
/// never lands on the pure-state <see cref="TaskRecord"/>. The store persists all
/// three verbatim; the state machine stays free of task content (§2 principle 1).
///
/// <para><see cref="Continues"/> switches the task to <b>continuation targeting</b>
/// (§6/§11): rather than being dispatched to any profile-matching machine, the new
/// task resumes a prior task's harness session on the machine that holds it. The
/// resolved facts ride the command; the engine only validates them (same-Team,
/// profile declarable) and never dereferences a task id or session ref (§2
/// principle 1). Null for ordinary profile targeting.</para>
/// </summary>
public sealed record CreateTask(
    Actor Actor,
    TeamId Team,
    string CompletionCriteria,
    CompletionMode Mode,
    string? Profile,
    bool TeamBudgetRemains,
    string Description = "",
    string? Workspace = null,
    Continuation? Continues = null) : TaskCommand(Actor);

/// <summary>
/// Continuation targeting facts (§6/§11), resolved by the control plane from the
/// continued task's row and the live connection registry <em>before</em> the
/// <see cref="CreateTask"/> command reaches the engine. Everything here is opaque
/// to the engine — it dereferences none of it — but two fields gate creation:
/// <see cref="ContinuedTeam"/> must equal the creating Team (continuation is
/// same-Team only), and, when <see cref="PreferredMachineProfiles"/> is known
/// (the preferred machine is currently connected), the effective profile must be
/// one the preferred machine declares, or the continuation could never dispatch to
/// the machine that holds its transcript.
///
/// <para><see cref="PreferredMachine"/> is the machine that last held/ran the
/// continued task; the plane seeds it and <see cref="InheritedSessionRef"/> onto the
/// new task as park-record-style affinity, so the first dispatch prefers that
/// machine and hands the runner the session ref to <c>--resume</c> (§11 resume
/// seam). <see cref="OnMachineGone"/> decides what happens if that machine is gone
/// at dispatch. <see cref="PreferredMachineProfiles"/> is null when the machine's
/// declared profiles are not known at creation (it is gone), which skips the
/// profile-declarable check — dispatch's own profile routing still applies.</para>
/// </summary>
public sealed record Continuation(
    TaskId ContinuedTask,
    TeamId ContinuedTeam,
    string? PreferredMachine,
    string? InheritedSessionRef,
    MachineGonePolicy OnMachineGone,
    IReadOnlySet<string>? PreferredMachineProfiles);

/// <summary>
/// submitted → working. The dispatch transaction is the claim (§6); the
/// single-claimant guarantee itself is the store's SKIP LOCKED transaction
/// (§9 check 5) — this command carries the machine-eligibility half.
/// </summary>
public sealed record Dispatch(MachineSnapshot Machine, WorkerInstanceId NewInstance)
    : TaskCommand(ControlPlaneActor.Instance);

/// <summary>
/// working|blocked_on_input → submitted (infrastructure counter), or → canceled once
/// that counter reaches the task's cap (§9 check 7). <see cref="Reason"/> names the
/// signal that fired and is persisted on the requeue, so the trail distinguishes a
/// wedged agent from a silent daemon from a rebooted runner (#73).
/// </summary>
public sealed record LivenessLost(LivenessLossReason Reason)
    : TaskCommand(ControlPlaneActor.Instance);

/// <summary>
/// working → verifying. Requires a result reference (§6).
///
/// <para><see cref="Report"/> is the worker's optional in-band summary (§10): what
/// it did, what it verified, evidence pointers, and proposals (e.g. "task X should
/// run on profile Y"). It flows UP through the plane exactly as the Lead's
/// description flows DOWN — opaque content the engine never interprets (§2
/// principle 1), the store persists verbatim. The <see cref="ResultReference"/>
/// stays the load-bearing artifact pointer; the report is annotation, not
/// authority, and is size-capped (<see cref="MaxReportBytes"/>) so a worker puts
/// real detail in the workspace behind the reference rather than in the plane.</para>
/// </summary>
public sealed record ReportResult(Actor Actor, string? ResultReference, string? Report = null)
    : TaskCommand(Actor)
{
    /// <summary>The in-band report's hard cap, 16 KiB of UTF-8 (§10). Over-cap is
    /// refused at report time (<see cref="Rule.ReportWithinSizeCap"/>) so detail
    /// goes to the workspace behind the result reference, keeping the plane's
    /// in-band carry bounded.</summary>
    public const int MaxReportBytes = 16 * 1024;
}

/// <summary>
/// verifying → completed. Caller identity is not an agent; in review mode the
/// verdict carries human confirmation (§6, §7).
/// </summary>
public sealed record VerdictAccept(Actor Actor, bool HumanConfirmed = false) : TaskCommand(Actor);

/// <summary>
/// verifying → submitted while verification retries remain, else → rejected
/// (§6). Same identity gate as <see cref="VerdictAccept"/>.
/// </summary>
public sealed record VerdictFail(Actor Actor, bool HumanConfirmed = false) : TaskCommand(Actor);

/// <summary>
/// working → blocked_on_input. Requires a typed request kind (§6).
///
/// <para><see cref="Question"/> is <em>what the worker is actually asking</em> (§10/§11):
/// the decision it cannot make, the options it sees, what it will do with each answer.
/// The <see cref="Kind"/> only routes the request — who can answer it — so without the
/// question the channel is a doorbell, and a Lead or human sees that a task needs
/// attention but not what for. It rides UP through the plane exactly as the Lead's
/// description rides DOWN: opaque content the engine never interprets (§2 principle 1),
/// the store persists verbatim, and it is size-capped
/// (<see cref="MaxQuestionBytes"/>) so a worker keeps the ask a question rather than
/// pasting a workspace into the plane. Optional, because
/// <see cref="InputRequestKind.EndpointWait"/> and friends are self-describing.</para>
///
/// <para><see cref="PermissionTool"/> is set only on an
/// <see cref="InputRequestKind.Permission"/> request (§11 permission bridge) and names the
/// harness tool awaiting approval — required for that kind
/// (<see cref="Rule.PermissionRequestNamesItsTool"/>), because "something wants
/// permission" is not a question anyone can answer. It is a non-emptiness check only: the
/// name is the harness's string and the engine neither parses nor recognizes it, exactly
/// as it never reads <see cref="Question"/> (which on that kind carries the proposed tool
/// input verbatim).</para>
/// </summary>
public sealed record RequestInput(
    Actor Actor, InputRequestKind? Kind, string? Question = null, string? PermissionTool = null)
    : TaskCommand(Actor)
{
    /// <summary>The question's hard cap: the same 16 KiB in-band class as the worker's
    /// report (<see cref="ReportResult.MaxReportBytes"/>) — one number for every piece
    /// of prose the plane carries in-band (§10). Over-cap is refused
    /// (<see cref="Rule.QuestionWithinSizeCap"/>) and the task stays <c>working</c>, so
    /// the worker can ask again, shorter.</summary>
    public const int MaxQuestionBytes = ReportResult.MaxReportBytes;
}

/// <summary>
/// blocked_on_input → submitted: the Lead or a human answered. §11 guarantees a
/// headless worker's process is gone the moment it blocked ("waiting is always
/// the park shape"), so the answer cannot resume in place — it routes through the
/// same park→redispatch path the wait-TTL sweeper uses. <see cref="Park"/> is the
/// record written for redispatch affinity (§11), built by the control plane from
/// the held-lease machine and the row's stamped harness session ref; it is null
/// only when the dispatched machine is gone, and redispatch then cold-starts
/// elsewhere. This never touches the infrastructure counter — a Lead answering is
/// not an infrastructure requeue (§6, two counters).
///
/// <para><see cref="Answer"/> is the answer's <em>content</em> (§10/§11) — the other
/// half of <see cref="RequestInput.Question"/>. Without it the transition only
/// unblocks the task, and the redispatched worker resumes knowing it was answered
/// but not with what, which is how a worker guesses or asks the same question
/// again. Opaque content, capped at <see cref="MaxAnswerBytes"/>, persisted by the
/// store and read back by the resumed worker's opening <c>get_task</c> — never
/// through argv, which leaks via <c>ps</c> (§13).</para>
///
/// <para><see cref="PendingKind"/> is the live kind of the request being answered, supplied
/// by the store off the task row (a fact the caller provides, like
/// <see cref="Dispatch.Machine"/>). It exists for one refusal: an
/// <see cref="InputRequestKind.Permission"/> request must never be answered this way
/// (<see cref="Rule.PermissionVerdictAnswersPermissionRequests"/>). Prose-answering a
/// permission request would revoke the token of a worker that is still alive and blocked
/// inside its tool call, stranding it — so the two answer paths refuse each other's
/// requests rather than silently doing the wrong thing. Null means "not supplied", which
/// keeps every non-permission caller unchanged.</para>
/// </summary>
public sealed record AnswerInput(
    Actor Actor, ParkRecord? Park, string? Answer = null, InputRequestKind? PendingKind = null)
    : TaskCommand(Actor)
{
    /// <summary>The answer's hard cap, the same in-band class as the question and the
    /// report (§10). Over-cap is refused (<see cref="Rule.AnswerWithinSizeCap"/>) and
    /// the task stays <c>blocked_on_input</c>, so the answer is never half-delivered.</summary>
    public const int MaxAnswerBytes = ReportResult.MaxReportBytes;
}

/// <summary>
/// blocked_on_input → <b>working</b>: a permission request was decided (§11 permission
/// bridge). The one answer path that resumes a worker <em>in place</em>, and the reason
/// it has to exist: the harness's permission contract has no resumed-answer seam — it
/// blocks inside the relaying tool call and there is nowhere to deliver a verdict to a
/// process that has exited — so unlike <see cref="AnswerInput"/> this revokes no token,
/// writes no park record, and leaves <see cref="TaskRecord.CurrentInstance"/> exactly as
/// it was. The asking worker is still the incumbent and picks up its own tool call.
///
/// <para><see cref="PendingKind"/> and <see cref="EscalatedToHuman"/> are facts the store
/// supplies off the task row. The first confines this path to permission requests
/// (<see cref="Rule.PermissionVerdictAnswersPermissionRequests"/>); the second is what
/// makes escalation mean something — once a request is escalated a
/// <see cref="LeadClaim"/> is refused
/// (<see cref="Rule.EscalatedPermissionIsHumanOnly"/>) and only a
/// <see cref="HumanSession"/> can decide it. A human can always answer, escalated or
/// not: escalation removes the Lead's authority, it does not create the human's.</para>
///
/// <para><see cref="Message"/> is guidance for the worker, and a
/// <see cref="PermissionVerdict.Deny"/> requires one
/// (<see cref="Rule.PermissionDenialCarriesMessage"/>) — a refusal an agent cannot read
/// teaches it nothing, so it retries the same call. Opaque content, capped like every
/// other in-band prose field (§10).</para>
/// </summary>
public sealed record AnswerPermission(
    Actor Actor,
    InputRequestKind? PendingKind,
    bool EscalatedToHuman,
    PermissionVerdict Verdict,
    string? Message = null) : TaskCommand(Actor)
{
    /// <summary>The verdict message's cap — the same in-band class as the answer it
    /// replaces (§10).</summary>
    public const int MaxMessageBytes = AnswerInput.MaxAnswerBytes;
}

/// <summary>
/// blocked_on_input → blocked_on_input: a pending permission request is marked human-only
/// (§11 permission bridge). Not a state change — the worker is still blocked and still
/// waiting — but an authority change: after this the Lead can no longer decide this one
/// request (<see cref="Rule.EscalatedPermissionIsHumanOnly"/>) and it sits for a human.
///
/// <para><see cref="Reason"/> is required (<see cref="Rule.PermissionEscalationCarriesReason"/>).
/// The human inherits a decision without the Lead's context, so an escalation that does not
/// say what worried the Lead just moves the guessing to a slower answerer. Opaque content,
/// capped like the verdict message.</para>
/// </summary>
public sealed record EscalatePermission(
    Actor Actor, InputRequestKind? PendingKind, string Reason) : TaskCommand(Actor);

/// <summary>blocked_on_input → parked: wait TTL expired, lease released (§6, §11).</summary>
public sealed record WaitTtlExpired(ParkRecord Park) : TaskCommand(ControlPlaneActor.Instance);

/// <summary>
/// parked → submitted: the awaited answer or endpoint landed. Redispatch then
/// runs the full submitted → working checks, preferring the park record's
/// machine (§6, §11).
///
/// <para><see cref="Answer"/> carries the answer's content on the <em>parked</em> half
/// of the one-call answer path (§11): a Lead answering does not know, and should not
/// have to know, whether the wait-TTL sweeper parked the task first, so the text must
/// land either way. Null on the wakes that answer nothing in words — an
/// <see cref="InputRequestKind.EndpointWait"/> consumer woken because the service
/// registered. Same cap and opacity as <see cref="AnswerInput.Answer"/>.</para>
/// </summary>
public sealed record WakeParked(string? Answer = null) : TaskCommand(ControlPlaneActor.Instance);

/// <summary>working → parked: stop with disposition preserve_and_park (§6, §11).</summary>
public sealed record StopPreserveAndPark(Actor Actor, ParkRecord Park) : TaskCommand(Actor);

/// <summary>
/// any → canceled. Disposition required (§9 check 12); the control plane may
/// cancel only on Team budget exhaustion (§6).
/// </summary>
public sealed record Cancel(Actor Actor, CancelDisposition? Disposition) : TaskCommand(Actor);
