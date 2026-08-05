namespace Docket.Core;

/// <summary>
/// Written when a task parks, spec §11. Redispatch prefers this machine and
/// directory because harness transcripts are machine- and directory-local.
/// All fields are opaque to the control plane; it stores and returns them,
/// never dereferences them.
///
/// <see cref="Directory"/> and <see cref="HarnessSessionRef"/> are nullable because
/// they originate <em>runner-side</em> (§11's resume seam): a harness working
/// directory and session id are machine-local facts the plane cannot observe on its
/// own. The session ref does reach it — the runner's <c>session-started</c> event
/// stamps it on the task row, and the wait-TTL sweeper copies it into the park record
/// so redispatch can resume the transcript — so it is null only for a work session
/// that never reported one. The directory has no producer at all and is always null
/// from the sweeper, so such a redispatch cold-starts from the workspace plus the
/// worker's persisted notes (§11 explicitly allows this when the recorded directory is
/// absent), and null honestly means "not known to the plane" rather than an empty path.
/// A <see cref="StopPreserveAndPark"/> issued through a runner may carry either.
/// </summary>
public sealed record ParkRecord(
    string Machine,
    string? Directory,
    string? HarnessSessionRef,
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
    /// <summary>
    /// The default infrastructure requeue cap (§9 check 7). Five is enough that a task
    /// survives an ordinary bad patch — a machine rebooting, a redeploy, one flaky
    /// dispatch — and small enough that a genuinely wedged task stops burning
    /// authorization inside an hour rather than forever: each attempt now costs a
    /// no-progress ceiling (30 min by default) and commits a fresh per-dispatch budget
    /// cap (§9.9).
    /// </summary>
    public const int DefaultInfrastructureRequeueLimit = 5;

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

    /// <summary>
    /// The ceiling on <see cref="InfrastructureRequeues"/> (§6, §9 check 7). Reaching it
    /// abandons the task — <c>canceled</c>, never <c>rejected</c>: the two counters stay
    /// separate, so a flaky machine still cannot make a task fail its criteria (§6).
    /// Fixed at creation from control-plane config, exactly like
    /// <see cref="VerificationRetryLimit"/>, so a task's terms never change under it.
    ///
    /// <para><b>Non-positive means uncapped</b> — the behaviour before the cap existed,
    /// and the deliberate opt-out for an operator who would rather a task retry forever
    /// than go terminal, in the same spirit as an unconfigured Team budget being
    /// unbounded (§9.9).</para>
    /// </summary>
    public int InfrastructureRequeueLimit { get; init; } = DefaultInfrastructureRequeueLimit;

    /// <summary>
    /// Why this task was last requeued for infrastructure reasons (§6), or null if it
    /// never was. Typed state the engine takes off the command — the same shape as
    /// <see cref="CompletionProvenance"/> — so the store persists it and the Lead
    /// (<c>get_task_report</c>, <c>get_team_state</c>) and the §12 dashboard can tell
    /// requeue causes apart instead of counting identical events (#73). On an
    /// at-cap abandonment this is the reason that ended the task.
    /// </summary>
    public LivenessLossReason? LastRequeueReason { get; init; }

    /// <summary>Failed verification verdicts. The only counter that drives rejection (§6).</summary>
    public int VerificationFailures { get; init; }

    public int VerificationRetryLimit { get; init; } = 3;

    /// <summary>The incumbent worker instance, if one is dispatched (§9 check 14).</summary>
    public WorkerInstanceId? CurrentInstance { get; init; }

    public ParkRecord? Park { get; init; }

    /// <summary>
    /// Who adjudicated this task's completion (§9 check 4), set on the
    /// verifying → completed transition and null in every other state. Typed state,
    /// not opaque content — the engine derives it from the verdict's actor — so it
    /// lands on the record and the store persists it for the §12 dashboard.
    /// </summary>
    public VerdictProvenance? CompletionProvenance { get; init; }

    /// <summary>
    /// Whether infrastructure requeues have reached this task's cap (§9 check 7) — true
    /// only on a task the cap abandoned, since a requeue that reaches the cap is the one
    /// that ends the task, so a live task's count can never exceed the limit. Always
    /// false when <see cref="InfrastructureRequeueLimit"/> is non-positive (uncapped).
    /// What distinguishes a cap abandonment from any other <c>canceled</c> task on the
    /// read side.
    /// </summary>
    public bool InfrastructureRequeuesExhausted =>
        InfrastructureRequeueLimit > 0 && InfrastructureRequeues >= InfrastructureRequeueLimit;
}
