namespace Docket.Core;

/// <summary>
/// Side effects a transition demands of the surrounding system, returned as
/// data so the engine stays pure and the store applies them transactionally
/// with the state write.
/// </summary>
public abstract record Effect;

/// <summary>Dispatch minted a new worker instance; issue its token (§5).</summary>
public sealed record MintWorkerInstanceToken(WorkerInstanceId Instance) : Effect;

/// <summary>
/// The named instance is no longer the incumbent. Revocation must land before
/// any successor is dispatched or resumed (§5, §11).
/// </summary>
public sealed record RevokeWorkerInstanceToken(WorkerInstanceId Instance) : Effect;

/// <summary>
/// Emitted on every transition out of working: registered services are
/// cleared and relay forwards released (§6).
/// </summary>
public sealed record ClearServicesAndForwards : Effect;

/// <summary>Persist the park record for redispatch affinity (§11).</summary>
public sealed record WriteParkRecord(ParkRecord Park) : Effect;

/// <summary>Cancellation with disposition discard: remove the task's workspace instance (§11).</summary>
public sealed record DiscardWorkspace : Effect;

/// <summary>
/// Cancellation with discard while verifying: deletion is deferred until the
/// task leaves verifying, so a verifier is never checking a vanished
/// workspace (§11).
/// </summary>
public sealed record DeferWorkspaceDiscardUntilVerdict : Effect;
