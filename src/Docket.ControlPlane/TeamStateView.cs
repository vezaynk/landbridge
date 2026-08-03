using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// The Team view (§12) as structured data, returned by <c>get_team_state</c>.
/// Counts and states only — <b>never prose</b> (§10): a Lead reads free text
/// (descriptions, blocker notes, results, the worker's report) deliberately, one
/// item at a time and delimited as untrusted (§13). The fields here are the
/// reattachment surface (§4): enough for a fresh Lead to reconstruct what the Team
/// is doing without ever seeing a task's contents.
/// </summary>
public sealed record TeamStateView(
    Guid TeamId,
    int TotalTasks,
    IReadOnlyDictionary<TaskState, int> CountsByState,
    IReadOnlyList<TeamTaskSummary> Tasks,
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
    bool HasReport);

/// <summary>
/// One task's worker report (§10), returned by the Lead's deliberate per-task
/// <c>get_task_report</c> fetch (§13: free text pulled one item at a time, not on
/// the bulk status read). <see cref="Report"/> is the opaque worker-authored text,
/// null when the task has left none. Team-scoped at the store, so this is only ever
/// built for a task in the caller's own Team.
/// </summary>
public sealed record TaskReportView(
    Guid TaskId,
    string Namespace,
    string? Report);

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
