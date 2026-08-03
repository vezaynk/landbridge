namespace Docket.Contracts;

/// <summary>
/// The machine-level heartbeat, spec §10: docketd on its own timer. Loss means
/// every task on the machine is suspect. Carries the derived readiness the
/// control plane dispatches against — <c>ready</c> unless under back-pressure,
/// in which case the machine shows as <c>saturated</c> (§10 concurrency) — and
/// the <see cref="Profiles"/> the machine declares, which is the only channel
/// by which the control plane learns a machine's profile set for exact-match
/// dispatch routing (§7, §10). It is not part of the frozen command/event enum;
/// it is the runner's periodic self-report.
/// </summary>
/// <param name="TranscriptsServable">
/// Whether this <c>docketd</c> can answer <see cref="ReadTranscriptCommand"/> (§12
/// serving). Added because the alternative is a dashboard offering a transcript link
/// that silently times out against an older runner: a runner predating the command
/// rejects it at the wire boundary (<see cref="RunnerWire.DecodeCommand"/> returns
/// null) and simply never replies, which is indistinguishable from a slow machine.
/// An older heartbeat omits it and decodes to <c>false</c> — no link offered.
///
/// <para>Deliberately a narrow boolean about <em>this wire member</em>, not a
/// capability manifest (§15) and not a statement about whether any profile enabled
/// capture: a machine that can serve but captured nothing answers with an empty
/// inventory, which is a different and honest answer.</para>
/// </param>
/// <param name="Services">
/// §10 operator-declared services this machine supervises, as it currently sees them
/// (§12 Machine Group view). The heartbeat is the whole channel: the control plane
/// stores the last reported list against the connection and renders it, and
/// <b>interprets nothing</b> — it does not persist service state, model a service
/// lifecycle, or decide when a service is unhealthy. Every judgement stays on the
/// machine that owns the process, so a disconnected machine's services vanish from
/// the view exactly as its tasks and profiles already do.
///
/// <para>Null from a runner predating the field, which decodes to "this machine
/// reports nothing about services" — distinct from an empty list, which is a machine
/// that declares none.</para>
/// </param>
public sealed record MachineHeartbeat(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    SystemLoad Load,
    int RunningTasks,
    IReadOnlyList<string> Profiles,
    DateTimeOffset At,
    bool TranscriptsServable = false,
    IReadOnlyList<ServiceStatus>? Services = null);

/// <summary>
/// What a machine reports about one declared service (§10, §12). Reported, never
/// interpreted by the plane.
///
/// <para><see cref="StartedAt"/> is a timestamp rather than an uptime because a
/// transmitted duration is stale the moment it is serialized; the dashboard renders
/// the age, as it already does for heartbeats.</para>
/// </summary>
public sealed record ServiceStatus(
    string Name,
    ServiceState State,
    int Port,
    DateTimeOffset? StartedAt = null,
    int Restarts = 0,
    int? LastExitCode = null,
    DateTimeOffset? LastFailureAt = null);

/// <summary>A declared service's current condition on its machine (§10).</summary>
public enum ServiceState
{
    /// <summary>Spawned, not yet past its readiness check.</summary>
    Starting,

    /// <summary>Process alive and (where declared) its readiness port answered.</summary>
    Running,

    /// <summary>Exited non-zero or failed readiness; the supervisor is backing off to retry.</summary>
    Failed,

    /// <summary>Not running and not being retried — the supervisor gave up or was told to stop.</summary>
    Stopped,
}
