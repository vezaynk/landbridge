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
public sealed record MachineHeartbeat(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    SystemLoad Load,
    int RunningTasks,
    IReadOnlyList<string> Profiles,
    DateTimeOffset At);
