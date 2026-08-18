using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// The Team view (§12) as structured data, returned by <c>get_team_state</c>.
/// Counts and states only — <b>never prose</b> (§10): a Lead reads free text
/// (descriptions, results, the worker's report, the question it is blocked on)
/// deliberately, one item at a time and delimited as untrusted (§13). The fields here
/// are the reattachment surface (§4): enough for a fresh Lead to reconstruct what the
/// Team is doing — including which tasks are waiting on it and what kind of attention
/// each needs — without ever seeing a task's contents.
/// </summary>
public sealed record TeamStateView(
    Guid TeamId,
    int TotalSessions,
    IReadOnlyDictionary<SessionState, int> CountsByState,
    IReadOnlyList<TeamSessionSummary> Sessions,
    LeadMachineView? BoundMachine = null);

/// <summary>
/// The calling Lead's own bound machine (§8.3 human path), carried on the
/// reattachment surface because that is where a fresh Lead learns it: null means no
/// machine is bound and <c>open_lead_forward</c> will refuse until one is. A
/// machine-scoped fact about the <em>human</em>, not about the Team — a takeover does
/// not inherit it — so it is composed onto the view by the tool that knows the
/// caller, not read out of the Team's rows. Identifiers only, no prose (§10).
/// </summary>
public sealed record LeadMachineView(Guid MachineId, string MachineName, DateTimeOffset BoundAt);

/// <summary>
/// The fleet's declared profiles as <b>routing targets</b>, returned by the Lead's
/// <c>list_profiles</c> (§7 exact-match routing, §10 as-built refinement). A task's
/// <c>profile</c> is matched exactly against what a machine declares, so a Lead that
/// guesses a name it cannot look up creates a task nothing will ever claim — and nothing
/// reports why. This is the read that ends that.
///
/// <para><b>Deliberately not the Machine Group view</b> (§12, which stays human-only).
/// This is the routing projection and nothing else: profile names, the machines offering
/// each, and whether each of those machines can take work right now. No tasks, no owning
/// Teams, no services, no processes, no takeover history — a Lead needs to know where a
/// profile can run, not what the fleet is doing.</para>
///
/// <para><b>Fleet-wide, and that is the correct scope for this one read.</b> Every other
/// Lead read is Team-scoped because it carries Team content; a profile name is
/// operator-declared machine configuration that no Team owns, and it is the Lead's own
/// <c>create_session</c> argument. Scoping it to a Team would be scoping a fact that has no
/// Team — and would leave the Lead guessing again.</para>
///
/// <para><see cref="ConnectedMachines"/> is a count, not an enumeration, and it exists for
/// the empty answer: no profiles with no machines connected means the fleet is down, while
/// no profiles with machines connected means they have dialled in but not yet declared
/// anything (a machine's profiles arrive on its first heartbeat, §10). Those are different
/// problems and a Lead that cannot tell them apart waits on the wrong one.</para>
/// </summary>
/// <param name="DefaultProfile">The profile an omitted <c>create_session(profile:)</c> resolves
/// to — <c>default</c>. Carried rather than left implicit because it is the one profile name
/// a Lead uses without typing it: if it is absent from <see cref="Profiles"/>, or present but
/// not <see cref="ProfileRoutingEntry.Dispatchable"/>, then plain profile-less tasks are not
/// routable either, which is otherwise an invisible fleet condition.</param>
public sealed record ProfileRoutingView(
    IReadOnlyList<ProfileRoutingEntry> Profiles,
    int ConnectedMachines,
    string DefaultProfile = MachineSnapshot.DefaultProfile);

/// <summary>
/// One declared profile and where it can run. A profile appears here if and only if some
/// currently-connected machine declares it, so the list is the exact set of names
/// <c>create_session(profile:)</c> can match — a name absent from it is a name no task should
/// carry.
/// </summary>
/// <param name="Dispatchable">Whether at least one machine declaring this profile can take a
/// task <em>now</em>. Equivalent by construction to
/// <see cref="RunnerConnectionRegistry.TryPickMachine"/> finding a machine for it, which is
/// the same eligibility dispatch itself runs on: readiness already has back-pressure folded
/// into it (§10), and the engine's own check is this readiness plus this exact-match. False
/// with a non-empty <see cref="Machines"/> is the informative case — the profile exists and
/// every machine offering it is saturated or not yet ready, so a task on it will queue and
/// then run, rather than sit unclaimable forever.</param>
public sealed record ProfileRoutingEntry(
    string Profile,
    bool Dispatchable,
    IReadOnlyList<ProfileMachineView> Machines);

/// <summary>
/// One machine offering a profile, as a routing candidate: its id and whether it is
/// currently able to accept a dispatch. Identifiers and liveness only (§10) — the machine's
/// tasks, services and processes are the §12 human view's business, not routing's.
/// <see cref="UnderBackPressure"/> rides beside <see cref="Ready"/> for the same reason the
/// §12 view keeps both: "saturated" and "not ready" are different waits, and a Lead told
/// only "not ready" cannot tell a busy fleet from a broken one.
/// <see cref="LastHeartbeat"/> is when the machine last spoke, so a Lead can judge how fresh
/// this answer is rather than trusting a boolean of unknown age.
/// </summary>
public sealed record ProfileMachineView(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    DateTimeOffset? LastHeartbeat);

