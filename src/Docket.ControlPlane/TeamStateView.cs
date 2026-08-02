using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// The Team view (§12) as structured data, returned by <c>get_team_state</c>.
/// Counts, states, and identifiers — plus the one deliberate free-text field a Lead
/// needs to adjudicate: the worker's in-band <see cref="TeamTaskSummary.Report"/>
/// (§10), which the Lead reads and treats as untrusted agent claims (§13), never
/// authority. Everything else (descriptions, blocker notes, criteria) is still
/// fetched deliberately elsewhere. The fields here are the reattachment surface
/// (§4): enough for a fresh Lead to reconstruct what the Team is doing.
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
/// human attention. <see cref="ContinuesTaskId"/> is the Y-continues-X lineage
/// (§6/§11): the prior task whose harness session this one resumed, or null for an
/// ordinary task — an identifier, never prose. <see cref="CompletionProvenance"/>
/// records who adjudicated a completed task (§9 check 4), null until then.
/// <see cref="Report"/> is the worker's opaque in-band report (§10) once it has
/// reported a result — agent-authored claims (§13), null until then.
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
