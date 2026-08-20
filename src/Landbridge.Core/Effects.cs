namespace Landbridge.Core;

/// <summary>
/// Side effects a transition demands of the surrounding system, returned as
/// data so the engine stays pure and the store applies them transactionally
/// with the state write.
/// </summary>
public abstract record Effect;

/// <summary>Dispatch minted a new worker instance; issue its token (§5).</summary>
/// <param name="Machine">
/// The machine this dispatch went to. Carried on the effect because the instance row is
/// the system's one row per dispatch, and it is the only durable record of <em>where</em> a
/// dispatch ran: the live registry forgets a task the moment it exits (§10), so by the time
/// a human wants a terminal task's machine-local transcript (§12) nothing else remembers
/// which machine to ask.
/// </param>
public sealed record MintWorkerInstanceToken(WorkerInstanceId Instance, string Machine) : Effect;

/// <summary>
/// The named instance is no longer the incumbent. Revocation must land before
/// any successor is dispatched or resumed (§5, §11).
/// </summary>
public sealed record RevokeWorkerInstanceToken(WorkerInstanceId Instance) : Effect;

/// <summary>
/// Registered services are cleared and relay forwards released (§6). Emitted on every
/// transition out of <c>working</c> <em>except</em> a permission request (§11 permission
/// bridge): that worker never left its tool call, so it keeps both and is about to return
/// to <c>working</c> as the same incumbent — tearing them down would break a live worker
/// mid-turn for asking a question.
///
/// <para><b>The exception compounds.</b> Every path that emits this guards on the task
/// being <c>working</c>, so a permission-blocked task that is then parked on wait-TTL
/// expiry, or requeued on liveness loss, does not clear them either: a task can reach
/// <c>parked</c> or <c>submitted</c> still holding registered services and live relay
/// grants. Anything reasoning about grant lifetime should read that as the real
/// invariant rather than assuming this fires whenever <c>working</c> is left.</para>
/// </summary>
public sealed record ClearServicesAndForwards : Effect;

/// <summary>Persist the park record for redispatch affinity (§11).</summary>
public sealed record WriteParkRecord(ParkRecord Park) : Effect;

/// <summary>
/// Cancellation with disposition <c>discard</c>: the task's workspace instance is to be
/// removed (§11). <b>Nothing enacts this today</b> — the store matches it into an explicit
/// no-op arm, and no §10 command carries a workspace discard, so this records the Lead's
/// intent and removes nothing (§11: "nothing enacts workspace discard today, which is the
/// moment to get this right rather than after"). Whatever eventually enacts it must not
/// delete a directory a continuation merely borrowed (§11).
/// </summary>
public sealed record DiscardWorkspace : Effect;

/// <summary>
/// Cancellation with discard while a report is outstanding: deletion is deferred
/// until the session closes, so the Lead is never reading a vanished workspace
/// (§11). Like <see cref="DiscardWorkspace"/>, nothing enacts either half today —
/// this records which of the two intents applied.
/// </summary>
public sealed record DeferWorkspaceDiscardUntilVerdict : Effect;
