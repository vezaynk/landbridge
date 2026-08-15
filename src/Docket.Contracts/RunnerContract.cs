using Docket.Core;

namespace Docket.Contracts;

/// <summary>
/// The control-plane ↔ runner contract, spec §10 — <b>the only frozen
/// interface in the system</b>. It is a closed vocabulary: a runner rejects
/// anything outside it (see <see cref="RunnerWire.DecodeCommand(string)"/> for the
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
/// (§5, §13), and opaque substitutions for the profile's spawn argv. The runner
/// never interprets any of it — it is transport.
///
/// <para><b><c>budget_usd</c> was <em>removed</em> here (2026-08-12), which is the one
/// subtraction §10's freeze allows.</b> It carried the per-dispatch dollar cap to the
/// harness (<c>{budget}</c> → Claude Code's <c>--max-budget-usd</c>); the Team-level
/// ceiling that was its only value source is gone (§9's note), so nothing can populate it
/// and no cap it once expressed is being silently dropped. Both decode directions stay
/// clean: an older <c>docketd</c> receiving an envelope without the field reads
/// <c>null</c>, which is exactly what a Team configuring no cap already sent, and a newer
/// one receiving a stale envelope <em>with</em> it ignores the unmapped property. Contrast
/// <see cref="SpawnSubstitutions"/> below, which stays precisely because it still has a
/// live consumer — an unpopulated field a peer can still act on is a seam; one neither
/// side can do anything with is a fiction.</para>
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
///
/// <para><see cref="WorkDirTask"/> is additive and wire-compatible in the same way, and
/// names the task whose working directory this dispatch runs in. It is <b>not</b> about
/// resume: a <c>continues:</c> continuation runs where its predecessor worked whether or
/// not it resumes that transcript, because the workspace <em>is</em> the work (§7, §11) —
/// a cold-started continuation still needs the worktree and artifacts the predecessor
/// left. Transcript resume is the additional bonus when the machine and session survive,
/// and it needs this too, since a harness session is directory-local as well as
/// machine-local (Claude Code resumes only from the directory that created it) and a
/// continuation runs under a NEW task id. Null whenever the directory is the dispatched
/// task's own — every ordinary task, and every park-resume of one.</para>
///
/// <para><b>Not the same value as continuation lineage</b>, which is why it is its own
/// field rather than something the runner could derive: the plane resolves it
/// transitively. Resuming does not create a directory per link, so B continuing A works
/// in A's directory and C continuing B works in A's too, though C's lineage names B.</para>
///
/// <para><b>A task, not a path.</b> The plane does not know — and must not choose — a
/// machine's filesystem layout: <c>work_root</c> is machine-local runner config, and the
/// runner is what maps a task id to a directory under it. Naming a task keeps that
/// mapping in one place and keeps this record free of machine-specific paths. The park
/// record learned the same thing the hard way: it carried a working-directory field for
/// months and nothing plane-side could ever fill it in, so every caller passed null and
/// the field was eventually dropped.</para>
///
/// <para><b><see cref="SpawnSubstitutions"/> now has one producer: <c>mcp_url</c>.</b>
/// The runner consumes the map as extra <c>{key}</c> placeholders alongside the built-in
/// <c>task_id</c> / <c>machine_id</c> / <c>work_dir</c> / <c>session_id</c> /
/// <c>mcp_config</c> set. <c>DispatchService</c> fills <c>mcp_url</c> with the plane's
/// public MCP URL so a profile can write a harness-native config file (#112 G2) without
/// parsing Claude's <c>mcp.json</c>. An older envelope with a null map still decodes;
/// an older runner receiving the key just substitutes it. The field stays on the
/// frozen wire for that reason — dropping it is the one change a year-old
/// <c>docketd</c> would notice.</para>
/// </summary>
public sealed record DispatchCommand(
    TaskId Task,
    string Profile,
    string WorkerToken = "",
    string? McpConfigJson = null,
    Dictionary<string, string>? SpawnSubstitutions = null,
    string? ResumeSessionRef = null,
    TaskId? WorkDirTask = null) : RunnerCommand;

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
/// <c>prompt</c> — deliver a follow-up turn to a worker whose session is <b>still open</b>
/// (<c>ideas/sessions.md</c> stage 1). The first command in this vocabulary that assumes a
/// worker is something you talk to rather than something you launch.
///
/// <para><b>Why this is not <c>dispatch</c> again.</b> A dispatch starts a process, mints a
/// token, writes an MCP config and opens a session; this reaches a conversation that already
/// has all four and adds one message to it. Under the task model the distinction could not
/// arise — a worker that had finished a turn had also exited, so the only way to say
/// anything more to it was to start a new one, cold or resumed. An ACP worker outlives its
/// turn, so "continue" and "begin" stop being the same act.</para>
///
/// <para><b>Only meaningful for a <c>protocol: acp</c> profile.</b> A stream-mode worker has
/// no channel that accepts a turn: its stdin is a dead-man's pipe, not a request channel,
/// and the harnesses this repo supports do not read mid-task turns off it (the reason every
/// stream profile is forced to <c>stop.mode: signal</c>). A runner that receives this for a
/// stream task refuses it rather than writing bytes nobody reads.</para>
///
/// <para>Best-effort like every §10 command: the runner acks that the turn was
/// <em>delivered to the agent</em>, which — unlike the stream mode's message stop — is a
/// claim the protocol actually supports, because an ACP agent is specified to consume
/// <c>session/prompt</c>.</para>
/// </summary>
public sealed record PromptCommand(TaskId Task, string Text) : RunnerCommand;

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
/// <param name="ServiceName">
/// Which registered service this forward is for — <b>diagnostic only: the runner never reads
/// it.</b> A dial is resolved entirely from <see cref="Port"/>, <see cref="Role"/>,
/// <see cref="Grant"/>, and <see cref="RelayUrl"/>, and refuse-at-dial resolves a target to a
/// service by <em>port</em> (§10), so this travels for the plane's own logging and for a human
/// reading a captured envelope. Do not start routing on it: port is the resolution key on both
/// ends, and a second one would be a second source of truth.
/// </param>
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
/// <c>close-forward</c> — end one relay forward this machine holds (§8.3). The
/// counterpart to <see cref="OpenForwardCommand"/>, and the one thing that was
/// missing from it: §8.3 says an established splice "persists until the owning task
/// leaves <c>working</c>", which is a <em>bound</em>, and nothing enforced it. The
/// plane cleared the task's registered services and revoked its grants at that
/// moment, but a grant only gates <em>open</em> — a splice already running had no
/// handle anywhere in the system, so it survived until one of its peers happened to
/// drop. Where the worker was killed (a liveness loss tree-kills it, #84) its sockets
/// died by RST and the splice ended by accident; where nothing was killed —
/// <c>report_result</c>, <c>cancel_task</c> — the tunnel stayed up past the task that
/// authorized it, and so did the consumer end's idle loopback listener.
///
/// <para><b>This is not the severing §8.3 forbids.</b> That prohibition is about
/// grant expiry and byte ceilings cutting a live database session or websocket
/// mid-flight (§9.10) — policy interrupting work in progress. Leaving <c>working</c>
/// is the opposite: the authority the splice ran under is gone, and §8.3 names that
/// exact moment as its end.</para>
///
/// <para><b>Additive and skew-compatible</b> like every §10 addition since the freeze:
/// a <c>docketd</c> that predates it rejects the envelope at the wire boundary
/// (<see cref="RunnerWire.DecodeCommand(string)"/> returns null on an unknown type) and
/// goes on serving its splice exactly as it does today — the pre-fix behaviour, not a
/// crash. A newer <c>docketd</c> talking to an older plane simply never receives one.</para>
/// </summary>
/// <param name="Task">
/// The task this forward serves — <b>correlation, not the close key.</b> §10 requires a
/// task id on every message and this is it: the runner logs it and its handle span keys
/// off it, exactly as <see cref="OpenForwardCommand.ServiceName"/> travels for
/// diagnostics while <see cref="OpenForwardCommand.Port"/> does the resolving. Which end
/// of the forward it names therefore follows whichever end the plane addressed (a
/// worker's consumer end names its own task, a Lead's names the producer's — see
/// <c>ForwardOrchestrator</c>), and a runner that resolved by it instead would have to
/// know that distinction.
/// </param>
/// <param name="ForwardId">
/// The forward to close — the resolution key, and unambiguous because a forward id is
/// unique per grant and is what both the relay and <c>RelayForwarder</c> already key
/// their pairing on. Closing one this machine does not hold is a no-op: §10 commands are
/// best-effort against a live machine, and a forward that already ended is exactly what
/// the plane wanted.
/// </param>
public sealed record CloseForwardCommand(TaskId Task, string ForwardId) : RunnerCommand;

