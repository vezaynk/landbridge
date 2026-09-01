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
public sealed record MachineHeartbeat(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    SystemLoad Load,
    int RunningSessions,
    IReadOnlyList<string> Profiles,
    DateTimeOffset At,
    bool TranscriptsServable = false,
    IReadOnlyList<ProcessStatus>? Processes = null);

/// <summary>
/// What a machine reports about one agent-started <b>process</b> (§10, §12). A process
/// is agent-started and never restarted, so <see cref="ServiceState.Exited"/> is a
/// resting state rather than a transient one.
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

/// <summary>A process's current condition on its machine (§10).</summary>
public enum ServiceState
{
    /// <summary>Process alive.</summary>
    Running,

    /// <summary>Not running — stopped on request.</summary>
    Stopped,

    /// <summary>
    /// Ran and ended. A resting state for an agent-started process (§10), which is never
    /// restarted — its exit code is information for the agent to act on, not something to hide
    /// behind a backoff ladder.
    /// </summary>
    Exited,
}
