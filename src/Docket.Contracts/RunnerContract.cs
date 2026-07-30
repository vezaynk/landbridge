using Docket.Core;

namespace Docket.Contracts;

/// <summary>
/// The control-plane ↔ runner contract, spec §10 — <b>the only frozen
/// interface in the system</b>. It is a closed vocabulary: a runner rejects
/// anything outside it (see <see cref="RunnerWire.DecodeCommand"/> for the
/// wire-boundary rejection). The hierarchy is closed structurally too — the
/// base constructors are <c>private protected</c>, so no message type can be
/// added from outside this assembly.
///
/// <b>Every message carries a task id</b> (§10): a machine runs many agents
/// concurrently and there is no implied current task in either direction. The
/// single exception is <see cref="RebootedEvent"/>, which is machine-scoped by
/// construction — it is emitted precisely when the runner holds no tasks to
/// reference (§10 runner restart).
///
/// Nothing here is domain-specific: the runner is transport (§2.6). It lives in
/// <c>Docket.Contracts</c> so both sides — <c>docketd</c> and the control
/// plane — share one wire vocabulary rather than each redeclaring it.
/// </summary>
public abstract record RunnerMessage
{
    private protected RunnerMessage() { }
}

// ── Outbound: control plane → runner ─────────────────────────────────────────

/// <summary>Commands the control plane sends the runner (§10 outbound).</summary>
public abstract record RunnerCommand : RunnerMessage
{
    private protected RunnerCommand() { }
}

/// <summary>
/// <c>dispatch</c> — run one task under the named profile. Carries the minted
/// worker token and generated MCP config the runner injects into the harness
/// (§5, §13), the harness-local budget cap (§10, §9 check 9), and opaque
/// substitutions for the profile's spawn argv. The runner never interprets any
/// of it — it is transport.
///
/// <para><see cref="ResumeSessionRef"/> was <b>added</b> for §11 resume — an
/// additive, wire-compatible field exactly like the relay fields on
/// <see cref="OpenForwardCommand"/>: an older envelope carrying none decodes to
/// <c>null</c>. It is the opaque harness session ref of a task that was worked
/// before and parked/requeued; the plane passes it back whenever the row holds
/// one, and the runner resumes the transcript only if the resolved profile also
/// declares how (<c>resume.args</c>) — otherwise it cold-starts (documented
/// fallback). Opaque transport metadata: the runner substitutes it into
/// <c>resume.args</c> and never interprets it (§11 resume seam).</para>
/// </summary>
public sealed record DispatchCommand(
    TaskId Task,
    string Profile,
    string WorkerToken = "",
    string? McpConfigJson = null,
    decimal? BudgetUsd = null,
    Dictionary<string, string>? SpawnSubstitutions = null,
    string? ResumeSessionRef = null) : RunnerCommand;

/// <summary>
/// <c>stop(ttl, disposition)</c> — graceful wind-down (§10, §11). Delivered as
/// an injected message turn where the profile supports it, a signal otherwise;
/// on TTL expiry the runner hard-kills. <c>ttl == 0</c> means kill immediately
/// without waiting for ack (§9 check 12).
/// </summary>
public sealed record StopCommand(
    TaskId Task,
    TimeSpan Ttl,
    StopDisposition Disposition = StopDisposition.Preserve,
    string? Reason = null) : RunnerCommand;

/// <summary><c>kill</c> — take the task's whole process group down now (§10).</summary>
public sealed record KillCommand(TaskId Task) : RunnerCommand;

/// <summary>
/// <c>open-forward</c> — the control plane asks this runner to stand up one end
/// of a relay forward (§8.3). In this deployment the plane (not the relay, which
/// holds no docketd channel of its own) sends this to <em>both</em> ends over the
/// runner channel: the <c>producer</c> dials <see cref="Port"/> on loopback and
/// opens its outbound tunnel; the <c>consumer</c> binds a loopback listener,
/// reports the bound port via <see cref="ForwardOpenedEvent"/>, and opens its
/// tunnel per accepted connection.
///
/// <para>The <see cref="Role"/>/<see cref="Grant"/>/<see cref="RelayUrl"/>/<see cref="Port"/>
/// fields were <b>added</b> to the frozen §10 member once §8.3's internals were
/// implemented — additions are wire-compatible because the envelope decode ignores
/// unknown properties and fills absent ones with these defaults. An older envelope
/// carrying none decodes to an empty <see cref="Role"/> and <c>0</c>
/// <see cref="Port"/>, which the runner treats as "acknowledge, do nothing"
/// (§10, the pre-increment-3 stub behaviour) rather than crashing.</para>
/// </summary>
/// <param name="Role"><c>consumer</c>|<c>producer</c> (<see cref="RelayTunnel"/>); empty on a legacy envelope.</param>
/// <param name="Grant">The opaque connection grant both ends present to the relay, each for its own role.</param>
/// <param name="RelayUrl">The relay base URL this end dials (http/https → ws/wss <c>/tunnel</c>).</param>
/// <param name="Port">Producer: the registered service's loopback port to dial. Consumer: <c>0</c> (it binds one).</param>
public sealed record OpenForwardCommand(
    TaskId Task,
    string ForwardId,
    string ServiceName,
    string Role = "",
    string Grant = "",
    string RelayUrl = "",
    int Port = 0) : RunnerCommand;

