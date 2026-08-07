using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// The Team view (§12) as structured data, returned by <c>get_team_state</c>.
/// Counts and states only — <b>never prose</b> (§10): a Lead reads free text
/// (descriptions, results, the worker's report, the question it is blocked on)
/// deliberately, one item at a time and delimited as untrusted (§13). The fields here
/// are the reattachment surface (§4): enough for a fresh Lead to reconstruct what the
/// Team is doing — including which tasks are waiting on it and what kind of attention
/// each needs — without ever seeing a task's contents.
///
/// <para><see cref="Budget"/> is <b>read-only</b> for a Lead (§9.9): it needs to see the
/// ceiling to understand why <c>create_task</c> is refusing it, and telling it "no budget"
/// is strictly better than letting it re-derive that from failures. Changing the ceiling is
/// a human act on the dashboard and has deliberately no MCP tool — a Lead that could raise
/// its own limit is enforcement where a model can reason past it (§2 principle 3).</para>
/// </summary>
public sealed record TeamStateView(
    Guid TeamId,
    int TotalTasks,
    IReadOnlyDictionary<TaskState, int> CountsByState,
    IReadOnlyList<TeamTaskSummary> Tasks,
    LeadMachineView? BoundMachine = null,
    TeamBudgetView? Budget = null);

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
/// One task's structural summary. <see cref="Namespace"/> is the server-assigned
/// <c>team-{id}/task-{id}</c> identifier (§7), not content. <see cref="Parked"/>
/// surfaces §12's "parks per task" signal — whether decomposition is starving on
/// human attention. <see cref="ContinuesTaskId"/> is the Y-continues-X lineage
/// (§6/§11): the prior task whose harness session this one resumed, or null for an
/// ordinary task — an identifier, never prose. <see cref="CompletionProvenance"/>
/// records who adjudicated a completed task (§9 check 4), null until then.
/// <see cref="HasReport"/> is a <em>flag</em> — not the text — that the worker left
/// an in-band report (§10); the Lead fetches the report itself deliberately, one
/// task at a time, via <c>get_task_report</c> (keeps this bulk view prose-free).
/// <see cref="InputKind"/> and <see cref="HasQuestion"/> split the worker's input
/// request the same way (§11): the typed kind rides along because it is structure and
/// it is the triage fact — <c>auth_help</c> needs a human, a <c>question</c> the Lead
/// can take — while the question's text does not, and is pulled per task with
/// <c>get_task_question</c>.
/// <see cref="InfrastructureRequeues"/> and <see cref="LastRequeueReason"/> are the §6
/// infrastructure counter and the signal behind its last increment (#73) — both
/// structure, and both here because a task quietly on its fourth machine is a shape of
/// trouble <see cref="Attempt"/> alone does not name. On a
/// <see cref="TaskState.Canceled"/> task a non-null reason is what distinguishes the
/// requeue cap (§9 check 7) from a cancel someone asked for; the cap it was measured
/// against comes with the per-task <c>get_task_report</c> fetch.
/// </summary>
public sealed record TeamTaskSummary(
    Guid TaskId,
    string Namespace,
    TaskState State,
    CompletionMode Mode,
    int Attempt,
    bool Parked,
    Guid? ContinuesTaskId,
    VerdictProvenance? CompletionProvenance,
    bool HasReport,
    InputRequestKind? InputKind,
    bool HasQuestion,
    int InfrastructureRequeues = 0,
    LivenessLossReason? LastRequeueReason = null);

/// <summary>
/// One task's worker report (§10), returned by the Lead's deliberate per-task
/// <c>get_task_report</c> fetch (§13: free text pulled one item at a time, not on
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
public sealed record TaskReportView(
    Guid TaskId,
    string Namespace,
    string? Report,
    string? ResultReference,
    int InfrastructureRequeues = 0,
    int InfrastructureRequeueLimit = TaskRecord.DefaultInfrastructureRequeueLimit,
    LivenessLossReason? LastRequeueReason = null);

/// <summary>
/// One task's input exchange (§10/§11), returned by the Lead's deliberate per-task
/// <c>get_task_question</c> fetch — the question read path, shaped exactly like
/// <see cref="TaskReportView"/> because it is the same discipline: free text pulled
/// one item at a time, never on the bulk status read (§13).
/// <see cref="Question"/> is the opaque worker-authored ask (null when the task asked
/// nothing), <see cref="Answer"/> the opaque answer already given (null while the
/// question is still open), and <see cref="Kind"/> the typed routing fact.
/// <see cref="State"/> rides along because "is this still waiting on me?" is the
/// question a Lead asks in the same breath — a task that has moved back to
/// <c>submitted</c> has been answered. Team-scoped at the store.
/// </summary>
public sealed record TaskQuestionView(
    Guid TaskId,
    string Namespace,
    TaskState State,
    InputRequestKind? Kind,
    string? Question,
    string? Answer);

/// <summary>
/// The seed facts a <c>create_task(continues:)</c> reads off the continued task's
/// row (§6/§11), returned by <see cref="TaskStore.ReadContinuationSourceAsync"/>:
/// the owning Team, the profile to default to, the opaque harness session ref to
/// resume, and the park machine as a fallback preferred machine. Identifiers and
/// opaque refs only — no prose.
/// </summary>
public sealed record ContinuationSource(
    TeamId Team,
    string? Profile,
    string? HarnessSessionRef,
    string? ParkMachine);

/// <summary>
/// One blocked_on_input task as the wait-TTL sweeper reads it (§11):
/// <see cref="BlockedAt"/> is when it entered the state (null only if it blocked
/// before the column existed), <see cref="Attempt"/> is the running attempt the
/// sweeper stamps into a park record so the successor knows it inherited a
/// workspace (§7), and <see cref="HarnessSessionRef"/> is the opaque session ref
/// of the work session that then blocked — the sweeper copies it into the park
/// record so redispatch can resume the transcript (§11 resume; null when no
/// session-init was ever observed). No prose and no machine — the machine comes
/// from the live connection registry, not the row (§10 single control plane;
/// machine-assignment persistence across a restart is a documented follow-up).
/// </summary>
public sealed record BlockedTaskView(
    Guid TaskId,
    DateTimeOffset? BlockedAt,
    int Attempt,
    string? HarnessSessionRef);
