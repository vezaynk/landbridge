using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// The Team view (§12) as structured data, returned by <c>get_team_state</c>.
/// Counts and states only — <b>never prose</b> (§10): a Lead reads free text
/// (descriptions, blocker notes, results) deliberately, one item at a time and
/// delimited as untrusted (§13). The fields here are the reattachment surface
/// (§4): enough for a fresh Lead to reconstruct what the Team is doing without
/// ever seeing a task's contents.
/// </summary>
public sealed record TeamStateView(
    Guid TeamId,
    int TotalTasks,
    IReadOnlyDictionary<TaskState, int> CountsByState,
    IReadOnlyList<TeamTaskSummary> Tasks);

/// <summary>
/// One task's structural summary. <see cref="Namespace"/> is the server-assigned
/// <c>team-{id}/task-{id}</c> identifier (§7), not content. <see cref="Parked"/>
/// surfaces §12's "parks per task" signal — whether decomposition is starving on
/// human attention.
/// </summary>
public sealed record TeamTaskSummary(
    Guid TaskId,
    string Namespace,
    TaskState State,
    CompletionMode Mode,
    int Attempt,
    bool Parked);

/// <summary>
/// The verifier's narrow read scope (§5, §10 verifier webhook): one automated
/// task in <see cref="TaskState.Verifying"/> the verifier may run its check
/// against. It carries exactly what §5 permits — the namespace, the opaque
/// completion criteria the verifier interprets (§7), and the result reference
/// the worker reported — and nothing else. Review-mode tasks are excluded: their
/// verdict arrives through the Lead's <c>submit_review</c> (human-confirmed, §7),
/// not this webhook. The <see cref="ResultReference"/> is null only if the worker
/// somehow reached verifying without one, which the state machine forbids (§6).
/// </summary>
public sealed record VerifyingTaskView(
    Guid TaskId,
    string Namespace,
    string CompletionCriteria,
    string? ResultReference);

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
