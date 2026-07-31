using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// The persisted task row. Carries the typed state-machine fields plus the
/// opaque blobs the control plane stores and never interprets (§2 principle 1,
/// §7). Only <see cref="TaskStore"/> writes it, and only by running a
/// transition through <see cref="TaskStateMachine"/>.
/// </summary>
public sealed class TaskRow
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string Namespace { get; set; } = "";
    public CompletionMode CompletionMode { get; set; }
    public TaskState State { get; set; }
    public string? Profile { get; set; }

    public int Attempt { get; set; }
    public int InfrastructureRequeues { get; set; }
    public int VerificationFailures { get; set; }
    public int VerificationRetryLimit { get; set; }

    public Guid? CurrentInstanceId { get; set; }

    // Park record (§11); null unless the task has parked.
    public string? ParkMachine { get; set; }
    public string? ParkDirectory { get; set; }
    public string? ParkSessionRef { get; set; }
    public int? ParkAttempt { get; set; }

    /// <summary>
    /// When the task most recently entered <see cref="TaskState.BlockedOnInput"/>,
    /// or null when it is not blocked. Opaque plane plumbing captured in
    /// <see cref="TaskStore.RunTransition"/> on the RequestInput path and cleared
    /// on the way out — never an engine field, so <see cref="TaskRecord"/> stays
    /// free of clocks (§6: timers live in the control plane). The wait-TTL sweeper
    /// (§11) ages this against the configured wait TTL to decide when to park.
    /// </summary>
    public DateTimeOffset? BlockedAt { get; set; }

    // Opaque to the control plane: stored, returned, never dereferenced (§7).
    public string CompletionCriteria { get; set; } = "";

    /// <summary>
    /// The Lead's prose instructions (§7 <c>description</c>). Opaque: the worker
    /// reads it (worker-skill.md), the control plane never parses it. Captured at
    /// creation and handed back by <c>get_task</c>.
    /// </summary>
    public string Description { get; set; } = "";

    public string? Workspace { get; set; }
    public string? ResultReference { get; set; }

    /// <summary>
    /// The ambient W3C trace context (traceparent) captured when the Lead created
    /// the task. Opaque transport metadata, exactly like <see cref="ResultReference"/>:
    /// stored verbatim, never dereferenced by the control plane, never entering
    /// <c>Docket.Core</c>. Dispatch continues the Lead's trace from here so one
    /// trace spans create_task → dispatch → runner → worker. Null when no Activity
    /// was sampling at creation.
    /// </summary>
    public string? TraceContext { get; set; }

    /// <summary>
    /// The opaque harness session ref of the task's most recent work session (§11
    /// resume), stamped from a <see cref="Docket.Contracts.SessionStartedEvent"/>
    /// the moment docketd captures it. Transport metadata exactly like
    /// <see cref="ResultReference"/>/<see cref="TraceContext"/>: stored verbatim,
    /// never dereferenced, never entering <c>Docket.Core</c> — so it is set outside
    /// the state machine (a targeted column write) and survives state transitions
    /// untouched. A park copies it into the park record so redispatch can resume the
    /// transcript; distinct from <see cref="ParkSessionRef"/>, which is that park
    /// record's own snapshot. Null until the first session-init is observed.
    /// </summary>
    public string? HarnessSessionRef { get; set; }

    /// <summary>Postgres system column, used as the optimistic-concurrency token.</summary>
    public uint Version { get; set; }

    internal TaskRecord ToDomain() => new()
    {
        Id = new TaskId(Id),
        Team = new TeamId(TeamId),
        Namespace = Namespace,
        CompletionMode = CompletionMode,
        State = State,
        Profile = Profile,
        Attempt = Attempt,
        InfrastructureRequeues = InfrastructureRequeues,
        VerificationFailures = VerificationFailures,
        VerificationRetryLimit = VerificationRetryLimit,
        CurrentInstance = CurrentInstanceId is { } i ? new WorkerInstanceId(i) : null,
        Park = ParkMachine is { } m
            ? new ParkRecord(m, ParkDirectory, ParkSessionRef, ParkAttempt!.Value)
            : null,
    };

    internal void CopyFrom(TaskRecord task)
    {
        State = task.State;
        Attempt = task.Attempt;
        InfrastructureRequeues = task.InfrastructureRequeues;
        VerificationFailures = task.VerificationFailures;
        CurrentInstanceId = task.CurrentInstance?.Value;
        ParkMachine = task.Park?.Machine;
        ParkDirectory = task.Park?.Directory;
        ParkSessionRef = task.Park?.HarnessSessionRef;
        ParkAttempt = task.Park?.Attempt;
    }
}

