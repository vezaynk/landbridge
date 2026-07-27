namespace Docket.Core;

/// <summary>
/// Everything that can move a task through §6's table. Commands carry the
/// caller's credential and the facts the transition's checks need — the
/// engine itself holds no clock and reads no content. Timers (wait TTLs,
/// liveness) live in the control plane, which expresses their expiry as
/// commands.
/// </summary>
public abstract record TaskCommand(Actor Actor);

/// <summary>→ submitted. Only a lead claim may create tasks (§9 check 3).</summary>
public sealed record CreateTask(
    Actor Actor,
    TeamId Team,
    string CompletionCriteria,
    CompletionMode Mode,
    string? Profile,
    bool TeamBudgetRemains) : TaskCommand(Actor);

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
/// blocked_on_input → working: the answer landed within the wait TTL and the
/// dispatched machine still holds the lease (§6).
/// </summary>
public sealed record AnswerInput(Actor Actor, bool LeaseStillHeld) : TaskCommand(Actor);

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