/// <summary>
/// <c>read-transcript</c> — read one byte range of one captured transcript stream
/// this machine holds, or (<see cref="Ordinal"/> <c>0</c>) inventory what it holds
/// for the task (§12 serving). The <b>only pull in the vocabulary</b>: every other
/// member is the plane commanding or the runner reporting, while this is the plane
/// asking a question that exactly one <see cref="TranscriptChunkEvent"/> answers,
/// correlated by <see cref="RequestId"/>.
///
/// <para>Pull-per-range is what keeps transcript bulk off the control channel's
/// critical path (§10 channel separation): the plane asks for the next range only
/// once the reader has taken the last, so at most one chunk is ever in flight and a
/// heartbeat or a <c>kill</c> waits behind one frame rather than a backlog. The
/// runner sends the file's bytes and interprets nothing (§2.6, §13) — redaction is
/// deliberately unresolved, so serving is operator-only and verbatim (§16).</para>
/// </summary>
/// <param name="RequestId">Opaque correlation id; the reply carries it back verbatim.</param>
/// <param name="Ordinal">The worker-instance ordinal to read, or <c>0</c> to inventory the task.</param>
/// <param name="Stream"><c>stdout</c> | <c>stderr</c> — which captured stream (see <c>TranscriptStore</c>).</param>
/// <param name="Offset">Byte offset into the on-disk file to resume at.</param>
/// <param name="MaxBytes">Cap on the bytes this reply may carry.</param>
public sealed record ReadTranscriptCommand(
    TaskId Task,
    string RequestId,
    int Ordinal = 0,
    string Stream = TranscriptStreams.Stdout,
    long Offset = 0,
    int MaxBytes = TranscriptStreams.DefaultMaxBytes) : RunnerCommand;

