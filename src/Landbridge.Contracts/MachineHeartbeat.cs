namespace Landbridge.Contracts;

/// <summary>
/// The machine-level heartbeat, spec §10: landbridged on its own timer. Loss means
/// every task on the machine is suspect. Carries the derived readiness the
/// control plane dispatches against — <c>ready</c> unless under back-pressure,
/// in which case the machine shows as <c>saturated</c> (§10 concurrency) — and
/// the <see cref="Profiles"/> the machine declares, which is the only channel
/// by which the control plane learns a machine's profile set for exact-match
/// dispatch routing (§7, §10). It is not part of the frozen command/event enum;
/// it is the runner's periodic self-report.
/// </summary>
/// <param name="TranscriptsServable">
/// Whether this <c>landbridged</c> can answer <see cref="ReadTranscriptCommand"/> (§12
/// serving). Added because the alternative is a dashboard offering a transcript link
/// that silently times out against an older runner: a runner predating the command
/// rejects it at the wire boundary (<see cref="RunnerWire.DecodeCommand(string)"/> returns
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
    int RunningSessions,
    IReadOnlyList<string> Profiles,
    DateTimeOffset At,
    bool TranscriptsServable = false,
    IReadOnlyList<ServiceStatus>? Services = null,
    IReadOnlyList<ProcessStatus>? Processes = null);

/// <summary>
/// What a machine reports about one agent-started <b>process</b> (§10, §12) — deliberately a
/// separate list from <see cref="ServiceStatus"/> rather than a flag on it, so a reader is
/// never left working out which kind they are looking at. A service is operator-declared and
/// restart-supervised; a process is agent-started and never restarted, so <see cref="ServiceState.Exited"/>
/// is a resting state here rather than a transient one.
/// </summary>
/// <param name="DeclaredBySession">Provenance, not ownership: the task whose worker started it.
/// The process is machine-scoped and outlives that task, and any worker on this machine may
/// stop it — which is what lets a Lead's cleanup continuation tidy up.</param>
/// <param name="StdinOpen">Whether it has a usable stdin pipe — false unless the starter asked
/// for one. A cleanup agent needs this before choosing how to stop it: without stdin there is no
/// graceful EOF lever, so stopping is the bounded wait and then a tree kill.</param>
public sealed record ProcessStatus(
    string Name,
    ServiceState State,
    Guid DeclaredBySession,
    DateTimeOffset? StartedAt = null,
    int? ExitCode = null,
    DateTimeOffset? ExitedAt = null,
    bool StdinOpen = false);

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

    /// <summary>
    /// Ran and ended. A resting state for an agent-started process (§10), which is never
    /// restarted — its exit code is information for the agent to act on, not something to hide
    /// behind a backoff ladder. A config-declared service does not rest here: it is restarted.
    /// </summary>
    Exited,

    /// <summary>
    /// Declared with <c>enabled: false</c> and deliberately not started. Distinct from
    /// <see cref="Stopped"/> on purpose: "the operator turned this off" and "this is not
    /// running and nobody meant that" are different facts, and an operator reading the
    /// Machine Group view needs to tell them apart.
    /// </summary>
    Disabled,
}
