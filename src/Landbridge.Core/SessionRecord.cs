namespace Landbridge.Core;

/// <summary>
/// Written when a task parks, spec §11: the machine redispatch should prefer, because
/// harness transcripts are machine-local. Opaque to the control plane, which stores and
/// returns it and never dereferences it.
///
/// <para><b>The machine is all that is left, and all there ever was.</b> This record once
/// also carried a working directory, a harness session ref, and an attempt number. None of
/// the three survived contact with where those facts actually live: the directory never had
/// a producer at all (the plane cannot observe a machine's filesystem layout, and §11's
/// directory inheritance is expressed as a task id instead — see
/// <see cref="SessionRecord.Park"/>'s callers and the work-dir task on the dispatch command);
/// and the session ref and attempt were copied out of the live <c>tasks</c> columns on the
/// way in and written to park-specific columns on the way out that nothing ever read back.
/// Redispatch reads the live <c>harness_session_ref</c> and <c>attempt</c>, which is why the
/// snapshots could drift from them without anyone noticing. So the park record is one fact,
/// stated once.</para>
/// </summary>
public sealed record ParkRecord(string Machine);

/// <summary>
/// What dispatch needs to know about a machine, as reported by its runner.
/// Ready/back-pressure are derived by landbridged (§10); profiles are declared
/// names the control plane never interprets.
/// </summary>
public sealed record MachineSnapshot(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    IReadOnlySet<string> DeclaredProfiles);

/// <summary>
/// The typed task record, spec §7. Prose fields (description, result_summary,
/// blocker_note) and the opaque workspace blob live at the storage layer;
/// nothing here requires interpreting them.
/// </summary>
public sealed record SessionRecord
{
    /// <summary>
    /// The default infrastructure requeue cap (§9 check 7). Five is enough that a task
    /// survives an ordinary bad patch — a machine rebooting, a redeploy, one flaky
    /// dispatch — and small enough that a genuinely wedged task stops spending inside an
    /// hour rather than forever: each attempt costs a whole no-progress ceiling (30 min by
    /// default) of a model's time before the plane reclaims it.
    /// </summary>
    public const int DefaultInfrastructureRequeueLimit = 5;

    public required SessionId Id { get; init; }
    public required TeamId Team { get; init; }

    /// <summary>Server-assigned team-{id}/session-{id}; uniqueness is structural (§9 check 2).</summary>
    public required string Namespace { get; init; }

    public SessionState State { get; init; } = SessionState.Submitted;

    public Occupancy OccupancyDesired { get; init; } = Occupancy.Running;
    public Occupancy OccupancyObserved { get; init; } = Occupancy.None;
    public SessionHealth Health { get; init; } = SessionHealth.Ok;
    public bool Hidden { get; init; }
    public MessageState MessageState { get; init; } = MessageState.Idle;
    public MessageVerdict? MessageVerdict { get; init; }

    /// <summary>
    /// Id of the outstanding envelope, null iff <see cref="MessageState"/> is
    /// <see cref="MessageState.Idle"/>. MCP Tasks use this as <c>taskId</c>.
    /// </summary>
    public Guid? MessageId { get; init; }

    /// <summary>Most recently closed envelope, for <c>tasks/get</c> after idle.</summary>
    public Guid? LastMessageId { get; init; }

    public MessageTerminal? LastMessageTerminal { get; init; }
    public PendingSpawn? PendingSpawn { get; init; } = Landbridge.Core.PendingSpawn.New;
    public bool PullRedelivered { get; init; }

    /// <summary>Required runner profile name; exact-match routing, never interpreted (§7).</summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Dispatches so far, incremented on every dispatch (§7). Visible to the
    /// worker so a successor knows it may inherit a dirty workspace.
    /// </summary>
    public int Attempt { get; init; }

    /// <summary>Requeues from ack/liveness/reboot. Never drives rejection (§6).</summary>
    public int InfrastructureRequeues { get; init; }

    /// <summary>
    /// Observability ceiling on <see cref="InfrastructureRequeues"/>. The cap does
    /// not auto-requeue and does not cancel; every <c>LivenessLost</c> lands
    /// <c>health=failed</c> and stops. Non-positive means uncapped as a counter.
    /// </summary>
    public int InfrastructureRequeueLimit { get; init; } = DefaultInfrastructureRequeueLimit;

    /// <summary>
    /// Why this task was last requeued for infrastructure reasons (§6), or null if it
    /// never was. Typed state the engine takes off the command — the same shape as
    /// <see cref="CompletionProvenance"/> — so the store persists it and the Lead
    /// (<c>get_session_report</c>, <c>get_team_state</c>) and the §12 dashboard can tell
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
    /// Who closed this session (§9 check 4), set on submit_review and null
    /// until then. Typed state,
    /// not opaque content — the engine derives it from the verdict's actor — so it
    /// lands on the record and the store persists it for the §12 dashboard.
    /// </summary>
    public VerdictProvenance? CompletionProvenance { get; init; }

    /// <summary>
    /// Whether the observability counter has reached its ceiling. Does not change
    /// occupancy or health by itself.
    /// </summary>
    public bool InfrastructureRequeuesExhausted =>
        InfrastructureRequeueLimit > 0 && InfrastructureRequeues >= InfrastructureRequeueLimit;

    /// <summary>
    /// Derived <see cref="SessionState"/> for unconverted readers. Hidden/verdict
    /// before health so a fail-during-stop cannot clobber accept/discard.
    /// </summary>
    public static SessionState DeriveState(SessionRecord t)
    {
        if (t.Hidden && t.MessageVerdict == Landbridge.Core.MessageVerdict.Accepted)
            return SessionState.Completed;
        if (t.Hidden && t.MessageVerdict == Landbridge.Core.MessageVerdict.Discarded)
            return SessionState.Rejected;
        if (t.Hidden)
            return SessionState.Canceled;
        if (t.Health == SessionHealth.Failed)
            return SessionState.Failed;
        if (t.OccupancyDesired == Occupancy.OnDisk)
            return SessionState.Parked;
        if (t.MessageState == MessageState.AwaitingPermission)
            return SessionState.BlockedOnInput;
        if (t.OccupancyDesired == Occupancy.Running
            && t.OccupancyObserved == Occupancy.None
            && t.CurrentInstance is null
            && t.Health == SessionHealth.Ok)
            return SessionState.Submitted;
        return SessionState.Working;
    }
}