/// <summary>
/// <c>start-process</c> (§10) — an agent-started background process.
///
/// <para><b>A process is not a service, and the distinction does real work.</b> A
/// <em>service</em> is operator-declared in config, restart-supervised, and bound to the
/// machine generation — a daemon. A <em>process</em> is agent-started over this wire, never
/// restarted, its exit recorded, and cleaned up by Lead orchestration — a job. Same
/// supervision substrate, same machine tagging, same stray-sweep bound; different
/// declaration source, restart policy, and name. Neither is a Docket <em>task</em>.</para>
///
/// <para><b>Machine-scoped, not task-scoped.</b> The task id here is provenance and nothing
/// more: the process outlives the worker's turn, the task blocking, and the task completing.
/// It ends on an explicit <see cref="StopProcessCommand"/>, on its own exit, or with the
/// daemon (the stray sweep). Cleanup is orchestration — a Lead sends a continuation task and
/// the resumed agent stops what it started — because a process that survived its task's
/// state machine cannot be reclaimed by that state machine.</para>
/// </summary>
/// <param name="Name">Machine-scoped identity, validated against the same allowlist a
/// config-declared service name faces. Unique across processes <em>and</em> services on this
/// machine: one namespace, so a collision is always reported rather than resolved silently.</param>
/// <remarks>
/// <b>Ports are entirely out of scope here.</b> This is a process manager: it keeps a process
/// alive and reports how it ended. Reachability is a different noun with its own surface —
/// §8.2 <c>register_service</c> — and there is no overlap to reconcile. If a process happens to
/// listen on something, that is the agent's business, exactly as if it had started the server
/// from a shell; a port clash between two processes is the agent's problem too, consistent with
/// there being no restarts. Processes are therefore invisible to refuse-at-dial by construction,
/// because they declare nothing to dial.
/// </remarks>
/// <param name="OpenStdin">
/// Whether the child gets a usable stdin pipe. <b>Default false</b>: the common case is
/// fire-and-forget — a build, a dev server, a watcher — which gets the simplest spawn with
/// nothing held open, and its first read returns EOF rather than blocking on input nobody will
/// send. Closing is done by redirecting stdin and closing it at once, which is the portable
/// choice: .NET has no cross-platform null-device handle, and leaving stdin un-redirected would
/// hand the child whatever docketd inherited, which is not a defined thing to give it.
///
/// <para>Ask for true when you intend to talk to the process — <c>write-process</c> is therefore
/// <b>opt-in by construction</b>, and so is the graceful EOF half of <c>stop-process</c>. A
/// process started without stdin can only be stopped hard.</para>
///
/// <para>Either way it is <b>not</b> a terminal. A program that changes behaviour because
/// <c>isatty</c> is false behaves the same however this is set; a real TTY needs a pty, which is
/// out of scope. Opening stdin buys you a writable pipe, not interactivity.</para>
/// </param>
public sealed record StartProcessCommand(
    TaskId Task,
    string RequestId,
    string Name,
    IReadOnlyList<string> Spawn,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Env = null,
    bool OpenStdin = false) : RunnerCommand;

