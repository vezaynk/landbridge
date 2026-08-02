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
public sealed record MachineHeartbeat(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    SystemLoad Load,
    int RunningTasks,
    IReadOnlyList<string> Profiles,
    DateTimeOffset At,
    bool TranscriptsServable = false);