/// <summary>
/// The token registry that makes §9 check 14 enforceable at the store: one
/// row per dispatch, revoked when the instance stops being incumbent. A
/// zombie's token is a revoked row.
/// </summary>
public sealed class WorkerInstanceRow
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// A registered live endpoint (§8.2). Rows for a task are cleared when it
/// leaves <see cref="TaskState.Working"/> (the ClearServicesAndForwards
/// effect).
/// </summary>
public sealed class RegisteredServiceRow
{
    public long Seq { get; set; }
    public Guid TaskId { get; set; }
    public Guid TeamId { get; set; }
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One HTTP-preview mapping (§8.4): the durable <c>{label → (team, task, service,
/// expiry, auth-policy)}</c> record the preview frontend resolves a subdomain
/// label against. Persisted (not in-memory like <see cref="ForwardWaiters"/>)
/// because a shareable preview URL handed to a human must survive a control-plane
/// redeploy within its TTL.
///
/// <para>The label is an opaque, unguessable random token that lives only in the
/// URL/DNS; only its SHA-256 <see cref="LabelHash"/> is stored, looked up by hash
/// exactly like every other opaque credential (§5). Structure is never encoded in
/// the label (§8.4).</para>
///
/// <para><see cref="ExpiresAt"/> gates whether a <em>new</em> browser connection
/// is admitted — mandatory and short for <see cref="PreviewAuthPolicy.Public"/>.
/// It is distinct from a forward grant's own short open-handshake TTL (§8.3): the
/// mapping expiry bounds the preview's life, the grant expiry bounds one
/// connection's tunnel-open.</para>
/// </summary>
public sealed class PreviewMappingRow
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the opaque subdomain label; the label itself is never stored (§5, §8.4).</summary>
    public string LabelHash { get; set; } = "";

    public Guid TeamId { get; set; }

    /// <summary>The task that owns the previewed service — check 11 re-verifies it is still working at connect.</summary>
    public Guid TaskId { get; set; }

    /// <summary>The registered service name the preview resolves to (§8.2).</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>Gated (default) requires a §12 operator session; public is the label-only capability (§8.4).</summary>
    public PreviewAuthPolicy AuthPolicy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the mapping stops admitting new connections (§8.4). Mandatory + short for public.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Append-only transition journal, and — since #50 — the sink for the derived
/// telemetry events that carry no state transition (§10/§12). Monotonic
/// <see cref="Seq"/> gives the per-recipient ordering the messaging layer will
/// build on; for now it is the store's own audit trail plus the §12 dashboard's
/// read model. Every row is appended in the same transaction as the
/// application-issued <c>pg_notify</c> that wakes listeners (§3.1 LISTEN/NOTIFY,
/// not a DB trigger).
///
/// <para>Most rows are a state transition (<see cref="FromState"/>/<see cref="ToState"/>
/// set, <see cref="Kind"/> the command name). The telemetry kinds — <c>auth-failed</c>
/// and <c>subagent-spawned</c> — carry no transition and populate their own typed
/// columns instead; the input-request kind rides the <c>RequestInput</c> transition
/// row's <see cref="InputKind"/>. The columns are nullable and unset off their own
/// kind, so the JSON twin stays a clean structured shape rather than a mashed
/// string (§12: every view is consumable as structured data).</para>
/// </summary>
public sealed class TaskEventRow
{
    /// <summary>The <see cref="Kind"/> of an <c>auth-failed</c> telemetry row (§11) —
    /// a wire-vocabulary name, not a command type, shared by the writer and the
    /// dashboard reader so the two never drift.</summary>
    public const string AuthFailedKind = "auth-failed";

    /// <summary>The <see cref="Kind"/> of a <c>subagent-spawned</c> telemetry row (§10/§12).</summary>
    public const string SubagentSpawnedKind = "subagent-spawned";

    public long Seq { get; set; }
    public Guid TaskId { get; set; }
    public Guid TeamId { get; set; }
    public string Kind { get; set; } = "";
    public TaskState? FromState { get; set; }
    public TaskState? ToState { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// The typed request kind of a <c>RequestInput</c> transition (§6/§11 — the
    /// <c>request_input</c> tool carries it), threaded onto the working →
    /// blocked_on_input row so the dashboard can show <em>what kind</em> of
    /// attention a task needs. Null on every non-blocking event.
    /// </summary>
    public InputRequestKind? InputKind { get; set; }

    /// <summary>
    /// The structured facts of an <c>auth-failed</c> telemetry event (§11): the
    /// operation, target, error code, and missing scope the runner reported.
    /// Persisted so the dashboard can surface them (the remediation menu itself
    /// is a later step, #54-adjacent). All null unless <see cref="Kind"/> is
    /// <c>auth-failed</c>; <see cref="AuthMissingScope"/> is null even then when
    /// the failure named no scope.
    /// </summary>
    public string? AuthOperation { get; set; }
    public string? AuthTarget { get; set; }
    public string? AuthErrorCode { get; set; }
    public string? AuthMissingScope { get; set; }

    /// <summary>
    /// The (optional) subagent lineage of a <c>subagent-spawned</c> telemetry
    /// event (§10/§12): agent id and parent agent id. Progressive enhancement —
    /// a harness that does not emit lineage leaves both null even on this kind
    /// (§10). Null entirely off the <c>subagent-spawned</c> kind.
    /// </summary>
    public string? SubagentId { get; set; }
    public string? SubagentParentId { get; set; }
}