/// <summary>
/// <c>stop-process</c> (§10) — end an agent-started process. Machine-scoped: any worker
/// dispatched to this machine may stop any process on it, which is what lets a Lead's
/// cleanup continuation — a <em>new</em> task id — tidy up what an earlier task started.
/// </summary>
public sealed record StopProcessCommand(
    TaskId Task, string RequestId, string Name) : RunnerCommand;

/// <summary>
/// <c>write-process</c> (§10) — write to an agent-started process's stdin.
///
/// <para><b>This is a pipe, not a TTY</b>, and that is not a detail. Programs that check
/// whether stdin is a terminal behave differently on one: no interactive prompts, block
/// buffering instead of line buffering, and some refuse outright. A password prompt that
/// reads <c>/dev/tty</c> never sees these bytes at all, and a curses/TUI program will not
/// work. The reply confirms the bytes were accepted by the pipe — never that the program
/// understood them; the answer, if any, appears in the log file.</para>
/// </summary>
/// <param name="Data">UTF-8 text, capped at <see cref="ProcessStdin.MaxBytes"/>. Several
/// writes for more. No binary in v1.</param>
/// <param name="AppendNewline">Append a newline, the default: line-oriented is the 95% case
/// (REPLs, command-driven daemons) and a partial write is the exception.</param>
public sealed record WriteProcessCommand(
    TaskId Task, string RequestId, string Name, string Data, bool AppendNewline = true) : RunnerCommand;

/// <summary>Limits on <c>write-process</c> payloads (§10), shared by both sides.</summary>
public static class ProcessStdin
{
    /// <summary>Cap on one write, matching the in-band prose discipline elsewhere (16 KB).</summary>
    public const int MaxBytes = 16 * 1024;
}

/// <summary>The reply to <see cref="StartProcessCommand"/> (§10).</summary>
/// <param name="LogPath">Where this run's output is captured on the machine. The declaring
/// agent is on that machine, so it reads its own process's output with ordinary file tools —
/// no serving path, no redaction question (§16 open question 8).</param>
public sealed record ProcessStartedEvent(
    TaskId Task,
    string RequestId,
    string Name,
    bool Started,
    string? Refusal = null,
    string? LogPath = null) : RunnerEvent;

/// <summary>The reply to <see cref="StopProcessCommand"/> (§10).</summary>
/// <param name="ExitCode">The code it ended with, when the machine observed one.</param>
public sealed record ProcessStoppedEvent(
    TaskId Task, string RequestId, string Name, bool Stopped,
    int? ExitCode = null, string? Refusal = null) : RunnerEvent;

/// <summary>The reply to <see cref="WriteProcessCommand"/> (§10). Confirms the pipe accepted
/// the bytes, never that the program understood them.</summary>
public sealed record ProcessWrittenEvent(
    TaskId Task, string RequestId, string Name, bool Written,
    int Bytes = 0, string? Refusal = null) : RunnerEvent;

