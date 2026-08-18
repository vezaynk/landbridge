namespace Landbridge.Core;

/// <summary>
/// Everything that can move a task through §6's table. Commands carry the
/// caller's credential and the facts the transition's checks need — the
/// engine itself holds no clock and reads no content. Timers (wait TTLs,
/// liveness) live in the control plane, which expresses their expiry as
/// commands.
/// </summary>
public abstract record SessionCommand(Actor Actor);

/// <summary>
/// → submitted. Only a lead claim may create tasks (§9 check 3).
///
/// <see cref="Description"/> (prose instructions, §7) and <see cref="Workspace"/>
/// (optional context, §7) ride along as content the engine never interprets.
/// The store persists them verbatim; the state machine stays free of session
/// content (§2 principle 1). The description is the whole brief — there is no
/// separate completion-criteria field.
///
/// <para><see cref="Continues"/> switches the task to <b>continuation targeting</b>
/// (§6/§11): rather than being dispatched to any profile-matching machine, the new
/// task resumes a prior task's harness session on the machine that holds it. The
/// resolved facts ride the command; the engine only validates them (same-Team,
/// profile declarable) and never dereferences a task id or session ref (§2
/// principle 1). Null for ordinary profile targeting.</para>
/// </summary>
public sealed record CreateSession(
    Actor Actor,
    TeamId Team,
    string Description,
    string Profile,
    string? Workspace = null,
    Continuation? Continues = null) : SessionCommand(Actor);

/// <summary>
/// Continuation targeting facts (§6/§11), resolved by the control plane from the
/// continued task's row and the live connection registry <em>before</em> the
/// <see cref="CreateSession"/> command reaches the engine. Everything here is opaque
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
    SessionId ContinuedSession,
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
    : SessionCommand(ControlPlaneActor.Instance);

/// <summary>
/// working|blocked_on_input → submitted (infrastructure counter), or → canceled once
/// that counter reaches the task's cap (§9 check 7). <see cref="Reason"/> names the
/// signal that fired and is persisted on the requeue, so the trail distinguishes a
/// wedged agent from a silent daemon from a rebooted runner (#73).
///
/// <para><see cref="Instance"/> fences the loss to <em>one dispatch attempt</em> (§9 check
/// 14), and which of the two forms a caller sends says what its signal was about:</para>
/// <list type="bullet">
///   <item><b>A per-dispatch clock names the attempt it judged.</b> The aliveness and
///   no-progress clocks judge one working attempt, from a read taken before this command is
///   applied — the engine holds no clock, so the plane reads, decides, and only then
///   applies. Naming the instance makes that decision a compare-and-set: it lands
///   only while the attempt it judged is still the incumbent of a still-<c>working</c>
///   task, so a task that has moved on in between is left alone instead of being requeued
///   on evidence about a dispatch that no longer exists. Two moves are why it matters — a
///   permission request parks the task with its incumbent still alive inside the tool call
///   it is waiting in (§11), and a requeue plus redispatch replaces the attempt outright —
///   and applying a stale loss to either requeues work nothing is wrong with and kills a
///   live worker.</item>
///   <item><b>Machine death names nothing.</b> A reboot, a dropped socket, or a machine
///   gone silent under a task blocked on input is a fact about everything that machine
///   holds rather than about one attempt, so those callers send no instance and the loss
///   applies to whichever incumbent it finds — including from <c>blocked_on_input</c>,
///   where a per-dispatch clock never fires at all (§11 suspends liveness there).</item>
/// </list>
/// </summary>
public sealed record LivenessLost(LivenessLossReason Reason, WorkerInstanceId? Instance = null)
    : SessionCommand(ControlPlaneActor.Instance);

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
    : SessionCommand(Actor)
{
    /// <summary>The in-band report's hard cap, 16 KiB of UTF-8 (§10). Over-cap is
    /// refused at report time (<see cref="Rule.ReportWithinSizeCap"/>) so detail
    /// goes to the workspace behind the result reference, keeping the plane's
    /// in-band carry bounded.</summary>
    public const int MaxReportBytes = 16 * 1024;
}

/// <summary>
/// verifying → completed. Caller identity is not an agent. Review mode trusts
/// the Lead; <see cref="HumanConfirmed"/> is accepted but not gated on.
/// </summary>
public sealed record VerdictAccept(Actor Actor, bool HumanConfirmed = false) : SessionCommand(Actor);

/// <summary>
/// verifying → rejected. A fail is not a redispatch: if the Lead wants more
/// from this worker they reply (<see cref="LeadMessage"/>) instead. Same
/// identity gate as <see cref="VerdictAccept"/>.
/// </summary>
public sealed record VerdictFail(Actor Actor, bool HumanConfirmed = false) : SessionCommand(Actor);

