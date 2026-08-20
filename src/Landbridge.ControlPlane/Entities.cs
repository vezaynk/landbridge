using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// The persisted task row. Carries the typed state-machine fields plus the
/// opaque blobs the control plane stores and never interprets (§2 principle 1,
/// §7). Only <see cref="SessionStore"/> writes it, and only by running a
/// transition through <see cref="SessionStateMachine"/>.
/// </summary>
public sealed class SessionRow
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string Namespace { get; set; } = "";
    public SessionState State { get; set; }
    public Occupancy OccupancyDesired { get; set; } = Occupancy.Running;
    public Occupancy OccupancyObserved { get; set; } = Occupancy.None;
    public SessionHealth Health { get; set; } = SessionHealth.Ok;
    public bool Hidden { get; set; }
    public MessageState MessageState { get; set; } = MessageState.Idle;
    public MessageVerdict? MessageVerdict { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? LastMessageId { get; set; }
    public MessageTerminal? LastMessageTerminal { get; set; }
    public DateTimeOffset? MessageOpenedAt { get; set; }
    public DateTimeOffset? LastMessageClosedAt { get; set; }
    public PendingSpawn? PendingSpawn { get; set; } = Landbridge.Core.PendingSpawn.New;
    public bool PullRedelivered { get; set; }
    public DateTimeOffset? MessagePulledAt { get; set; }
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
    /// requeue's reason as history (<see cref="SessionEventRow.LivenessReason"/>); this is
    /// the <em>live</em> one — the same row-vs-event-log split
    /// <see cref="InputKind"/> makes — so <c>get_team_state</c>, <c>get_session_report</c>,
    /// and the §12 task views can say why a task keeps coming back (or, on a task the
    /// cap abandoned, why it stopped) without walking the log. Null until the first
    /// infrastructure requeue; retained afterwards.
    /// </summary>
    public LivenessLossReason? LastRequeueReason { get; set; }

    public Guid? CurrentInstanceId { get; set; }

    /// <summary>
    /// The park record (§11), null unless the task has parked: the machine redispatch
    /// prefers. One column, because the machine is the whole record — the directory,
    /// session-ref and attempt columns that used to sit here were written from the live
    /// <see cref="HarnessSessionRef"/> / <see cref="Attempt"/> columns (or, for the
    /// directory, never written at all) and read back by nothing.
    /// </summary>
    public string? ParkMachine { get; set; }

    /// <summary>
    /// When the task most recently entered <see cref="SessionState.BlockedOnInput"/>,
    /// or null when it is not blocked. Opaque plane plumbing captured in
    /// <see cref="SessionStore.RunTransition"/> on the RequestInput path and cleared
    /// on the way out — never an engine field, so <see cref="SessionRecord"/> stays
    /// free of clocks (§6: timers live in the control plane). The wait-TTL sweeper
    /// (§11) ages this against the configured wait TTL to decide when to park.
    /// </summary>
    public DateTimeOffset? BlockedAt { get; set; }

    /// <summary>
    /// The typed kind of the task's most recent input request (§6/§11), captured on the
    /// RequestInput path beside <see cref="BlockedAt"/>. The event row already records
    /// every request's kind as history (<see cref="SessionEventRow.InputKind"/>); this is
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
    /// size-capped at the engine (<see cref="Landbridge.Core.RequestInput.MaxQuestionBytes"/>).
    /// Opaque: the plane stores it and never parses it (§2 principle 1). Read by the
    /// Lead per task (<c>get_session_question</c>), by a human on the §12 dashboard and
    /// inbox — where the answering happens — and by the resumed worker on
    /// <c>get_session</c>, which matters most on a cold start, where the transcript that
    /// held the question is gone. Retained past the answer so the pair stays readable;
    /// a <em>new</em> question overwrites it and clears <see cref="InputAnswer"/>.
    /// </summary>
    public string? InputQuestion { get; set; }

    /// <summary>
    /// The answer to <see cref="InputQuestion"/> (§10/§11), captured verbatim on
    /// whichever half of the one-call answer path ran — <c>AnswerInput</c> for a still
    /// blocked task, <c>WakeParked</c> for one the wait-TTL sweeper parked first — and
    /// capped at the engine (<see cref="Landbridge.Core.AnswerInput.MaxAnswerBytes"/>).
    /// This is how the answer reaches the redispatched worker: it surfaces on that
    /// worker's opening <c>get_session</c>, deliberately <b>not</b> through the resume
    /// argv, which would leak the text to any local process reading
    /// <c>/proc/&lt;pid&gt;/cmdline</c> (§13, the same reason tokens never ride argv).
    /// Null when nobody has answered yet, or when the answer carried no words (an
    /// <c>endpoint_wait</c> woken by a service registering).
    /// </summary>
    public string? InputAnswer { get; set; }

    /// <summary>
    /// The harness tool a pending <see cref="InputRequestKind.Permission"/> request is
    /// asking about (§11 permission bridge) — the tool name the harness relayed, e.g.
    /// <c>Bash</c>. The proposed tool input rides <see cref="InputQuestion"/> beside it, so
    /// the pair is "which tool" plus "with what", and both are agent-authored strings the
    /// plane stores verbatim and never parses (§2 principle 1). Required by the engine on
    /// that kind (<see cref="Rule.PermissionRequestNamesItsTool"/>) and null for every
    /// other kind. Read by the §12 inbox and the Lead's per-task fetch, where the deciding
    /// happens.
    /// </summary>
    public string? PermissionTool { get; set; }

    /// <summary>
    /// The ACP <c>options</c> array JSON for a pending permission request, stored
    /// verbatim so a Lead or human can pick one of the harness's own buttons.
    /// Null when the request offered none (legacy MCP prompt-tool). Cleared by
    /// a new request the same way <see cref="PermissionTool"/> is.
    /// </summary>
    public string? PermissionOptions { get; set; }

    /// <summary>
    /// The <c>optionId</c> a Lead or human selected, or null while the request
    /// is undecided or when the answer was a legacy allow/deny with no list.
    /// The relaying worker reads this so landbridged can return that id to the
    /// agent instead of mapping a binary verdict onto <c>allow_once</c>.
    /// </summary>
    public string? PermissionOptionId { get; set; }

    /// <summary>
    /// The verdict a pending permission request was decided with (§11), or null while it is
    /// still undecided. This is the field the relaying worker tool polls: it blocks inside
    /// its own tool call until this lands (or until the wait-TTL sweeper parks the task out
    /// from under it), then translates it into the harness's permission result. A note
    /// beside the verdict rides the AnswerPermission event, not
    /// <see cref="InputAnswer"/> — that column is the Lead's prose to the worker.
    /// Cleared, with the escalation fields, by a new request.
    /// </summary>
    public PermissionVerdict? PermissionVerdict { get; set; }

    /// <summary>
    /// When a pending permission request was marked human-only (§11), or null if it was
    /// not. While this is set a Lead is refused
    /// (<see cref="Rule.EscalatedPermissionIsHumanOnly"/>) and only a human can decide the
    /// request. Deliberately <em>not</em> a reset of <see cref="BlockedAt"/>: escalating
    /// does not extend the wait deadline, so an escalation nobody picks up still parks on
    /// schedule rather than waiting forever.
    /// </summary>
    public DateTimeOffset? PermissionEscalatedAt { get; set; }

    /// <summary>
    /// Why the request was escalated (§11), required by the engine whenever
    /// <see cref="PermissionEscalatedAt"/> is set
    /// (<see cref="Rule.PermissionEscalationCarriesReason"/>). Lead-authored prose the
    /// plane stores verbatim; the §12 inbox renders it beside the request so the human
    /// inherits the concern along with the decision.
    /// </summary>
    public string? PermissionEscalationReason { get; set; }

    /// <summary>
    /// The Lead's prose instructions (§7 <c>description</c>). Opaque: the worker
    /// reads it (worker-skill.md), the control plane never parses it. Captured at
    /// creation and handed back by <c>get_session</c>.
    /// </summary>
    public string Description { get; set; } = "";

    public string? Workspace { get; set; }

    /// <summary>
    /// The §8.1 pointer the worker handed over on <c>report_result</c> — a commit,
    /// branch, or URL saying where the work lives. Opaque: stored verbatim, never
    /// dereferenced, never entering <c>Landbridge.Core</c> (§2 principle 1). §6
    /// <b>requires</b> it on a report while the <see cref="WorkerReport"/> beside it
    /// stays optional. Read back by the Lead's <c>get_session_report</c> fetch and
    /// the §12 dashboard (#81) as agent-authored CLAIMS, never authority.
    /// Null until a report.
    /// </summary>
    public string? ResultReference { get; set; }

    /// <summary>
    /// The worker's optional in-band report (§10), captured on <c>report_result</c>
    /// next to <see cref="ResultReference"/> — the worker's own summary plus proposals.
    /// Opaque content the plane stores verbatim and never parses (§2 principle 1); its
    /// size is capped at the engine (<see cref="Landbridge.Core.ReportResult.MaxReportBytes"/>).
    /// Null when the worker reported none. Surfaced to the Lead (get_team_state), a
    /// successor worker (get_session), and the §12 dashboard — agent-authored CLAIMS
    /// (§13), never authority.
    /// </summary>
    public string? WorkerReport { get; set; }

    /// <summary>
    /// The ambient W3C trace context (traceparent) captured when the Lead created
    /// the task. Opaque transport metadata, exactly like <see cref="ResultReference"/>:
    /// stored verbatim, never dereferenced by the control plane, never entering
    /// <c>Landbridge.Core</c>. Dispatch continues the Lead's trace from here so one
    /// trace spans create_session → dispatch → runner → worker. Null when no Activity
    /// was sampling at creation.
    /// </summary>
    public string? TraceContext { get; set; }

    /// <summary>
    /// The opaque harness session ref of the task's most recent work session (§11
    /// resume), stamped from a <see cref="Landbridge.Contracts.SessionStartedEvent"/>
    /// the moment landbridged captures it. Transport metadata exactly like
    /// <see cref="ResultReference"/>/<see cref="TraceContext"/>: stored verbatim,
    /// never dereferenced, never entering <c>Landbridge.Core</c> — so it is set outside
    /// the state machine (a targeted column write) and survives state transitions
    /// untouched. This is the <em>only</em> record of it: a park used to snapshot it into a
    /// park-specific column too, which nothing read back, so redispatch resumes from this
    /// column whether the task parked or not. Null until the first session-init is observed.
    /// </summary>
    public string? HarnessSessionRef { get; set; }

    /// <summary>
    /// Continuation lineage (§6/§11): the prior task whose harness session this task
    /// resumes, or null for an ordinary profile-targeted task. Seeded at creation
    /// from <c>create_session(continues:)</c> and rendered as the Y-continues-X link in
    /// <c>get_team_state</c> and the dashboard task view. Opaque — the plane stores
    /// the id and never dereferences the state machine through it.
    /// </summary>
    public Guid? ContinuesSessionId { get; set; }

    /// <summary>
    /// The task whose machine-local work dir this task's harness runs in (§7, §11), or null
    /// when that is this task's own. Set for a continuation and nothing else; rides every
    /// dispatch as <see cref="Landbridge.Contracts.DispatchCommand.WorkDirSession"/>.
    ///
    /// <para><b>A property of continuation itself, not of resume.</b> A continuation works
    /// where its predecessor worked whether or not it resumes that transcript, because the
    /// workspace is the work: a cold-started continuation still needs the worktree and
    /// artifacts the predecessor left. So this is <em>not</em> suppressed or cleared when a
    /// session is not being resumed — including a degrade cold-start, which keeps it so the
    /// task's directory stays the same across every attempt. Transcript resume additionally
    /// requires it (a session is directory-local), but does not define it.</para>
    ///
    /// <para>Distinct from <see cref="ContinuesSessionId"/>, which is <em>lineage</em>, and it
    /// has to be: lineage names the immediate predecessor, while a chain (a continuation of
    /// a continuation — §11 calls chains natural) does not create a directory per link. B
    /// continuing A works in A's dir, so B has no dir of its own, and C continuing B works
    /// in A's too though its lineage names B. Seeded transitively at creation (the source's
    /// own value, else the source itself), which also makes it O(1) at dispatch rather than
    /// a walk up the lineage.</para>
    /// </summary>
    public Guid? WorkDirSessionId { get; set; }

    /// <summary>
    /// Continuation dispatch affinity (§6/§11): the machine that last held/ran the
    /// continued task. Distinct from <see cref="ParkMachine"/> — a submitted
    /// continuation is not parked — so it does not perturb park semantics or the
    /// dashboard "parked on" signal. <see cref="SessionStore.DispatchNextAsync"/> makes
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
    /// unused) until a park or fail pins the same-task <c>session/load</c> to the
    /// box that holds the transcript. Stored as its enum name like the other
    /// enum columns.
    /// </summary>
    public MachineGonePolicy? OnMachineGone { get; set; }

    /// <summary>
    /// Who closed this session (§9 check 4): a Lead session or a human, set on
    /// <c>submit_review</c> and null otherwise. Typed state the engine derives from
    /// the actor, carried by <see cref="CopyFrom"/> and rendered on the §12 dashboard.
    /// </summary>
    public VerdictProvenance? CompletionProvenance { get; set; }

    /// <summary>Postgres system column, used as the optimistic-concurrency token.</summary>
    public uint Version { get; set; }

    internal SessionRecord ToDomain() => new()
    {
        Id = new SessionId(Id),
        Team = new TeamId(TeamId),
        Namespace = Namespace,
        State = State,
        OccupancyDesired = OccupancyDesired,
        OccupancyObserved = OccupancyObserved,
        Health = Health,
        Hidden = Hidden,
        MessageState = MessageState,
        MessageVerdict = MessageVerdict,
        MessageId = MessageId,
        LastMessageId = LastMessageId,
        LastMessageTerminal = LastMessageTerminal,
        PendingSpawn = PendingSpawn,
        PullRedelivered = PullRedelivered,
        Profile = Profile,
        Attempt = Attempt,
        InfrastructureRequeues = InfrastructureRequeues,
        InfrastructureRequeueLimit = InfrastructureRequeueLimit,
        LastRequeueReason = LastRequeueReason,
        VerificationFailures = VerificationFailures,
        VerificationRetryLimit = VerificationRetryLimit,
        CurrentInstance = CurrentInstanceId is { } i ? new WorkerInstanceId(i) : null,
        Park = ParkMachine is { } m ? new ParkRecord(m) : null,
        CompletionProvenance = CompletionProvenance,
    };

    internal void CopyFrom(SessionRecord task)
    {
        State = task.State;
        OccupancyDesired = task.OccupancyDesired;
        OccupancyObserved = task.OccupancyObserved;
        Health = task.Health;
        Hidden = task.Hidden;
        MessageState = task.MessageState;
        MessageVerdict = task.MessageVerdict;
        MessageId = task.MessageId;
        LastMessageId = task.LastMessageId;
        LastMessageTerminal = task.LastMessageTerminal;
        PendingSpawn = task.PendingSpawn;
        PullRedelivered = task.PullRedelivered;
        Attempt = task.Attempt;
        InfrastructureRequeues = task.InfrastructureRequeues;
        // The reason follows the counter it explains. The cap itself is deliberately not
        // copied back: it is written once at creation and the engine only ever reads it,
        // so the row stays the authority on this task's terms.
        LastRequeueReason = task.LastRequeueReason;
        VerificationFailures = task.VerificationFailures;
        CurrentInstanceId = task.CurrentInstance?.Value;
        ParkMachine = task.Park?.Machine;
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
    public Guid SessionId { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this instance stopped being the incumbent. Written but read by nothing —
    /// <see cref="Revoked"/> is what §9 check 14 predicates on. Kept for §13 forensics.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The machine this dispatch ran on — the durable answer to "where do this task's
    /// transcripts live" (§12). One row per dispatch is exactly the right grain: a task
    /// requeued across two machines has an instance row for each, and transcript ordinals
    /// are per-machine, so the pair (machine, ordinal) is what identifies a captured
    /// instance. Nothing else remembers this once the task is terminal: the in-memory
    /// registry untracks a task when it exits, <see cref="SessionRow.ParkMachine"/> only
    /// covers parked tasks, and <see cref="SessionRow.PreferredMachine"/> is the
    /// continuation pin plus the same-task session/load pin after park or fail.
    ///
    /// <para>Nullable for rows written before this column existed; a null simply means the
    /// plane cannot say where that attempt ran, and the dashboard says so rather than
    /// guessing.</para>
    /// </summary>
    public string? MachineId { get; set; }
}

/// <summary>
/// A registered live endpoint (§8.2). Rows for a task are cleared when it
/// leaves <see cref="SessionState.Working"/> (the ClearServicesAndForwards
/// effect) — with one exception: a task blocked on a <b>permission</b> request
/// keeps its registrations, because that worker is still alive inside its tool
/// call and returns to <see cref="SessionState.Working"/> as the same incumbent
/// (§11 permission bridge). It keeps them through a subsequent park or requeue
/// too, since the clearing effect is only ever emitted from
/// <see cref="SessionState.Working"/>.
///
/// <para><c>(TeamId, Name)</c> is <b>unique</b>: the name is the Team-scoped address every
/// resolver is handed, so one live row may hold it (<see cref="Rule.ServiceNameUniqueInTeam"/>,
/// enforced by the index and by <c>SessionStore.RegisterServiceAsync</c>, which updates a task's
/// own row and refuses another task's). Since a row exists only while its task is working, a
/// finished task's name is free for the next one.</para>
/// </summary>
public sealed class RegisteredServiceRow
{
    public long Seq { get; set; }
    public Guid SessionId { get; set; }
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

    /// <summary>
    /// The task that owns the previewed service, and <b>half of what the label resolves
    /// to</b>: <see cref="PreviewConnectService"/> mints against <c>(SessionId, ServiceName)</c>,
    /// so check 11 re-verifies <em>this</em> registration is still working at connect rather
    /// than accepting whatever else answers to the name in the Team by then. Resolving by
    /// name alone let a label outlive its subject and reach a different task's service.
    /// </summary>
    public Guid SessionId { get; set; }

    /// <summary>The registered service name the preview resolves to (§8.2).</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>Gated (default) requires a §12 operator session; public is the label-only capability (§8.4).</summary>
    public PreviewAuthPolicy AuthPolicy { get; set; }

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
public sealed class SessionEventRow
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

    public long Seq { get; set; }
    public Guid SessionId { get; set; }
    public Guid TeamId { get; set; }
    public string Kind { get; set; } = "";
    public SessionState? FromState { get; set; }
    public SessionState? ToState { get; set; }
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

    /// <summary>
    /// The audit trail of §11's permission bridge, threaded onto the deciding transition
    /// exactly like <see cref="InputKind"/> and <see cref="LivenessReason"/>: which way the
    /// request went and <em>who</em> had the authority to send it there. Every permission
    /// decision leaves one of these rows, so the event log answers after the fact whether a
    /// tool call was approved routinely by a Lead or deliberately by a person — the question
    /// an operator asks first when a worker turns out to have done something surprising.
    /// The decision's message rides <see cref="Detail"/>. Both null on every event that is
    /// not an <c>AnswerPermission</c> transition.
    /// </summary>
    public PermissionVerdict? PermissionVerdict { get; set; }
    public PermissionAnswerer? PermissionAnswerer { get; set; }
}

/// <summary>
/// What a task's harness said it consumed, per model (§10 telemetry ingest, §12 measured
/// view) — <b>measured and reported, enforced on by nothing</b>.
///
/// <para><b>The harness's claim, kept as such.</b> Every column here was computed by the
/// harness and relayed through <c>landbridged</c> verbatim; the plane sums rows to aggregate and
/// does no other arithmetic. That is why §12 renders this in a section visually separated from
/// the wire-derived facts beside it (§2 principle 2): a reader must be able to tell what the
/// plane observed from what a worker told it, and the same pixel treatment would erase the
/// distinction that makes one of them trustworthy.</para>
///
/// <para><b>Keyed per (task, model), and the model may be null.</b> One dispatch legitimately
/// spans several models — a Claude worker whose subagents ran on a cheaper one reports each
/// separately — so the model is part of the key rather than a column on a per-task row.
/// <see cref="Model"/> is null when the harness named none, which is a real state and not a
/// placeholder — and nothing but the harness may fill it (see
/// <see cref="UsageReportedEvent"/>).</para>
///
/// <para><b>Counters only rise, and that is what makes a dropped report harmless.</b> Reports
/// are cumulative-to-date, so an upsert keeps the high-water mark and §10's best-effort ring
/// may drop any number of them without the total going backwards. The trade is the tail: a
/// worker killed between its last report and its exit leaves its final tokens uncounted, so
/// this figure trails reality and <see cref="ReportedAt"/> is how a reader sees by how much —
/// the same honesty marker, for the same reason, as §9.10's relay bytes.</para>
///
/// <para><b><see cref="CostUsd"/> is null unless the harness stated a cost.</b> Claude does;
/// Codex states none anywhere. Nothing here derives dollars from tokens — a derived figure is
/// a different kind of claim from a reported one, and <see cref="ModelPricing"/> exists to
/// keep that boundary visible rather than to quietly fill this column in.</para>
///
/// <para><see cref="ReasoningOutputTokens"/> is a portion OF <see cref="OutputTokens"/>, never
/// an addition to it (Codex breaks it out, Claude's stream does not expose one). It is stored
/// because it is free and a real cost lever, and it is excluded from every total on purpose.</para>
/// </summary>
public sealed class SessionUsageRow
{
    /// <summary>The task whose dispatch reported this. Half the composite key.</summary>
    public Guid SessionId { get; set; }

    /// <summary>The model the HARNESS named for these tokens, or null when it named none — the
    /// other half of the key. Opaque: the plane never parses or validates it, so a harness
    /// naming models in any scheme it likes needs no change here.</summary>
    public string? Model { get; set; }

    /// <summary>The Team, denormalized off the task so the §12 Team roll-up is one indexed
    /// query rather than a join per row. Written once with the row and never updated — a task
    /// does not change Teams.</summary>
    public Guid TeamId { get; set; }

    /// <summary>Uncached prompt tokens. Disjoint from the two cache columns — for a harness
    /// that reports its cache hits inside its input count, landbridged subtracted before this
    /// arrived (see <see cref="UsageReportedEvent"/>).</summary>
    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>Prompt tokens served from cache — the cheap ones, and the reason a cache-heavy
    /// worker's real spend is legible instead of averaged away.</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Prompt tokens written INTO the cache — the expensive ones, paid once so the
    /// reads above can be cheap.</summary>
    public long CacheWriteTokens { get; set; }

    /// <summary>The reasoning portion of <see cref="OutputTokens"/>, where the harness breaks
    /// one out. Null when unreported, which is every Claude row.</summary>
    public long? ReasoningOutputTokens { get; set; }

    /// <summary>Cost in USD as the HARNESS computed it, or null when it computes none. Never
    /// derived here.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>When the last report landed. The staleness marker on a figure that trails a
    /// live worker by up to one report.</summary>
    public DateTimeOffset ReportedAt { get; set; }
}

/// <summary>
/// Bytes a Team has moved through relay forwards, spec §9 check 10 / §9.10 — <b>accounting,
/// not enforcement</b>.
///
/// <para>Its own table, one row per Team, and it stays that way now that the dollar-ceiling
/// row it once sat beside is gone (2026-08-12, §9's note): what is counted here is bytes a
/// relay actually moved, which is a different kind of number from anything a Team is
/// <em>granted</em>, and a Team should not acquire a spend record merely because bytes
/// flowed.</para>
///
/// <para><b>Nothing is enforced on this.</b> No ceiling is checked against it, because §8.3
/// forbids severing an established splice mid-flight, so what a reached byte ceiling should
/// actually do is an unresolved design question. What this gives a human is visibility — the
/// §12 Team view's byte burn — and it is honestly <b>best-effort</b>: the relay reports
/// asynchronously, so a relay that dies loses its unreported tail. A containment signal, never
/// an invoice.</para>
///
/// <para><see cref="ForwardedBytes"/> only ever increases, for the plainest of reasons:
/// bytes already moved cannot un-move.</para>
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