/// <summary>The closed refusal vocabulary for the §10 process commands.</summary>
public static class ProcessRefusals
{
    /// <summary>The profile does not permit agent-started processes (the operator's choice).</summary>
    public const string ProfileNotPermitted = "profile-not-permitted";

    /// <summary>The machine's cap on agent-started processes is reached.</summary>
    public const string CapReached = "cap-reached";

    /// <summary>The name is not a valid machine-local identifier.</summary>
    public const string InvalidName = "invalid-name";

    /// <summary>Empty argv — nothing to run.</summary>
    public const string InvalidSpawn = "invalid-spawn";

    /// <summary>That name is already held on this machine, by a process or a service.</summary>
    public const string NameTaken = "name-taken";

    /// <summary>The process could not be started at all (bad argv, missing binary).</summary>
    public const string SpawnFailed = "spawn-failed";

    /// <summary>No agent-started process by that name on this machine.</summary>
    public const string NoSuchProcess = "no-such-process";

    /// <summary>It is not running, so there is nothing to stop or write to.</summary>
    public const string NotRunning = "not-running";

    /// <summary>Its stdin is closed or the pipe broke.</summary>
    public const string StdinUnavailable = "stdin-unavailable";

    /// <summary>
    /// It was started without a stdin pipe — the default. Distinct from a wrong name and from a
    /// broken pipe: the caller learns the process exists and is running, and that writing to it
    /// required asking for <c>open_stdin</c> at start time.
    /// </summary>
    public const string StdinNotOpened = "stdin-not-opened";

    /// <summary>The payload exceeds <see cref="ProcessStdin.MaxBytes"/>.</summary>
    public const string PayloadTooLarge = "payload-too-large";
}

/// <summary>The two captured stream names and the default range size (§12), shared by
/// both sides so the wire strings live in one place.</summary>
public static class TranscriptStreams
{
    public const string Stdout = "stdout";
    public const string Stderr = "stderr";

    /// <summary>Default bytes per range: big enough that a 50 MiB stream is a couple
    /// hundred round trips, small enough that one frame's socket occupancy stays far
    /// inside the machine-liveness window (§10).</summary>
    public const int DefaultMaxBytes = 256 * 1024;

    public static bool IsKnown(string? stream) => stream is Stdout or Stderr;
}

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

/// <summary><c>tool-call</c> — a progress signal derived from harness hooks; the
/// per-task no-progress clock keys off it (§2.2, §10). Carries no token counts.</summary>
public sealed record ToolCallEvent(TaskId Task, string Tool, DateTimeOffset At) : RunnerEvent;