/// <summary>
/// working → blocked_on_input. Requires a typed request kind (§6).
///
/// <para><see cref="Question"/> is <em>what the worker is actually asking</em> (§10/§11):
/// the decision it cannot make, the options it sees, what it will do with each answer.
/// The <see cref="Kind"/> only <em>labels</em> the request — the plane branches on it for
/// <see cref="InputRequestKind.Permission"/> alone, and never uses it to decide who may
/// answer (any Lead or human may answer any kind) — so without the question the channel is a
/// doorbell, and a Lead or human sees that a task needs attention but not what for. §11's
/// "Answered by" column is guidance to the answerer, not an authorization the engine
/// enforces. It rides UP through the plane exactly as the Lead's
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
    : SessionCommand(Actor)
{
    /// <summary>The question's hard cap: the same 16 KiB in-band class as the worker's
    /// report (<see cref="ReportResult.MaxReportBytes"/>) — one number for every piece
    /// of prose the plane carries in-band (§10). Over-cap is refused
    /// (<see cref="Rule.QuestionWithinSizeCap"/>) and the task stays <c>working</c>, so
    /// the worker can ask again, shorter.</summary>
    public const int MaxQuestionBytes = ReportResult.MaxReportBytes;
}

/// <summary>
/// blocked_on_input → submitted: the Lead or a human answered. Waiting is the park shape
/// for every kind a worker <em>chooses</em> to ask (§11) — such a worker has ended its turn
/// and its process is gone — so the answer cannot resume in place and routes through the
/// same park→redispatch path the wait-TTL sweeper uses. The one exception is
/// <see cref="InputRequestKind.Permission"/>, which the harness asks and whose process
/// stays up; that request is answered by <see cref="AnswerPermission"/> instead, and
/// <see cref="PendingKind"/> below is what keeps the two paths from crossing.
/// <see cref="Park"/> is the
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
/// store and read back by the resumed worker's opening <c>get_session</c> — never
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
    : SessionCommand(Actor)
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
/// writes no park record, and leaves <see cref="SessionRecord.CurrentInstance"/> exactly as
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
    string? Message = null) : SessionCommand(Actor)
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
    Actor Actor, InputRequestKind? PendingKind, string Reason) : SessionCommand(Actor);

/// <summary>blocked_on_input → parked: wait TTL expired, lease released (§6, §11).
/// Off by default under ACP (wait is indefinite); kept for explicit config.</summary>
public sealed record WaitTtlExpired(ParkRecord Park) : SessionCommand(ControlPlaneActor.Instance);

/// <summary>
/// working | blocked_on_input → parked: a Lead or human released the session on
/// purpose. Not a timer. The ACP host gets session/cancel; the instance token
/// is revoked; a later wake is session/load (or PromptCommand if the process
/// is still up — it will not be after cancel).
/// </summary>
public sealed record Park(Actor Actor, ParkRecord Record) : SessionCommand(Actor);

/// <summary>
/// blocked_on_input → working: a Lead or human answered a still-live ACP
/// session. The process is idle waiting for a follow-up prompt, so this keeps
/// the incumbent instance, revokes nothing, and writes no park record. The
/// plane then sends <c>PromptCommand</c>; the worker pulls the answer on
/// <c>get_session</c> (ideas/sessions.md).
///
/// <para><see cref="PendingKind"/> is the live kind of the request being
/// answered, supplied by the store off the task row. A
/// <see cref="InputRequestKind.Permission"/> request must never be answered
/// this way (<see cref="Rule.PermissionVerdictAnswersPermissionRequests"/>) —
/// that worker is blocked inside a tool call and needs a verdict, not a
/// follow-up turn.</para>
/// </summary>
public sealed record ContinueSession(
    Actor Actor, string? Answer = null, InputRequestKind? PendingKind = null) : SessionCommand(Actor);

/// <summary>
/// working → working: the Lead or a human sent a follow-up without a pending
/// question. The process is still up; the plane doorbells it and the worker
/// pulls the text on <c>get_session</c>. The Claude-subagent shape: the parent
/// speaks when it has something to say, not only when the child files a ticket.
/// </summary>
public sealed record LeadMessage(
    Actor Actor, string? Text = null, InputRequestKind? PendingKind = null) : SessionCommand(Actor);

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
public sealed record WakeParked(string? Answer = null) : SessionCommand(ControlPlaneActor.Instance);

/// <summary>working → parked: stop with disposition preserve_and_park (§6, §11).</summary>
public sealed record StopPreserveAndPark(Actor Actor, ParkRecord Park) : SessionCommand(Actor);

/// <summary>
/// any → canceled. Disposition required (§9 check 12), and the actor is a Lead or a
/// human: cancelling is a judgement about the work, which the plane does not make. The
/// one thing it gives up on is <em>placing</em> the work, and that path is the §9 check 7
/// requeue cap inside <c>LivenessLost</c> — not a command anyone sends (§6).
/// </summary>
public sealed record Cancel(Actor Actor, CancelDisposition? Disposition) : SessionCommand(Actor);
