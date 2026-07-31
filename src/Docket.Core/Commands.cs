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

/// <summary>working|blocked_on_input → submitted (infrastructure counter).</summary>
public sealed record LivenessLost(LivenessLossReason Reason)
    : TaskCommand(ControlPlaneActor.Instance);

/// <summary>working → verifying. Requires a result reference (§6).</summary>
public sealed record ReportResult(Actor Actor, string? ResultReference) : TaskCommand(Actor);

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

/// <summary>working → blocked_on_input. Requires a typed request kind (§6).</summary>
public sealed record RequestInput(Actor Actor, InputRequestKind? Kind) : TaskCommand(Actor);

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
/// </summary>
public sealed record AnswerInput(Actor Actor, ParkRecord? Park) : TaskCommand(Actor);

/// <summary>blocked_on_input → parked: wait TTL expired, lease released (§6, §11).</summary>
public sealed record WaitTtlExpired(ParkRecord Park) : TaskCommand(ControlPlaneActor.Instance);

/// <summary>
/// parked → submitted: the awaited answer or endpoint landed. Redispatch then
/// runs the full submitted → working checks, preferring the park record's
/// machine (§6, §11).
/// </summary>
public sealed record WakeParked() : TaskCommand(ControlPlaneActor.Instance);

/// <summary>working → parked: stop with disposition preserve_and_park (§6, §11).</summary>
public sealed record StopPreserveAndPark(Actor Actor, ParkRecord Park) : TaskCommand(Actor);

/// <summary>
/// any → canceled. Disposition required (§9 check 12); the control plane may
/// cancel only on Team budget exhaustion (§6).
/// </summary>
public sealed record Cancel(Actor Actor, CancelDisposition? Disposition) : TaskCommand(Actor);