/// <summary>
/// <c>usage-reported</c> — <b>the harness's own account of what a dispatch consumed</b>
/// (§10 telemetry ingest, §12 measured view). One event per model per report, carrying that
/// model's token counts and, where the harness states one, its cost in USD.
///
/// <para><b>This is a claim, not a derivation (§2 principle 2).</b> Every number here was
/// computed by the harness and relayed verbatim; docketd does no arithmetic on it beyond the
/// normalization below, and the plane none at all. A harness can under-report, mis-report, or
/// report nothing — which is why nothing is enforced on it and why the §12 section that
/// renders it is visually separated from everything the plane derives itself.</para>
///
/// <para><b>Additive and optional, like every field added to this frozen contract since.</b>
/// A <c>docketd</c> that predates it simply never sends one, and the plane's measured view
/// then shows the honest empty state rather than a zero — an absence of measurement is not a
/// measurement of nothing (the same distinction §9.10's relay bytes draw).</para>
///
/// <para><b>The four token buckets are DISJOINT, and that took normalizing</b> because the two
/// harnesses do not agree on what "input" means. Claude counts uncached prompt tokens in
/// <c>input_tokens</c> and its cache hits separately, so its buckets are already disjoint.
/// Codex counts the WHOLE prompt in <c>input_tokens</c> with <c>cached_input_tokens</c> as a
/// subset of it — its own <c>non_cached_input()</c> subtracts one from the other for display.
/// Summing the four as reported would therefore double-count a Codex worker's cache hits, so
/// docketd subtracts where a profile declares the subset relationship
/// (<c>usage_cached_is_subset</c>) and what arrives here is always four buckets that add up.
/// The normalization is declared per profile as data, never inferred from a harness name.</para>
///
/// <para><see cref="CostUsd"/> is null where the harness reports no cost at all — Codex has no
/// cost figure anywhere, on its stream or in its metrics. The plane does not multiply tokens
/// into dollars to fill the gap; a derived figure and a reported one are different kinds of
/// claim and §12 keeps them apart (see <c>ModelPricing</c>).</para>
///
/// <para><b><see cref="Model"/> is the harness's own name for the model, or null when it names
/// none.</b> Nothing else may fill it: a model the PLANE asserted — from a profile's declaration
/// or a spawn argv — rendered inside a section labelled "reported by the harness" would be the
/// plane's claim wearing the harness's label, which is precisely the confusion §2 principle 2 and
/// the §12 visual separation exist to prevent. Claude names its models per model and a single
/// dispatch legitimately reports several; Codex names none on any stream event, so a Codex report
/// arrives unattributed and §12 renders "not reported" against real token counts. That empty state
/// is the honest answer, not a gap to be filled.</para>
///
/// <para><see cref="ReasoningOutputTokens"/> is a <b>subset of</b>
/// <see cref="OutputTokens"/>, not an addition to it — Codex breaks out the reasoning portion
/// of its output and Claude's stream does not expose one. Null where unreported. Anyone
/// tempted to add it into a total should read that sentence twice.</para>
///
/// <para><b>Cumulative-to-date for the dispatch, not a delta.</b> Each report supersedes the
/// last for its (task, model) pair, so the plane keeps the high-water mark and a report lost
/// to §10's best-effort ring costs nothing but freshness. A delta would silently undercount
/// on exactly that drop, and an undercount in a spend figure is the failure this shape
/// exists to avoid.</para>
/// </summary>
public sealed record UsageReportedEvent(
    TaskId Task,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long? ReasoningOutputTokens,
    decimal? CostUsd,
    DateTimeOffset At) : RunnerEvent;

/// <summary><c>subagent-spawned</c> — subagent lineage where the harness emits
/// it; progressive enhancement, not a given (§10 telemetry ingest).</summary>
public sealed record SubagentSpawnedEvent(
    TaskId Task, string? AgentId, string? ParentAgentId, DateTimeOffset At) : RunnerEvent;

/// <summary>
/// <c>turn-ended</c> — an ACP worker finished a turn and its session is still open
/// (<c>ideas/sessions.md</c> stage 1). The event the task model could not have: there, a
/// finished turn and a dead process were the same observation, so <c>exited</c> carried
/// both meanings and neither precisely.
///
/// <para><see cref="StopReason"/> is the agent's own word for why the turn ended —
/// <c>end_turn</c>, <c>max_tokens</c>, <c>max_turn_requests</c>, <c>refusal</c> or
/// <c>cancelled</c>. Under the task model all five arrived as "the process exited 0 without
/// reporting a result", which is why a worker that hit a token ceiling looked exactly like a
/// lazy one. That distinction matters more since the ACP migration, not less: ACP has no
/// <c>--max-turns</c> or budget flag, so <c>max_tokens</c> is now one of the few bounds that
/// announces itself.</para>
///
/// <para>Null when the turn ended without the agent saying why — a stream that died
/// mid-turn, or an agent that omits the field. Not invented, per §2 principle 2.</para>
/// </summary>
public sealed record TurnEndedEvent(TaskId Task, string? StopReason, DateTimeOffset At) : RunnerEvent;

/// <summary><c>exited</c> — the harness process ended (§10). Observed directly
/// by process supervision, not inferred.</summary>
public sealed record ExitedEvent(TaskId Task, int ExitCode, DateTimeOffset At) : RunnerEvent;

