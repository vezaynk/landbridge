using Docket.Core;

namespace Docket.Runner;

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
/// Nothing here is domain-specific: the runner is transport (§2.6).
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
/// </summary>
public sealed record DispatchCommand(
    TaskId Task,
    string Profile,
    string WorkerToken = "",
    string? McpConfigJson = null,
    decimal? BudgetUsd = null,
    Dictionary<string, string>? SpawnSubstitutions = null) : RunnerCommand;

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
/// <c>open-forward</c> — the relay asks this runner (the producer side) to dial
/// a registered local service and open its outbound tunnel (§8.3). Forwarding
/// internals are deferred; the vocabulary member is frozen here.
/// </summary>
public sealed record OpenForwardCommand(TaskId Task, string ForwardId, string ServiceName) : RunnerCommand;

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

/// <summary><c>forward-closed</c> — a relay forward for this task closed (§8.3).</summary>
public sealed record ForwardClosedEvent(TaskId Task, string ForwardId) : RunnerEvent;

/// <summary>
/// <c>rebooted</c> — the runner restarted and adopted nothing (§10 runner
/// restart). The lone machine-scoped message: after a restart with no
/// persistence the runner has no task ids to name, which is the whole point —
/// every task it held requeues against the infrastructure counter (§6).
/// </summary>
public sealed record RebootedEvent(string MachineId, DateTimeOffset At) : RunnerEvent;
