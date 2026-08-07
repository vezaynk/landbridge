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

    /// <summary>
    /// The cap this task's <see cref="InfrastructureRequeues"/> are judged against (§9
    /// check 7), stamped at creation from control-plane config exactly like
    /// <see cref="VerificationRetryLimit"/> — so raising the configured cap never
    /// silently changes the terms of work already in flight, and the row can say "4 of
    /// 5" without consulting configuration. Non-positive is uncapped.
    /// </summary>
    public int InfrastructureRequeueLimit { get; set; }

    /// <summary>
    /// Why this task was last requeued for infrastructure reasons (§6), carried by
    /// <see cref="CopyFrom"/> off the engine's record. The event row records every
    /// requeue's reason as history (<see cref="TaskEventRow.LivenessReason"/>); this is
    /// the <em>live</em> one — the same row-vs-event-log split
    /// <see cref="InputKind"/> makes — so <c>get_team_state</c>, <c>get_task_report</c>,
    /// and the §12 task views can say why a task keeps coming back (or, on a task the
    /// cap abandoned, why it stopped) without walking the log. Null until the first
    /// infrastructure requeue; retained afterwards.
    /// </summary>
    public LivenessLossReason? LastRequeueReason { get; set; }

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

    /// <summary>
    /// The typed kind of the task's most recent input request (§6/§11), captured on the
    /// RequestInput path beside <see cref="BlockedAt"/>. The event row already records
    /// every request's kind as history (<see cref="TaskEventRow.InputKind"/>); this is
    /// the <em>live</em> one, so the §12 human surfaces and <c>get_team_state</c> can say
    /// what kind of attention a waiting task needs without walking the event log — the
    /// same row-vs-event-log split <see cref="BlockedAt"/> already makes. Structure, not
    /// prose: it is safe on a bulk status read (§10). Null until the task first asks;
    /// retained afterwards so the last exchange stays legible.
    /// </summary>
    public InputRequestKind? InputKind { get; set; }

    /// <summary>
    /// What the worker actually asked (§10/§11) — the content half of
    /// <see cref="InputKind"/>, captured verbatim on the RequestInput transition and
    /// size-capped at the engine (<see cref="Docket.Core.RequestInput.MaxQuestionBytes"/>).
    /// Opaque: the plane stores it and never parses it (§2 principle 1). Read by the
    /// Lead per task (<c>get_task_question</c>), by a human on the §12 dashboard and
    /// inbox — where the answering happens — and by the resumed worker on
    /// <c>get_task</c>, which matters most on a cold start, where the transcript that
    /// held the question is gone. Retained past the answer so the pair stays readable;
    /// a <em>new</em> question overwrites it and clears <see cref="InputAnswer"/>.
    /// </summary>
    public string? InputQuestion { get; set; }

    /// <summary>
    /// The answer to <see cref="InputQuestion"/> (§10/§11), captured verbatim on
    /// whichever half of the one-call answer path ran — <c>AnswerInput</c> for a still
    /// blocked task, <c>WakeParked</c> for one the wait-TTL sweeper parked first — and
    /// capped at the engine (<see cref="Docket.Core.AnswerInput.MaxAnswerBytes"/>).
    /// This is how the answer reaches the redispatched worker: it surfaces on that
    /// worker's opening <c>get_task</c>, deliberately <b>not</b> through the resume
    /// argv, which would leak the text to any local process reading
    /// <c>/proc/&lt;pid&gt;/cmdline</c> (§13, the same reason tokens never ride argv).
    /// Null when nobody has answered yet, or when the answer carried no words (an
    /// <c>endpoint_wait</c> woken by a service registering).
    /// </summary>
    public string? InputAnswer { get; set; }

    // Opaque to the control plane: stored, returned, never dereferenced (§7).
    public string CompletionCriteria { get; set; } = "";

    /// <summary>
    /// The Lead's prose instructions (§7 <c>description</c>). Opaque: the worker
    /// reads it (worker-skill.md), the control plane never parses it. Captured at
    /// creation and handed back by <c>get_task</c>.
    /// </summary>
    public string Description { get; set; } = "";

    public string? Workspace { get; set; }

    /// <summary>
    /// The §8.1 artifact pointer the worker handed over on the working → verifying
    /// transition — a commit, branch, or URL saying where the finished work lives.
    /// Opaque: stored verbatim, never dereferenced, never entering <c>Docket.Core</c>
    /// (§2 principle 1). §6 <b>requires</b> it for that transition while the
    /// <see cref="WorkerReport"/> beside it stays optional, so it is the one thing every
    /// task that reached verifying has said about its output. Read back by the Lead's
    /// <c>get_task_report</c> fetch and the §12 dashboard — the §7 adjudication read
    /// (#81) — as agent-authored CLAIMS to resolve against reality, never authority.
    /// Null until the task reaches verifying.
    /// </summary>
    public string? ResultReference { get; set; }

    /// <summary>
    /// The worker's optional in-band report (§10), captured on the working →
    /// verifying transition next to <see cref="ResultReference"/> — the worker's own
    /// summary of what it did/verified plus proposals. Opaque content the plane
    /// stores verbatim and never parses (§2 principle 1); its size is capped at the
    /// engine (<see cref="Docket.Core.ReportResult.MaxReportBytes"/>). Null when the
    /// worker reported none. Surfaced to the Lead (get_team_state), a successor
    /// worker (get_task), and the §12 dashboard — agent-authored CLAIMS the Lead
    /// verifies before accepting (§13), never authority.
    /// </summary>
    public string? WorkerReport { get; set; }

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

    /// <summary>
    /// Continuation lineage (§6/§11): the prior task whose harness session this task
    /// resumes, or null for an ordinary profile-targeted task. Seeded at creation
    /// from <c>create_task(continues:)</c> and rendered as the Y-continues-X link in
    /// <c>get_team_state</c> and the dashboard task view. Opaque — the plane stores
    /// the id and never dereferences the state machine through it.
    /// </summary>
    public Guid? ContinuesTaskId { get; set; }

    /// <summary>
    /// Continuation dispatch affinity (§6/§11): the machine that last held/ran the
    /// continued task. Distinct from <see cref="ParkMachine"/> — a submitted
    /// continuation is not parked — so it does not perturb park semantics or the
    /// dashboard "parked on" signal. <see cref="TaskStore.DispatchNextAsync"/> makes
    /// this the preferred machine (park-record-style affinity): the task is claimable
    /// on this machine (and resumes there), and on another machine only under
    /// <see cref="OnMachineGone"/> = <see cref="MachineGonePolicy.Degrade"/> once this
    /// machine is gone. Cleared when a degrade cold-start abandons the session. Null
    /// for a non-continuation task.
    /// </summary>
    public string? PreferredMachine { get; set; }

    /// <summary>
    /// What to do when <see cref="PreferredMachine"/> is gone at dispatch (§6/§11):
    /// <see cref="MachineGonePolicy.Degrade"/> cold-starts elsewhere (memory lost,
    /// logged), <see cref="MachineGonePolicy.Pin"/> waits in submitted. Null (and
    /// unused) for a non-continuation task; stored as its enum name like the other
    /// enum columns.
    /// </summary>
    public MachineGonePolicy? OnMachineGone { get; set; }

    /// <summary>
    /// Who adjudicated this task's completion (§9 check 4): a Lead session or a
    /// human, set on the verifying → completed transition and null otherwise. Typed
    /// state the engine derives from the verdict actor (mirrors <see cref="CompletionMode"/>),
    /// carried by <see cref="CopyFrom"/> and rendered on the §12 dashboard task view.
    /// </summary>
    public VerdictProvenance? CompletionProvenance { get; set; }

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
        InfrastructureRequeueLimit = InfrastructureRequeueLimit,
        LastRequeueReason = LastRequeueReason,
        VerificationFailures = VerificationFailures,
        VerificationRetryLimit = VerificationRetryLimit,
        CurrentInstance = CurrentInstanceId is { } i ? new WorkerInstanceId(i) : null,
        Park = ParkMachine is { } m
            ? new ParkRecord(m, ParkDirectory, ParkSessionRef, ParkAttempt!.Value)
            : null,
        CompletionProvenance = CompletionProvenance,
    };

    internal void CopyFrom(TaskRecord task)
    {
        State = task.State;
        Attempt = task.Attempt;
        InfrastructureRequeues = task.InfrastructureRequeues;
        // The reason follows the counter it explains. The cap itself is deliberately not
        // copied back: it is written once at creation and the engine only ever reads it,
        // so the row stays the authority on this task's terms.
        LastRequeueReason = task.LastRequeueReason;
        VerificationFailures = task.VerificationFailures;
        CurrentInstanceId = task.CurrentInstance?.Value;
        ParkMachine = task.Park?.Machine;
        ParkDirectory = task.Park?.Directory;
        ParkSessionRef = task.Park?.HarnessSessionRef;
        ParkAttempt = task.Park?.Attempt;
        CompletionProvenance = task.CompletionProvenance;
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

    /// <summary>
    /// The machine this dispatch ran on — the durable answer to "where do this task's
    /// transcripts live" (§12). One row per dispatch is exactly the right grain: a task
    /// requeued across two machines has an instance row for each, and transcript ordinals
    /// are per-machine, so the pair (machine, ordinal) is what identifies a captured
    /// instance. Nothing else remembers this once the task is terminal: the in-memory
    /// registry untracks a task when it exits, <see cref="TaskRow.ParkMachine"/> only
    /// covers parked tasks, and <see cref="TaskRow.PreferredMachine"/> only continuations.
    ///
    /// <para>Nullable for rows written before this column existed; a null simply means the
    /// plane cannot say where that attempt ran, and the dashboard says so rather than
    /// guessing.</para>
    /// </summary>
    public string? MachineId { get; set; }
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

    /// <summary>
    /// The <see cref="Kind"/> of a <c>continuation-memory-lost</c> telemetry row
    /// (§6/§11): a continuation task's preferred machine was gone at dispatch and
    /// <see cref="MachineGonePolicy.Degrade"/> cold-started it elsewhere, so the
    /// resumed transcript — its conversational memory — was lost. Carries no state
    /// transition; the machine facts ride <see cref="Detail"/> so the Lead can see
    /// what happened. Written in the dispatch transaction so it commits atomically
    /// with the cold-start.
    /// </summary>
    public const string ContinuationMemoryLostKind = "continuation-memory-lost";

    /// <summary>
    /// The <see cref="Kind"/> of a <c>budget-exhausted-stop</c> telemetry row (§9.9):
    /// the containment sweep found this task's Team over its ceiling and sent the
    /// runner a <c>stop</c>. Carries no state transition — the stop's own wind-down
    /// produces whatever transition follows — so the row exists to answer the question
    /// the dashboard otherwise cannot: <em>why</em> a Team stopped making progress when
    /// nothing failed. Written only once delivery to the machine succeeded, so it is
    /// also the sweep's idempotency record: a task that already has one is not swept
    /// again.
    /// </summary>
    public const string BudgetExhaustedStopKind = "budget-exhausted-stop";

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
    /// Which signal drove a <c>LivenessLost</c> transition (§6, §9 check 7) — the two
    /// liveness clocks, a process exit, a reboot, an undelivered dispatch — threaded onto
    /// the row exactly like <see cref="InputKind"/>. This is the fix for the half of #73
    /// that made a requeue loop hard to read: the reason was computed at requeue time and
    /// thrown away, so the §12 event log showed N identical rows. With it, the row's
    /// <see cref="ToState"/> also says which outcome this requeue took —
    /// <c>Submitted</c> for a redispatch, <c>Canceled</c> for the one that reached the
    /// cap. Null on every non-requeue event.
    /// </summary>
    public LivenessLossReason? LivenessReason { get; set; }

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

/// <summary>
/// Bytes a Team has moved through relay forwards, spec §9 check 10 / §9.10 — <b>accounting,
/// not enforcement</b>.
///
/// <para>Deliberately its own table rather than columns on <see cref="TeamBudgetRow"/>: the
/// budget row is about authorization Docket <em>granted</em>, and byte volume is the one
/// quantity here that is actually <em>measured</em>. Mixing them would put a measured number
/// next to the committed ceiling and invite exactly the confusion §9's as-built note exists to
/// prevent — and would mean a Team acquired a "budget" merely because bytes flowed.</para>
///
/// <para><b>Nothing is enforced on this.</b> No ceiling is checked against it, because §8.3
/// forbids severing an established splice mid-flight, so what a reached byte ceiling should
/// actually do is an unresolved design question. What this gives a human is visibility — the
/// §12 Team view's byte burn — and it is honestly <b>best-effort</b>: the relay reports
/// asynchronously, so a relay that dies loses its unreported tail. A containment signal, never
/// an invoice.</para>
///
/// <para><see cref="ForwardedBytes"/> only ever increases, like
/// <see cref="TeamBudgetRow.CommittedUsd"/> and for a simpler reason: bytes already moved
/// cannot un-move.</para>
/// </summary>
public sealed class TeamForwardUsageRow
{
    public Guid TeamId { get; set; }

    /// <summary>Total bytes spliced in both directions across all of the Team's forwards.
    /// Both directions summed, because an allowance would bound traffic moved rather than
    /// distinguish ingress from egress.</summary>
    public long ForwardedBytes { get; set; }

    /// <summary>When the last report landed — the honesty marker on a best-effort figure, so a
    /// reader can see how stale it is rather than trusting it as current.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A Team's spend ceiling and what has been authorized against it (spec §9 check 9,
/// §9.9). One row per Team — the first thing in the schema that makes a Team an
/// entity rather than just a <c>TeamId</c> on other rows.
///
/// <para><b>This is committed authorization, not measured spend.</b> Nothing in the
/// system ingests token/cost telemetry today (§10 describes the intent; no OTLP
/// receiver exists), so there is no consumption figure to accumulate. What is
/// knowable is what Docket <em>authorized</em>: every dispatch hands its harness a
/// hard per-dispatch cap, so committing that cap bounds exposure without measuring
/// anything. §9.9 calls check 9 "containment, not metering" — a reservation is
/// containment, and unlike metering it cannot be defeated by a signal that never
/// arrives.</para>
///
/// <para><b><see cref="CommittedUsd"/> only ever increases.</b> It is
/// authorized-spend-to-date for the Team's whole life, deliberately worst-case: a
/// task that spent a penny of its cap still consumes the whole cap of ceiling. It is
/// not released when a task finishes, because "unspent" is precisely the quantity
/// that cannot be known — and releasing would turn a lifetime ceiling into a
/// concurrency limiter, letting a $100 Team run ten thousand sequential $10 tasks
/// (§4: a Team owns a budget and terminates). The one exception is a task cancelled
/// before it was ever dispatched: no process ran, so no spend was possible, and
/// holding that commitment would be a lie in the other direction. The escape valve
/// is a human raising the ceiling, which is the control this is for.</para>
///
/// <para><b>Written only by a human.</b> A Lead that could raise its own ceiling is
/// enforcement living exactly where a model can reason past it (§2 principle 3), so
/// the write path is the §12 dashboard and there is deliberately no MCP tool. A Lead
/// may <em>read</em> it (<c>get_team_state</c>) — seeing your own ceiling is how you
/// plan, and how you explain why dispatch stopped.</para>
/// </summary>
public sealed class TeamBudgetRow
{
    /// <summary>The Team this ceiling governs; the primary key (one row per Team).</summary>
    public Guid TeamId { get; set; }

    /// <summary>The Team's lifetime ceiling in USD. Null means no ceiling is set, which
    /// is the unconfigured default and admits work — a Docket with no budgets
    /// configured behaves exactly as it did before this existed.</summary>
    public decimal? CeilingUsd { get; set; }

    /// <summary>The hard cap handed to each dispatch's harness (<c>{budget}</c> in the
    /// profile's spawn argv, §10). Also the unit committed per dispatch. Null means no
    /// per-dispatch cap is passed, and the ceiling then has no backstop (§9.9).</summary>
    public decimal? PerTaskUsd { get; set; }

    /// <summary>Authorized-spend-to-date: the sum of every per-dispatch cap this Team
    /// has ever been granted. Monotonic (see the type remarks).</summary>
    public decimal CommittedUsd { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