/// <summary><c>auth-failed</c> — reports structured facts (§11): operation,
/// target, error code, missing scope — which is what a remediation menu would render from
/// (§11). The plane persists the facts and shows them on the event log; no remediation menu
/// is built.</summary>
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
/// <summary>
/// <c>forward-closed</c> — a forward's splice ended, or it never opened (§8.3).
/// </summary>
/// <param name="Refusal">
/// Why it closed, when the machine knows something more useful than "an end closed".
/// Additive and optional: an older runner omits it and the plane falls back to its
/// generic sentence. The case that motivated it is §8.2's oldest hazard — a
/// registration outliving the process it advertises. Where the port belongs to a
/// docketd-supervised service that is <em>not</em> running, docketd refuses the dial
/// instead of connecting to whatever else may now hold that port, and this carries the
/// reason far enough that the consumer is told the service is down rather than being
/// handed a bare connection error.
/// </param>
public sealed record ForwardClosedEvent(
    TaskId Task, string ForwardId, string? Refusal = null) : RunnerEvent;

/// <summary>
/// <c>rebooted</c> — the runner restarted and adopted nothing (§10 runner
/// restart). The lone machine-scoped message: after a restart with no
/// persistence the runner has no task ids to name, which is the whole point —
/// every task it held requeues against the infrastructure counter (§6).
/// </summary>
public sealed record RebootedEvent(string MachineId, DateTimeOffset At) : RunnerEvent;

/// <summary>
/// <c>transcript-chunk</c> — the one reply to one <see cref="ReadTranscriptCommand"/>
/// (§12 serving), correlated by <see cref="RequestId"/>. Three shapes, distinguished
/// by which field is set: an inventory (<see cref="Instances"/>), one range of bytes
/// (<see cref="Text"/> plus <see cref="NextOffset"/>/<see cref="Eof"/>), or a refusal
/// (<see cref="Refusal"/>).
///
/// <para><b>This event does not ride the outbound ring.</b> The ring is bounded
/// drop-oldest with a gap marker (§10 buffering), and both of its properties are wrong
/// here: a dropped chunk is a silently corrupted read, and a few hundred chunks would
/// evict real liveness events from a shared ring. The reader publishes straight to the
/// channel instead, so transcript traffic can never starve a heartbeat or a
/// <c>kill</c>.</para>
///
/// <para><see cref="Text"/> is <b>verbatim</b> file content — Docket does not redact
/// transcripts (§13, §16 open question 8), which is why the plane serves them only to a
/// human operator and only for a terminal task.</para>
/// </summary>
/// <param name="Text">The range's bytes as UTF-8 text; empty for an inventory, EOF, or refusal.</param>
/// <param name="NextOffset">Where the next read resumes; may be short of
/// <c>Offset + MaxBytes</c> when the range ended mid-character.</param>
/// <param name="Eof">True when this range reached the end of the file.</param>
/// <param name="Refusal">Null on success, else why nothing could be read.</param>
/// <param name="Instances">The task's captured instances, on an inventory reply.</param>
public sealed record TranscriptChunkEvent(
    TaskId Task,
    string RequestId,
    string Text = "",
    long NextOffset = 0,
    bool Eof = false,
    string? Refusal = null,
    IReadOnlyList<TranscriptInstance>? Instances = null) : RunnerEvent;

/// <summary>One captured worker instance in a <see cref="TranscriptChunkEvent"/>
/// inventory (§12): its ordinal and each stream's size, so the dashboard can list what
/// is readable without the plane knowing anything about the layout. Sizes are the
/// on-disk byte counts; a stream never captured is <c>0</c>.</summary>
public sealed record TranscriptInstance(
    int Ordinal, long StdoutBytes, long StderrBytes, DateTimeOffset LastWrite);

/// <summary>
/// Why a <see cref="ReadTranscriptCommand"/> could not be answered (§12). Wire strings,
/// shared so the runner writes and the dashboard renders the same vocabulary.
/// </summary>
public static class TranscriptRefusals
{
    /// <summary>This machine holds no transcript directory for the task — it never ran
    /// here, capture was off for the profile that ran it, or the local prune window has
    /// already swept it. Indistinguishable at serve time, and deliberately one answer.</summary>
    public const string NoTranscript = "no-transcript";

    /// <summary>The task dir exists but not that ordinal/stream file.</summary>
    public const string UnknownInstance = "unknown-instance";

    /// <summary>The request named something outside the vocabulary (an unknown stream
    /// name, a negative ordinal or offset).</summary>
    public const string BadRequest = "bad-request";

    /// <summary>The file exists but could not be read (permissions, I/O, a vanished file).</summary>
    public const string ReadFailed = "read-failed";
}