/// <summary>
/// Stop dispositions, spec §11. Distinct from <see cref="CancelDisposition"/>:
/// this is the runner-transport wind-down instruction, not the state-machine
/// cancel enum. <c>preserve</c>/<c>preserve_and_park</c> are only as good as
/// the harness's stop delivery (§10).
/// </summary>
public enum StopDisposition
{
    Preserve,
    Discard,
    PreserveAndPark,
}

// ── Inbound: runner → control plane ──────────────────────────────────────────

/// <summary>Events the runner reports upstream (§10 inbound). Buffered through
/// the bounded ring (§10 buffering) before they reach the channel.</summary>
public abstract record RunnerEvent : RunnerMessage
{
    private protected RunnerEvent() { }
}

/// <summary><c>started</c> — the harness is up. Distinct from dispatch ack: a
/// death after <c>started</c> means side effects may exist, so requeue is not
/// free (§10).</summary>
public sealed record StartedEvent(TaskId Task, DateTimeOffset At) : RunnerEvent;

/// <summary>
/// <c>session-started</c> — the harness reported its opaque session ref (§11
/// resume seam). Emitted once per task the moment the events source captures it
/// (claude <c>system/init</c>); the control plane stamps <see cref="SessionRef"/>
/// verbatim onto the task row — opaque transport metadata like
/// <c>ResultReference</c>/<c>traceparent</c>, never interpreted — so a later park
/// can carry it and redispatch can resume the transcript. A frozen-vocabulary
/// addition, precedented by <see cref="ForwardOpenedEvent"/>.
/// </summary>
public sealed record SessionStartedEvent(TaskId Task, string SessionRef, DateTimeOffset At) : RunnerEvent;

/// <summary><c>alive</c> — per-task liveness signal (§10 concurrency).</summary>
public sealed record AliveEvent(TaskId Task, DateTimeOffset At) : RunnerEvent;

/// <summary><c>tool-call</c> — a progress signal derived from harness hooks;
/// per-task liveness and budget attribution key off it (§2.2, §10).</summary>
public sealed record ToolCallEvent(TaskId Task, string Tool, DateTimeOffset At) : RunnerEvent;

/// <summary><c>subagent-spawned</c> — subagent lineage where the harness emits
/// it; progressive enhancement, not a given (§10 telemetry ingest).</summary>
public sealed record SubagentSpawnedEvent(
    TaskId Task, string? AgentId, string? ParentAgentId, DateTimeOffset At) : RunnerEvent;

/// <summary><c>exited</c> — the harness process ended (§10). Observed directly
/// by process supervision, not inferred.</summary>
public sealed record ExitedEvent(TaskId Task, int ExitCode, DateTimeOffset At) : RunnerEvent;

/// <summary><c>auth-failed</c> — reports structured facts (§11): operation,
/// target, error code, missing scope. The control plane renders remediation.</summary>
public sealed record AuthFailedEvent(
    TaskId Task, string Operation, string Target, string ErrorCode, string? MissingScope) : RunnerEvent;

/// <summary>
/// <c>forward-opened</c> — the consumer end bound its loopback listener and is
/// ready (§8.3). <see cref="Port"/> is the <c>127.0.0.1</c> port the worker's
/// client connects to; the control plane hands it back from <c>open_forward</c>.
/// Only the consumer end emits this — the producer dials an already-known port.
/// </summary>
public sealed record ForwardOpenedEvent(TaskId Task, string ForwardId, int Port) : RunnerEvent;

/// <summary><c>forward-closed</c> — a relay forward for this task closed (§8.3).</summary>
public sealed record ForwardClosedEvent(TaskId Task, string ForwardId) : RunnerEvent;

/// <summary>
/// <c>rebooted</c> — the runner restarted and adopted nothing (§10 runner
/// restart). The lone machine-scoped message: after a restart with no
/// persistence the runner has no task ids to name, which is the whole point —
/// every task it held requeues against the infrastructure counter (§6).
/// </summary>
public sealed record RebootedEvent(string MachineId, DateTimeOffset At) : RunnerEvent;