/// <summary>
/// One task's structural summary. <see cref="Namespace"/> is the server-assigned
/// <c>team-{id}/session-{id}</c> identifier (§7), not content. <see cref="Parked"/>
/// surfaces §12's "parks per task" signal — whether decomposition is starving on
/// human attention. <see cref="ContinuesSessionId"/> is the Y-continues-X lineage
/// (§6/§11): the prior task whose harness session this one resumed, or null for an
/// ordinary task — an identifier, never prose. <see cref="CompletionProvenance"/>
/// records who adjudicated a completed task (§9 check 4), null until then.
/// <see cref="HasReport"/> is a <em>flag</em> — not the text — that the worker left
/// an in-band report (§10); the Lead fetches the report itself deliberately, one
/// task at a time, via <c>get_session_report</c> (keeps this bulk view prose-free).
/// <see cref="InputKind"/> and <see cref="HasQuestion"/> split the worker's input
/// request the same way (§11): the typed kind rides along because it is structure and
/// it is the triage fact — <c>auth_help</c> needs a human, a <c>question</c> the Lead
/// can take — while the question's text does not, and is pulled per task with
/// <c>get_session_question</c>. <see cref="HasQuestion"/> is a live wait (the row still
/// has <c>BlockedAt</c>), not "this task ever asked": a working task with no flag
/// is still turning, one with the flag is idle for the Lead.
/// <see cref="InfrastructureRequeues"/> and <see cref="LastRequeueReason"/> are the §6
/// infrastructure counter and the signal behind its last increment (#73) — both
/// structure, and both here because a task quietly on its fourth machine is a shape of
/// trouble <see cref="Attempt"/> alone does not name. On a
/// <see cref="SessionState.Canceled"/> task a non-null reason is what distinguishes the
/// requeue cap (§9 check 7) from a cancel someone asked for; the cap it was measured
/// against comes with the per-task <c>get_session_report</c> fetch.
/// </summary>
public sealed record TeamSessionSummary(
    Guid SessionId,
    string Namespace,
    SessionState State,
    CompletionMode Mode,
    int Attempt,
    bool Parked,
    Guid? ContinuesSessionId,
    VerdictProvenance? CompletionProvenance,
    bool HasReport,
    InputRequestKind? InputKind,
    bool HasQuestion,
    int InfrastructureRequeues = 0,
    LivenessLossReason? LastRequeueReason = null);

/// <summary>
/// One task's worker report (§10), returned by the Lead's deliberate per-task
/// <c>get_session_report</c> fetch (§13: free text pulled one item at a time, not on
/// the bulk status read). <see cref="Report"/> is the opaque worker-authored text,
/// null when the task has left none. Team-scoped at the store, so this is only ever
/// built for a task in the caller's own Team.
///
/// <para><see cref="ResultReference"/> is the §8.1 artifact pointer the worker handed
/// over on working → verifying — a commit, branch, or URL, stored verbatim and never
/// dereferenced. It rides this fetch because it is the half the Lead <em>must</em> be
/// able to read: §6 requires it for the transition while the report beside it stays
/// optional, so on a task whose worker reported no prose it is the only thing the
/// worker said, and §7 has the Lead reading it before adjudicating. Non-null on any
/// task that reached <c>verifying</c>; null before that, or on one the plane ended
/// first.</para>
///
/// <para>The infrastructure account travels with it (§6/§9 check 7, #73):
/// <see cref="InfrastructureRequeues"/> of <see cref="InfrastructureRequeueLimit"/>
/// requeues, and <see cref="LastRequeueReason"/> for the signal behind the last one.
/// This is the read that answers "what happened to this task", and on a task the cap
/// abandoned it is the <em>only</em> answer available — nothing ever finished, so there
/// is no report to explain it. A count that has reached the limit means the cap ended
/// the task; a non-positive limit means the cap is configured off.</para>
/// </summary>
public sealed record SessionReportView(
    Guid SessionId,
    string Namespace,
    string? Report,
    string? ResultReference,
    int InfrastructureRequeues = 0,
    int InfrastructureRequeueLimit = SessionRecord.DefaultInfrastructureRequeueLimit,
    LivenessLossReason? LastRequeueReason = null);

/// <summary>
/// One task's input exchange (§10/§11), returned by the Lead's deliberate per-task
/// <c>get_session_question</c> fetch — the question read path, shaped exactly like
/// <see cref="SessionReportView"/> because it is the same discipline: free text pulled
/// one item at a time, never on the bulk status read (§13).
/// <see cref="Question"/> is the opaque worker-authored ask (null when the task asked
/// nothing), <see cref="Answer"/> the opaque answer already given (null while the
/// question is still open), and <see cref="Kind"/> the typed routing fact.
/// <see cref="State"/> rides along because "is this still waiting on me?" is the
/// question a Lead asks in the same breath — a task that has moved back to
/// <c>submitted</c> has been answered. Team-scoped at the store.
/// </summary>
/// <param name="PermissionTool">On an <see cref="InputRequestKind.Permission"/> request
/// (§11 permission bridge), the harness tool awaiting approval — with
/// <paramref name="Question"/> carrying the proposed tool input verbatim beside it. Null on
/// every other kind, which is also how a reader tells the two apart without switching on
/// <paramref name="Kind"/>.</param>
/// <param name="Verdict">How a permission request was decided, null while it is still
/// pending. <paramref name="Answer"/> carries the decision's message, so the
/// question/answer pair reads the same way for a permission request as for a prose one.</param>
/// <param name="EscalationReason">Why a pending permission request was marked human-only,
/// null if it was not — and, for a Lead reading this, the signal that it can no longer
/// decide this one itself.</param>
public sealed record SessionQuestionView(
    Guid SessionId,
    string Namespace,
    SessionState State,
    InputRequestKind? Kind,
    string? Question,
    string? Answer,
    string? PermissionTool = null,
    PermissionVerdict? Verdict = null,
    string? EscalationReason = null);

/// <summary>
/// One permission request as an answerer reads it (§11/§12 permission bridge): everything
/// needed to decide, and nothing that pretends to be more trustworthy than it is.
/// <see cref="Tool"/> and <see cref="Input"/> are the harness's own strings, relayed through
/// an agent's process — untrusted text every surface renders escaped and fenced, never
/// interpreted (§2 principle 1, §13). <see cref="BlockedAt"/> gives the age, which is what
/// tells a human whether a request is about to hit its wait TTL.
/// <see cref="EscalatedAt"/>/<see cref="EscalationReason"/> are set once a Lead has handed
/// it over.
/// </summary>
public sealed record PermissionRequestView(
    Guid SessionId,
    string Namespace,
    Guid TeamId,
    SessionState State,
    DateTimeOffset? BlockedAt,
    string? Tool,
    string? Input,
    PermissionVerdict? Verdict,
    string? Message,
    DateTimeOffset? EscalatedAt,
    string? EscalationReason);

/// <summary>
/// What a decided permission request hands back to the worker tool that has been blocking
/// on it (§11 permission bridge): the verdict and the answerer's words. Translated at the
/// tool boundary into the harness's permission result — allow passes the proposed input
/// through unchanged, deny carries <see cref="Message"/> so the refusal teaches the agent
/// something instead of just stopping it.
/// </summary>
public sealed record PermissionOutcome(PermissionVerdict Verdict, string? Message);

/// <summary>
/// The seed facts a <c>create_session(continues:)</c> reads off the continued task's
/// row (§6/§11), returned by <see cref="SessionStore.ReadContinuationSourceAsync"/>:
/// the owning Team, the profile to default to, the opaque harness session ref to
/// resume, and two fallbacks for the preferred machine. Identifiers and opaque refs
/// only — no prose.
/// </summary>
/// <param name="ParkMachine">The park record's machine, set only while the continued task
/// is parked.</param>
/// <param name="LastRanOn">The machine of the continued task's most recent dispatch, from
/// its worker-instance rows (§12) — the durable fact that survives the task going terminal
/// and its process being forgotten by the live registry. Null only for a task that has
/// never been dispatched, or one whose instance rows predate the machine column.</param>
public sealed record ContinuationSource(
    TeamId Team,
    string? Profile,
    string? HarnessSessionRef,
    string? ParkMachine,
    string? LastRanOn = null);

/// <summary>
/// One blocked_on_input task as the wait-TTL sweeper reads it (§11):
/// <see cref="BlockedAt"/> is when it entered the state (null only if it blocked
/// before the column existed), <see cref="Attempt"/> is the running attempt the
/// sweeper stamps into a park record so the successor knows it inherited a
/// workspace (§7), and <see cref="HarnessSessionRef"/> is the opaque session ref
/// of the work session that then blocked — the sweeper copies it into the park
/// record so redispatch can resume the transcript (§11 resume; null when no
/// session-init was ever observed). No prose and no machine — the machine comes from
/// the live connection registry, not the row (§10 single control plane). That survives
/// a plane restart now: the registry is repopulated for a reconnecting machine from the
/// durable instance record, blocked tasks included
/// (<see cref="DispatchService.RehydrateMachineAsync"/>, #86), so the sweeper resolves a
/// machine again once the machine is back rather than skipping the task forever.
/// </summary>
public sealed record BlockedSessionView(
    Guid SessionId,
    DateTimeOffset? BlockedAt,
    int Attempt,
    string? HarnessSessionRef);

/// <summary>
/// What a task's row says about the dispatch currently on it (§10, §9.14), returned by
/// <see cref="SessionStore.GetIncumbentDispatchAsync"/>: the state, and the worker instance
/// working it (null in every state that holds no incumbent). Read as one pair because that
/// is what the §10 liveness scan decides against and what the <see cref="LivenessLost"/> it
/// then sends is fenced on — a state read and an instance read taken a moment apart would
/// fence nothing, since the fence's whole content is that the pair has not changed since the
/// scan looked. Null from the store means no such task.
/// </summary>
public sealed record IncumbentDispatchView(SessionState State, WorkerInstanceId? Instance);
