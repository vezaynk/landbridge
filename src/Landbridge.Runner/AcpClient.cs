using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Docket.Contracts;
using Docket.Core;

namespace Docket.Runner;

/// <summary>
/// §10: the Agent Client Protocol client that <b>drives</b> a worker. JSON-RPC 2.0,
/// newline-delimited, over the worker's stdin/stdout — docketd sends <c>initialize</c>,
/// opens or loads a session, and sends the profile's <c>prompt</c> as the opening turn.
///
/// <para><b>The only way docketd talks to a worker.</b> It previously also read whatever
/// NDJSON a harness happened to print, with an <c>events.mapping</c> per vendor describing
/// where that harness kept its session id, its tool names and its token counters — up to
/// thirteen keys for one CLI, each a fact about someone else's output format. That mode is
/// gone. The shapes are in a spec now, so there is nothing left to map, and a new harness
/// is an entry point rather than an archaeology exercise.</para>
///
/// <para><b>An agent must be spoken to.</b> One spawned and left alone does nothing, which
/// is why the prompt lives on the profile rather than the argv, why stdin is the request
/// channel rather than a dead pipe, and why the session ref arrives as a JSON-RPC
/// <em>result</em> rather than being fished out of a log line.</para>
///
/// <para><b>The dead-man's switch survives, and the spec agrees with it.</b> ACP's stdio
/// transport defines shutdown as "the client terminates the subprocess after closing
/// stdin", which is exactly docketd's convention: the held write end means docketd is
/// alive, and its EOF means docketd is gone. The two mechanisms coincide, so the switch
/// costs nothing here — and the per-profile opt-out it used to need is unreachable, because
/// a harness that blocked reading a held-open pipe was blocking on a <em>prompt</em>, and
/// stdin no longer carries one.</para>
///
/// <para><b>Stop is a real cancel.</b> The transport forbids writing anything to the agent's
/// stdin that is not an ACP message, so there is no free-text wind-down turn to write —
/// and nothing lost by that, since <c>session/cancel</c> is a notification the agent is
/// specified to honour, ending its turn with a <c>cancelled</c> stop reason.
/// <see cref="ProcessSupervisor.StopAsync"/> sends it before the deadline kill backstops
/// it.</para>
///
/// <para><b>Resume without a respawn (§11).</b> A dispatch carrying a resume ref takes
/// <c>session/load</c> instead of <c>session/new</c> — same process, same handshake, no
/// second spawn and no resume argv. Gated on the agent's <c>loadSession</c> capability,
/// which defaults to false in the spec: an agent that does not declare it cold-starts with
/// a loud line rather than silently discarding the transcript. Every agent measured on
/// 2026-08-15 declares it (<c>tools/acp-probe</c>).</para>
///
/// <para><b>The session outlives the turn</b> (<c>ideas/sessions.md</c>): after a turn ends
/// this client stays, taking follow-up turns on the same session, so a worker that asks a
/// question can be answered without a redispatch.</para>
///
/// <para><b>AOT (§10).</b> Reads go through <see cref="JsonDocument"/> and writes through
/// <see cref="Utf8JsonWriter"/> — both reflection-free — so no <c>JsonSerializerContext</c>
/// entry is needed and the runner stays clean under <c>IsAotCompatible</c>.</para>
/// </summary>
public sealed class AcpClient
{
    /// <summary>
    /// The protocol MAJOR version this client speaks. Every shipping agent measured
    /// on 2026-08-15 — <c>claude-agent-acp</c> 0.68.0, <c>codex-acp</c> 1.3.0,
    /// <c>opencode</c> 1.18.18 — answers <b>1</b>, and this client implements the
    /// v1 methods (<c>session/new</c>, <c>session/load</c>, <c>session/prompt</c>,
    /// <c>session/update</c>, <c>session/cancel</c>, <c>session/request_permission</c>,
    /// <c>session/set_config_option</c>). Offering 2 would claim v2 shapes we do
    /// not speak. <see cref="NegotiatedProtocolVersion"/> records what came back.
    /// </summary>
    public const int LatestProtocolVersion = 1;

    /// <summary>
    /// The oldest version this client can hold a session over. Same as
    /// <see cref="LatestProtocolVersion"/>: this is a v1 client.
    /// </summary>
    public const int OldestProtocolVersion = 1;

    /// <summary>
    /// ACP's "authenticate first" refusal. An agent that answers this to <c>session/new</c>
    /// is not broken and is not misconfigured — it is asking for the handshake step
    /// <see cref="AuthenticateAsync"/> exists to run. Matched on the code rather than the
    /// message, which is the agent's own prose (<c>codex-acp</c> says "Authentication
    /// required").
    /// </summary>
    public const int AuthRequiredCode = -32000;

    /// <summary>
    /// What docketd calls itself in <c>clientInfo</c>. Read from the assembly rather than
    /// written as a constant so it cannot drift; agents log it, and a wrong version in
    /// someone else's log is a wasted afternoon.
    /// </summary>
    private static readonly string ClientVersion =
        typeof(AcpClient).Assembly.GetName().Version?.ToString() ?? "0.0";

    private readonly SessionId _task;
    private readonly OutboundEventRing _ring;
    private readonly TimeProvider _clock;
    private readonly AcpSessionRequest _request;
    private readonly Action<string>? _onSessionId;
    private readonly Action<string>? _rawLineSink;
    private readonly Action<string> _warn;
    private readonly Func<AcpPermissionAsk, CancellationToken, Task<AcpPermissionDecision>>? _requestPermission;
    private AcpResult? _lastSessionResult;

    private readonly ConcurrentDictionary<long, TaskCompletionSource<AcpResult>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Tool calls already reported, keyed by <c>toolCallId</c>. ACP sends one
    /// <c>tool_call</c> and then any number of <c>tool_call_update</c>s as the call moves
    /// through its status values, so without this one call would move the progress clock
    /// several times — the same mistake as mapping Codex's <c>item.updated</c> alongside
    /// <c>item.started</c>. Touched only by the single-threaded read loop.
    /// </summary>
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>
    /// Agent-request methods already refused and reported, so the operator-facing line is
    /// once per method per task rather than once per call. Touched only by the
    /// single-threaded read loop.
    /// </summary>
    private readonly HashSet<string> _declined = new(StringComparer.Ordinal);

    /// <summary>
    /// Follow-up turns waiting to be sent (<c>ideas/sessions.md</c> stage 1). Unbounded and
    /// never dropped: a queued follow-up is usually the answer a worker is blocked on, and
    /// silently discarding one would strand the session waiting for something that already
    /// arrived. Depth is bounded in practice by the plane, which sends one message at a time.
    /// </summary>
    private readonly Channel<string> _followUps = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>
    /// Running totals for the whole session, in the four disjoint buckets §12 measures.
    /// Cumulative rather than per-turn on purpose: <c>SessionStore.RecordUsageAsync</c> keeps a
    /// high-water mark per bucket, so a client that reported each turn separately would
    /// leave the row holding the <em>largest</em> turn instead of the session's spend.
    /// Feeding it a monotonically rising total makes the max a no-op and the last report the
    /// truth. Touched only by the single-threaded read loop and the turn it drives.
    /// </summary>
    private long _inputTokens, _outputTokens, _cacheReadTokens, _cacheWriteTokens;

    /// <summary>
    /// The most recent dollar figure the agent put on this session, or null if it has never
    /// named one. See <see cref="RecordUsageUpdate"/> for why an explicit zero counts as
    /// "never".
    /// </summary>
    private decimal? _costUsd;

    /// <summary>Method ids the agent declared at <c>initialize</c>, in its own order.</summary>
    private readonly List<string> _authMethods = [];

    private TextWriter? _stdin;
    private long _nextId;
    private string? _sessionId;
    private bool _cancelSent;

    /// <summary>Well-formed JSON-RPC lines seen, for the silent-stream report at the end.</summary>
    private int _messages;

    public AcpClient(
        SessionId task,
        OutboundEventRing ring,
        TimeProvider clock,
        AcpSessionRequest request,
        Action<string>? onSessionId = null,
        Action<string>? rawLineSink = null,
        Action<string>? warn = null,
        Func<AcpPermissionAsk, CancellationToken, Task<AcpPermissionDecision>>? requestPermission = null)
    {
        _task = task;
        _ring = ring;
        _clock = clock;
        _request = request;
        _onSessionId = onSessionId;
        _rawLineSink = rawLineSink;
        _warn = warn ?? Console.Error.WriteLine;
        _requestPermission = requestPermission;
    }

    /// <summary>The protocol version the agent agreed to, or null before the handshake.</summary>
    public int? NegotiatedProtocolVersion { get; private set; }

    /// <summary>The agent's declared <c>loadSession</c> capability (§11 resume).</summary>
    public bool AgentSupportsLoadSession { get; private set; }

    /// <summary>The agent's declared <c>mcpCapabilities.http</c>, which is what carries the plane.</summary>
    public bool AgentSupportsHttpMcp { get; private set; }

    /// <summary>The stop reason from the most recent turn, or null if none has ended.</summary>
    public string? StopReason { get; private set; }

    /// <summary>Turns completed on this session — one for the opening prompt, one per follow-up.</summary>
    public int Turns { get; private set; }

    /// <summary>
    /// Runs one worker's whole ACP conversation: starts the read loop, drives the
    /// handshake and the prompt turn, and returns when the turn ends or the stream does.
    ///
    /// <para>The read loop is also the drain that keeps the worker's stdout pipe from
    /// filling, so it must run for the process's whole lifetime and must never throw into
    /// its host — a torn-down pipe or a cancelled token ends it quietly.</para>
    /// </summary>
    public async Task RunAsync(TextReader stdout, TextWriter stdin, CancellationToken ct)
    {
        _stdin = stdin;

        // The pending-request failure belongs to the read loop's own completion, NOT to the
        // sequence below it: a worker that dies mid-handshake closes its stdout while
        // `DriveAsync` is still awaiting a response, and nothing else will ever arrive to
        // complete it. Failing the pending requests anywhere downstream of the await would
        // deadlock — the drain would be finished, the driver would wait forever, and the
        // task would hang with no event and no diagnosis.
        var reading = Task.Run(
            async () =>
            {
                try
                {
                    await ReadLoopAsync(stdout, ct).ConfigureAwait(false);
                }
                finally
                {
                    FailPending(new AcpProtocolException(
                        "the agent's stdout ended before this request was answered"));

                    // The session dies with the stream. Both unblocks below are load-bearing
                    // and for the same reason: when the worker's process ends, DriveAsync is
                    // waiting on something that will never arrive — either a response to a
                    // turn in flight, or the next follow-up on an idle session. Neither wait
                    // can be broken from anywhere downstream of DriveAsync, because RunAsync
                    // awaits DriveAsync first, so it has to happen here, on the read loop's
                    // own completion. Getting this wrong hangs the drive loop for the life of
                    // the daemon, with no event and no diagnosis.
                    _followUps.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        try
        {
            await DriveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Teardown, or the worker died mid-conversation. Its exit code is the louder
            // signal and the supervisor already reports it; nothing to add here.
        }
        catch (AcpProtocolException e)
        {
            // Only a failed initialize / session/new is a handshake. A later
            // stdout-end (dispose killing connect while session/prompt is still
            // open) used to print this same line and claim the worker did no
            // work — after report_result had already landed.
            if (_sessionId is null)
            {
                _warn(
                    $"docketd: task {_task.Value}: the ACP handshake failed — {e.Message}. The worker was " +
                    "spawned but never reached a prompt turn, so it did no work; check that this profile's " +
                    "spawn argv really starts an ACP agent (§10).");
            }
        }

        // Whatever happened above, the stream still has to be drained to EOF: the worker
        // may keep writing, and an undrained pipe wedges it.
        await reading.ConfigureAwait(false);
        ReportSilentStream();
    }

    /// <summary>
    /// §10/§11 stop: the cooperative half, as a <c>session/cancel</c> notification. Sent at
    /// most once — a second stop for the same task is the deadline's business, not a second
    /// cancel — and best-effort by construction: the spec makes cancel a notification, so
    /// there is no ack to wait for and the agent's own <c>cancelled</c> stop reason is the
    /// only confirmation that exists. <see cref="ProcessSupervisor"/> arms the kill deadline
    /// regardless, so a silent agent still stops.
    /// </summary>
    public async Task<bool> CancelAsync(CancellationToken ct)
    {
        if (_sessionId is not { Length: > 0 } session || _cancelSent)
            return false;

        _cancelSent = true;

        // Ends the follow-up wait in DriveAsync. Without this a cancelled session would sit
        // blocked on a queue nobody will write to again, holding the drive loop open until
        // the kill deadline tore the pipe down underneath it — the stop would still land,
        // but by the ugliest available route.
        _followUps.Writer.TryComplete();

        try
        {
            await SendAsync(
                id: null,
                "session/cancel",
                w => w.WriteString("sessionId", session),
                ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // stdin is gone — the worker exited or is being torn down. The deadline kill
            // covers it, and reporting a failed cancel as a failed stop would be wrong.
            return false;
        }
    }

    /// <summary>
    /// The client half of the conversation: <c>initialize</c>, then a session, then the
    /// prompt turn. Each step's failure is a protocol error naming the step, because "the
    /// worker started and did nothing" is otherwise indistinguishable from a bad argv.
    /// </summary>
    private async Task DriveAsync(CancellationToken ct)
    {
        var init = await RequestAsync("initialize", WriteInitializeParams, ct).ConfigureAwait(false);
        ReadAgentCapabilities(init);

        var session = await OpenSessionAsync(ct).ConfigureAwait(false);

        if (session is not { Length: > 0 })
            throw new AcpProtocolException("the agent returned no sessionId");

        _sessionId = session;
        _onSessionId?.Invoke(session);
        await MaybeApplyConfigOptionsAsync(session, ct).ConfigureAwait(false);
        await MaybeSetSessionModeAsync(session, ct).ConfigureAwait(false);

        // A cancel that arrived between the spawn and here has nothing to cancel yet; now
        // that a session exists, honour it instead of prompting into a stop already asked for.
        if (_cancelSent)
            return;

        // The session outlives the turn (ideas/sessions.md stage 1). A cold start gets
        // the profile's opening prompt. A session/load is a wake, not a re-brief — the
        // follow-up says "read your assignment" (and, on the park bar, "report the
        // remembered value"). Sending the opening prompt again after load told the
        // worker to re-ask the question it had already asked.
        //
        // This loop is the whole shape change. Under the task model there was nothing to
        // loop over: a worker that finished a turn had also exited, so "the turn ended" and
        // "the conversation ended" were one observation. Here they are two, and only the
        // second ends this method.
        var resumed = _request.ResumeSessionRef is { Length: > 0 } && AgentSupportsLoadSession;
        var opening = resumed && _request.FollowUp is { Length: > 0 }
            ? _request.FollowUp
            : _request.Prompt;
        await PromptAsync(opening, ct).ConfigureAwait(false);

        try
        {
            while (!_cancelSent)
            {
                var next = await _followUps.Reader.ReadAsync(ct).ConfigureAwait(false);
                await PromptAsync(next, ct).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
            // CancelAsync completed the queue: the conversation is over by request.
        }
    }

    /// <summary>
    /// Sends one turn and waits for it to end, reporting the agent's own reason for ending
    /// it (<see cref="TurnEndedEvent"/>).
    ///
    /// <para>Serialized by construction: only <see cref="DriveAsync"/> calls this, one turn
    /// at a time, and a follow-up that arrives mid-turn waits in
    /// <see cref="_followUps"/> rather than racing the turn in flight. ACP does allow
    /// queued prompts on agents that declare it, but a second concurrent
    /// <c>session/prompt</c> against an agent that does not is undefined, and the queue
    /// costs nothing.</para>
    /// </summary>
    private async Task PromptAsync(string text, CancellationToken ct)
    {
        var session = _sessionId!;
        var result = await RequestAsync(
            "session/prompt",
            w =>
            {
                w.WriteString("sessionId", session);
                w.WriteStartArray("prompt");
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", text);
                w.WriteEndObject();
                w.WriteEndArray();
            },
            ct).ConfigureAwait(false);

        StopReason = result.Value.TryGetProperty("stopReason", out var sr) && sr.ValueKind == JsonValueKind.String
            ? sr.GetString()
            : null;
        Turns++;

        // §10 telemetry ingest, before the turn-ended event rather than after: turn-ended is
        // what the plane may act on (a turn that ended with nothing reported requeues), and
        // accounting for a dispatch should already be in when its fate is decided.
        //
        // No model name. Nothing in ACP attributes usage to a model — not the usage buckets,
        // not `usage_update` — so the report is deliberately unattributed rather than guessing
        // from the profile's argv, which names a CLI and not the model it happened to route to.
        var reportedTokens = AddTurnUsage(result.Value);
        if (reportedTokens || _costUsd is not null || HasAccumulatedUsage)
        {
            _ring.Enqueue(new UsageReportedEvent(
                _task,
                Model: null,
                _inputTokens,
                _outputTokens,
                _cacheReadTokens,
                _cacheWriteTokens,
                ReasoningOutputTokens: null,
                _costUsd,
                _clock.GetUtcNow()));
        }

        _ring.Enqueue(new TurnEndedEvent(_task, StopReason, _clock.GetUtcNow()));
    }

    /// <summary>
    /// Queues the profile's follow-up turn for this session — the runner's half of the §10
    /// <c>prompt</c> command. Returns false when the session cannot take one: it never
    /// opened, or it has been cancelled and is winding down.
    ///
    /// <para>Takes no text, deliberately. The turn is the profile's configured wake-up and
    /// says only "go read your assignment"; the input itself is pulled by the worker over
    /// the authenticated MCP call, which is what makes that read a receipt. See
    /// <see cref="Docket.Contracts.PromptCommand"/> for the three properties this
    /// protects.</para>
    ///
    /// <para>Deliberately a queue rather than a direct send. The plane's commands are
    /// best-effort and unordered with respect to a turn in flight, so a <c>prompt</c> that
    /// lands while the agent is mid-turn must wait for that turn rather than interleave
    /// with it.</para>
    /// </summary>
    public bool TryQueueFollowUp()
    {
        if (_sessionId is not { Length: > 0 } || _cancelSent)
            return false;
        return _followUps.Writer.TryWrite(_request.FollowUp);
    }

    private async Task<string?> NewSessionAsync(CancellationToken ct)
    {
        if (_request.ResumeSessionRef is { Length: > 0 } && !AgentSupportsLoadSession)
            _warn(
                $"docketd: task {_task.Value}: the plane handed back a resume ref but this agent does not " +
                "declare the ACP 'loadSession' capability, so the transcript cannot be reloaded and this " +
                "dispatch is a COLD START. Every redispatch of this task will be one (§11).");

        var result = await RequestAsync(
            "session/new",
            w =>
            {
                w.WriteString("cwd", _request.WorkDir);
                WriteMcpServers(w);
            },
            ct).ConfigureAwait(false);
        _lastSessionResult = result;

        return SessionIdOf(result);
    }

    /// <summary>
    /// §11 resume in-process. <c>session/load</c> takes the same <c>cwd</c> and
    /// <c>mcpServers</c> as <c>session/new</c> — the spec requires them to match — and the
    /// agent replays the whole conversation as <c>session/update</c> notifications before
    /// answering. Those replayed updates flow through the same read loop as live ones,
    /// which is why tool-call reporting is keyed on <c>toolCallId</c>: a replayed call must
    /// not move the progress clock a second time.
    /// </summary>
    private async Task<string?> LoadSessionAsync(string sessionRef, CancellationToken ct)
    {
        var result = await RequestAsync(
            "session/load",
            w =>
            {
                w.WriteString("sessionId", sessionRef);
                w.WriteString("cwd", _request.WorkDir);
                WriteMcpServers(w);
            },
            ct).ConfigureAwait(false);
        _lastSessionResult = result;

        // session/load's result carries no sessionId of its own — the ref we asked for IS
        // the session — so an agent that echoes one is honoured and one that does not is
        // not an error.
        return SessionIdOf(result) ?? sessionRef;
    }

    /// <summary>
    /// ACP <c>session/set_config_option</c> for each pair on the profile that
    /// this session advertised as a select listing that exact value. OpenCode
    /// ACP is why this exists: it defaults every session to
    /// <c>opencode/big-pickle</c> and ignores <c>opencode.json</c> (measured
    /// 2026-08-16). An unadvertised key, or a value the agent did not list, is
    /// skipped — not an error, and not a guess. Sequential on purpose: one
    /// pin can change the remaining options, so each call refreshes the
    /// advertisement used by the next.
    /// </summary>
    private async Task MaybeApplyConfigOptionsAsync(string sessionId, CancellationToken ct)
    {
        if (_request.ConfigOptions is not { Count: > 0 } wanted)
            return;

        foreach (var (configId, value) in wanted)
        {
            if (string.IsNullOrWhiteSpace(configId) || string.IsNullOrWhiteSpace(value))
                continue;
            if (_lastSessionResult is not { } result || !AdvertisesSelectValue(result.Value, configId, value))
                continue;

            _lastSessionResult = await RequestAsync(
                "session/set_config_option",
                w =>
                {
                    w.WriteString("sessionId", sessionId);
                    w.WriteString("configId", configId);
                    w.WriteString("value", value);
                },
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ACP <c>session/set_mode</c> when the profile named a mode this session
    /// advertised. Goose 1.46 defaults to <c>auto</c> (auto-approve); pinning
    /// <c>approve</c> is how a Docket profile keeps permissions on the protocol.
    /// Unadvertised is skipped, same as <see cref="MaybeApplyConfigOptionsAsync"/>.
    /// </summary>
    private async Task MaybeSetSessionModeAsync(string sessionId, CancellationToken ct)
    {
        if (_request.SessionMode is not { Length: > 0 } mode)
            return;
        if (_lastSessionResult is not { } result || !AdvertisesMode(result.Value, mode))
            return;

        _lastSessionResult = await RequestAsync(
            "session/set_mode",
            w =>
            {
                w.WriteString("sessionId", sessionId);
                w.WriteString("modeId", mode);
            },
            ct).ConfigureAwait(false);
    }

    private static bool AdvertisesMode(JsonElement session, string modeId)
    {
        if (!session.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Object)
            return false;
        if (!modes.TryGetProperty("availableModes", out var available) || available.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var mode in available.EnumerateArray())
        {
            if (mode.ValueKind == JsonValueKind.Object
                && mode.TryGetProperty("id", out var id)
                && id.GetString() == modeId)
                return true;
        }

        return false;
    }

    private static bool AdvertisesSelectValue(JsonElement session, string configId, string value)
    {
        if (!session.TryGetProperty("configOptions", out var options)
            || options.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var opt in options.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object)
                continue;
            if (!opt.TryGetProperty("id", out var id) || id.GetString() != configId)
                continue;
            if (!opt.TryGetProperty("options", out var choices) || choices.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind == JsonValueKind.Object
                    && choice.TryGetProperty("value", out var v)
                    && v.GetString() == value)
                    return true;
            }
        }
        return false;
    }

    private static string? SessionIdOf(AcpResult result) =>
        result.Value.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    private void WriteInitializeParams(Utf8JsonWriter w)
    {
        w.WriteNumber("protocolVersion", LatestProtocolVersion);

        // Declared honestly, which for docketd means declaring almost nothing. These
        // capabilities exist for an EDITOR: they let an agent see a file as it sits in an
        // unsaved buffer, and run a command in the user's terminal panel. A Docket worker
        // has neither — it has its own work dir and its own shell — so the agent doing its
        // own file and process I/O is not a degradation, it is the arrangement.
        //
        // The risk this takes, stated plainly because it is the one that could make an ACP
        // worker useless: an agent that routes ALL its shell and file access through the
        // client rather than performing it itself would, under this declaration, be unable
        // to do anything at all — and the symptom would read as a lazy model rather than a
        // wiring fault. That is why an incoming request for a declined capability is
        // reported (see AnswerAgentRequestAsync) instead of only being refused on the wire.
        // The three agents measured on 2026-08-15 all carry their own tools, so none of
        // them needs this; a fourth might.
        w.WriteStartObject("clientCapabilities");
        w.WriteStartObject("fs");
        w.WriteBoolean("readTextFile", false);
        w.WriteBoolean("writeTextFile", false);
        w.WriteEndObject();
        w.WriteBoolean("terminal", false);
        w.WriteEndObject();

        w.WriteStartObject("clientInfo");
        w.WriteString("name", "docketd");
        w.WriteString("title", "Docket runner");
        w.WriteString("version", ClientVersion);
        w.WriteEndObject();
    }

    /// <summary>
    /// Opens the session, authenticating first if the agent insists on it.
    ///
    /// <para><b>Lazily, on the agent's own signal</b>, rather than eagerly whenever
    /// <c>authMethods</c> is non-empty. A declared method means authentication is
    /// <em>available</em>, not that it is required — an agent already holding a valid
    /// credential accepts <c>session/new</c> straight away, and authenticating it anyway
    /// would be a round trip that can only fail. So the session request goes first and
    /// <see cref="AuthRequiredCode"/> is the agent asking; nothing else triggers this.</para>
    ///
    /// <para>Measured 2026-08-16: <c>codex-acp</c> 1.3.0 declares <c>api-key</c> and
    /// <c>chat-gpt</c> and refuses <c>session/new</c> with
    /// <c>-32000 "Authentication required"</c> until one is chosen;
    /// <c>claude-agent-acp</c> declares none and needs none. Before this the codex tier
    /// could not open a session at all — two transcript lines, initialize then the refusal.
    /// </para>
    ///
    /// <para>Retries exactly once. A second refusal is the credential being wrong rather
    /// than missing, and re-authenticating in a loop would turn a bad key into a spin.</para>
    /// </summary>
    private async Task<string?> OpenSessionAsync(CancellationToken ct)
    {
        try
        {
            return await RequestSessionAsync(ct).ConfigureAwait(false);
        }
        catch (AcpProtocolException e) when (e.Code == AuthRequiredCode)
        {
            await AuthenticateAsync(e, ct).ConfigureAwait(false);
            return await RequestSessionAsync(ct).ConfigureAwait(false);
        }

        Task<string?> RequestSessionAsync(CancellationToken token) =>
            _request.ResumeSessionRef is { Length: > 0 } resume && AgentSupportsLoadSession
                ? LoadSessionAsync(resume, token)
                : NewSessionAsync(token);
    }

    /// <summary>
    /// Runs ACP <c>authenticate</c> with one of the methods the agent declared at
    /// <c>initialize</c>.
    ///
    /// <para>The request carries a method id and nothing else — by design, in the spec: the
    /// credential itself is the agent's business, read from its own environment or config.
    /// That is the same split §13 already keeps for MCP, and it is why the fix here is a
    /// handshake step rather than anything that handles a secret.</para>
    ///
    /// <para>Which method is the profile's <c>auth_method</c>, required. An agent that
    /// demands authentication and a profile that did not name a method is a
    /// configuration miss, not a guess: the first declared method is often a
    /// browser login, and picking it unattended is how a headless worker hangs.
    /// The credential itself stays the agent's — this request carries only the
    /// id.</para>
    /// </summary>
    private async Task AuthenticateAsync(AcpProtocolException refusal, CancellationToken ct)
    {
        if (_request.AuthMethod is not { Length: > 0 } method)
        {
            var offered = _authMethods.Count == 0 ? "(none)" : string.Join(", ", _authMethods);
            _ring.Enqueue(new AuthFailedEvent(
                _task, "authenticate", "acp", "profile has no auth_method", null));
            throw new AcpProtocolException(
                "the agent requires authentication but this profile has no `auth_method`. "
                + $"Set it to one of the methods the agent declared: {offered}. "
                + "The credential stays in the profile's `env`; this key is only the method id (§10).",
                AuthRequiredCode);
        }

        try
        {
            await RequestAsync("authenticate", w => w.WriteString("methodId", method), ct)
                .ConfigureAwait(false);
        }
        catch (AcpProtocolException e)
        {
            // Named, because the operator's next move depends on which method was tried:
            // a wrong key is a different fix from a method that wanted a browser.
            _ring.Enqueue(new AuthFailedEvent(
                _task, "authenticate", method, e.Message, null));
            throw new AcpProtocolException(
                $"the agent refused ACP authenticate with method '{method}' ({e.Message}). "
                + $"Declared methods: {(_authMethods.Count == 0 ? "(none)" : string.Join(", ", _authMethods))}. "
                + "Set the profile's `auth_method` to pick a different one, or give the harness "
                + "its credential in the profile's `env` (§10).",
                e.Code);
        }
    }

    private void ReadAgentCapabilities(AcpResult init)
    {
        if (init.Value.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var version))
        {
            NegotiatedProtocolVersion = version;

            // Deliberately NOT "anything but the latest is a warning". Every agent in the
            // wild answers 1, so that rule would put a line on every task of every ACP
            // profile — and a warning that fires always is a warning an operator learns to
            // scroll past, which costs more than it can ever report. Only a version outside
            // the range this client can actually hold a session over is worth saying.
            if (version < OldestProtocolVersion || version > LatestProtocolVersion)
                _warn(
                    $"docketd: task {_task.Value}: the agent negotiated ACP protocol version {version}, which " +
                    $"is outside the {OldestProtocolVersion}–{LatestProtocolVersion} range this client speaks. " +
                    "Continuing on the assumption that the session methods are unchanged, but if this task " +
                    "reads oddly — no session ref, no tool calls — that assumption is the first thing to " +
                    "doubt (§10).");
        }

        // Every method the agent will accept at `authenticate`, in the order it listed them.
        // Order is the only preference signal ACP offers — nothing marks a method as
        // non-interactive — so it is preserved verbatim and the first is the default pick.
        if (init.Value.TryGetProperty("authMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
        {
            _authMethods.Clear();
            foreach (var m in methods.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object && StringOf(m, "id") is { Length: > 0 } id)
                    _authMethods.Add(id);
        }

        if (!init.Value.TryGetProperty("agentCapabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
            return;

        AgentSupportsLoadSession = caps.TryGetProperty("loadSession", out var load)
            && load.ValueKind == JsonValueKind.True;

        if (caps.TryGetProperty("mcpCapabilities", out var mcp) && mcp.ValueKind == JsonValueKind.Object)
            AgentSupportsHttpMcp = mcp.TryGetProperty("http", out var http) && http.ValueKind == JsonValueKind.True;

        if (!AgentSupportsHttpMcp && _request.McpServers.Count > 0)
            _warn(
                $"docketd: task {_task.Value}: this agent does not declare the ACP 'mcpCapabilities.http' " +
                "capability, so the plane's MCP server cannot be handed to it over the wire. The worker will " +
                "start with NO docket tools unless the machine wires them in out of band — it cannot call " +
                "get_session or report_result, so it will do nothing useful (§10).");
    }

    /// <summary>
    /// §13: the plane's generated MCP config, translated into ACP's <c>mcpServers</c> array
    /// and handed over on <c>session/new</c>.
    ///
    /// <para>In stream mode the generated config is a file whose path substitutes into the
    /// argv, which works for claude and not for Codex, OpenCode or Grok — none of which has
    /// a <c>--mcp-config</c> equivalent. <c>profiles[].files</c> and <c>profiles[].env</c>
    /// (#112 G2/G3) closed that gap: such a profile can now write its own
    /// <c>config.toml</c> into the work dir with a real <c>{worker_token}</c> in it. So the
    /// gain here is narrower than it would have been, and still worth having — the server
    /// is a <em>parameter of the session</em>, so there is no file to write, no mode to get
    /// right, and no live bearer token resident on disk for the length of the task.</para>
    /// </summary>
    private void WriteMcpServers(Utf8JsonWriter w)
    {
        w.WriteStartArray("mcpServers");
        foreach (var server in _request.McpServers)
        {
            w.WriteStartObject();
            w.WriteString("type", "http");
            w.WriteString("name", server.Name);
            w.WriteString("url", server.Url);
            w.WriteStartArray("headers");
            foreach (var (name, value) in server.Headers)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("value", value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private async Task ReadLoopAsync(TextReader stdout, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await stdout.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                // §12 capture tee, before parsing and never allowed to affect the worker —
                // the same contract the terminal reader's sink has.
                if (_rawLineSink is not null)
                {
                    try { _rawLineSink(line); }
                    catch { /* capture is never allowed to affect the worker */ }
                }

                await DispatchLineAsync(line, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancelled on teardown, or the pipe was torn down by a kill mid-read. Either
            // way the conversation is over; end the drain quietly.
        }
    }

    private async Task DispatchLineAsync(string line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // The transport forbids non-ACP bytes on stdout, but a harness banner or a
            // panic trace still happens in the real world and must not kill the drain.
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            _messages++;
            var hasId = root.TryGetProperty("id", out var id) && id.ValueKind is not JsonValueKind.Null;
            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            // A method with an id is the agent asking US something; a method without one is
            // a notification; no method at all is an answer to something we asked.
            if (method is not null && hasId)
                await AnswerAgentRequestAsync(method, id, root, ct).ConfigureAwait(false);
            else if (method is not null)
            {
                // Grok reports spend on a vendor notification, not PromptResponse.usage
                // or session/update usage_update. Measured 2026-08-16: tokens land on
                // `_x.ai/session_notification` / `response_completed`, no dollar figure.
                if (method == "_x.ai/session_notification")
                    RecordXaiUsage(root);
                else
                    HandleNotification(method, root);
            }
            else if (hasId)
                CompletePending(id, root);
        }
    }

    /// <summary>
    /// The two notifications that carry something docketd's event vocabulary has a place for:
    /// tool calls (progress) and <c>usage_update</c> (§12 accounting). Everything else ACP
    /// streams — message chunks, thoughts, plans, mode changes — is conversation content,
    /// which §12 captures verbatim through the transcript tee and which the frozen runner
    /// vocabulary deliberately has no member for.
    /// </summary>
    private void HandleNotification(string method, JsonElement root)
    {
        if (method != "session/update")
            return;
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            return;
        if (!p.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            return;

        var kind = update.TryGetProperty("sessionUpdate", out var k) && k.ValueKind == JsonValueKind.String
            ? k.GetString()
            : null;

        if (kind == "usage_update")
        {
            RecordUsageUpdate(update);
            return;
        }

        // `tool_call` announces a call; `tool_call_update` reports its progress through
        // pending → in_progress → completed. Both are accepted because an agent may skip
        // straight to an update for a call it never announced, and the toolCallId guard
        // below is what keeps either shape to one event per call.
        if (kind is not ("tool_call" or "tool_call_update"))
            return;

        var id = update.TryGetProperty("toolCallId", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (id is not { Length: > 0 } || !_reported.Add(id))
            return;

        // ACP names a call for a human (`title`: "Reading configuration file") and
        // categorises it (`kind`: read/edit/execute/…). The title is what a person reading
        // the §12 event log wants; the kind is the honest fallback when an agent omits one.
        var name = StringOf(update, "title") ?? StringOf(update, "kind") ?? "tool";
        _ring.Enqueue(new ToolCallEvent(_task, name, _clock.GetUtcNow()));
    }

    /// <summary>
    /// The agent asking the client for something. Only <c>session/request_permission</c> is
    /// answered; anything else is refused with a JSON-RPC "method not found", which is the
    /// correct answer for a capability this client declared UNSUPPORTED at initialize and
    /// is far better than leaving the agent blocked on a request nobody will ever answer.
    /// </summary>
    private async Task AnswerAgentRequestAsync(string method, JsonElement id, JsonElement root, CancellationToken ct)
    {
        if (method == "session/request_permission")
        {
            var option = await ResolvePermissionOptionAsync(root, ct).ConfigureAwait(false);
            await SendResponseAsync(
                id,
                w =>
                {
                    w.WriteStartObject("outcome");
                    if (option is null)
                    {
                        // No option we can choose. `cancelled` is the spec's own answer for
                        // "the client is not going to decide this", and it lets the agent
                        // move on rather than hang.
                        w.WriteString("outcome", "cancelled");
                    }
                    else
                    {
                        w.WriteString("outcome", "selected");
                        w.WriteString("optionId", option);
                    }
                    w.WriteEndObject();
                },
                ct).ConfigureAwait(false);
            return;
        }

        await SendErrorAsync(id, -32601, $"docketd does not implement '{method}'", ct).ConfigureAwait(false);

        // Once per method per task. A refusal the agent swallows is the quietest way for a
        // worker to end up unable to touch the filesystem or run a command, and the
        // resulting task — started, no tool calls, nothing reported — looks exactly like a
        // model that could not be bothered. Naming the method turns that into a five-second
        // diagnosis.
        if (_declined.Add(method))
            _warn(
                $"docketd: task {_task.Value}: the agent asked docketd to perform '{method}' and was refused — " +
                "this client declares the ACP fs and terminal capabilities UNSUPPORTED, because a Docket " +
                "worker is expected to use its own work dir and its own shell. An agent that delegates all " +
                "of its file or command access to the client cannot work under that declaration, and this " +
                "line is the only sign of it: check whether this harness needs a client-side terminal (§10).");
    }

    /// <summary>
    /// §11 permission bridge, ACP edition. The agent asks via
    /// <c>session/request_permission</c>; this client puts the same
    /// <see cref="InputRequestKind.Permission"/> request on the plane that the
    /// old harness prompt-tool did, waits for a Lead or human verdict, and maps
    /// it onto the agent's own options.
    ///
    /// <para>Allow picks <c>allow_once</c> only — never <c>allow_always</c>, even
    /// as a fallback. A standing bypass is not a plane decision. Deny picks a
    /// reject option when the agent offered one, otherwise <c>cancelled</c>. A
    /// missing plane callback (tests that did not wire one) is also cancelled
    /// rather than auto-allowed.</para>
    /// </summary>
    private async Task<string?> ResolvePermissionOptionAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;

        var options = ReadPermissionOptions(p);
        if (options.Count == 0)
            return null;

        if (_requestPermission is null)
            return null;

        var ask = new AcpPermissionAsk(ToolOf(p), InputOf(p));
        AcpPermissionDecision decision;
        try
        {
            decision = await _requestPermission(ask, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _warn(
                $"docketd: task {_task.Value}: the plane permission bridge failed ({ex.GetType().Name}: {ex.Message}); " +
                "answering the agent with cancelled so it is not wedged inside the tool call.");
            return null;
        }

        if (decision.Allow)
        {
            // Never allow_always: that is a standing bypass in the agent, not a
            // one-shot plane decision. If the agent offered no allow_once, cancel
            // rather than promote the allow into a lasting grant.
            return FirstOfKind(options, "allow_once");
        }

        // Once per task, and it earns its place. A refused tool call is invisible from
        // everywhere else: the agent absorbs the refusal, writes a paragraph about why it
        // could not proceed, and ends its turn — which on the plane looks exactly like a
        // model that ignored its instructions. Measured 2026-08-16: seven real claude
        // dispatches ended that way, ~400-800 output tokens each, no tool calls, and nothing
        // anywhere said "denied". A session runs in the agent's DEFAULT permission mode
        // unless a profile says otherwise, and for claude-agent-acp that mode prompts before
        // every MCP tool call — so on a fleet with no Lead watching, the first `get_session` is
        // the call that gets refused and the worker never starts.
        if (_declined.Add("session/request_permission"))
            _warn(
                $"docketd: task {_task.Value}: the plane DENIED this worker's permission request for " +
                $"'{ask.Tool}'{(decision.Message is { Length: > 0 } why ? $" ({why})" : "")}. A worker whose " +
                "docket tools are denied cannot call get_session or report_result, so it will end its turn " +
                "having done nothing — check that a Lead is answering permission requests, or set this " +
                "profile to a permission mode that does not prompt (§10, §11).");

        return FirstOfKind(options, "reject_once") ?? FirstOfKind(options, "reject_always");
    }

    /// <summary>
    /// Takes the dollar figure off a <c>usage_update</c>. The rest of that notification —
    /// <c>used</c> and <c>size</c> — is a context-window gauge, not spend, and §12's measured
    /// view has nowhere honest to put it; a gauge written into a cumulative column would read
    /// as consumption that never happened.
    ///
    /// <para><b>An explicit zero is treated as no cost at all</b>, not as a free dispatch.
    /// Measured on 2026-08-16: <c>claude-agent-acp</c> priced a turn at $0.0949, and
    /// <c>opencode acp</c> reported <c>{"amount":0}</c> for a turn that plainly burned 14,321
    /// tokens — so a zero here means "this adapter does not compute cost", and recording
    /// $0.00 would assert the dispatch was free. That is the same mislabelling §2 principle 2
    /// forbids, and the reason the real-harness bar refuses to derive a cost for Codex.</para>
    /// </summary>
    private void RecordUsageUpdate(JsonElement update)
    {
        if (!update.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
            return;
        if (!cost.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number)
            return;
        if (!amount.TryGetDecimal(out var value) || value <= 0m)
            return;
        _costUsd = value;
    }

    /// <summary>
    /// Adds a finished turn's token buckets to the session totals, and answers whether the
    /// agent reported any.
    ///
    /// <para>ACP's <c>PromptResponse.usage</c> buckets are <b>disjoint</b>, which is what lets
    /// them map onto §12's four columns without a subset correction — the per-harness
    /// <c>usage_cached_is_subset</c> knob the stream mapping needed has no ACP equivalent
    /// because there is nothing to correct. Both agents measured on 2026-08-16 reconcile
    /// exactly: claude reported 6 + 866 + 61,019 + 6,701 = 68,592 = <c>totalTokens</c>, and
    /// opencode 99 + 14 + 14,208 = 14,321 = <c>totalTokens</c>. <c>totalTokens</c> is therefore
    /// derivable and is deliberately not stored.</para>
    /// </summary>
    private bool HasAccumulatedUsage =>
        _inputTokens + _outputTokens + _cacheReadTokens + _cacheWriteTokens > 0;

    /// <summary>
    /// Grok's <c>_x.ai/session_notification</c> usage: snake_case buckets, no cost.
    /// Added into the same session totals <see cref="AddTurnUsage"/> feeds, so the
    /// high-water-mark store still sees one rising report.
    /// </summary>
    private void RecordXaiUsage(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            return;
        if (!p.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            return;
        if (!update.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;

        AddSnake(usage, "input_tokens", ref _inputTokens);
        AddSnake(usage, "output_tokens", ref _outputTokens);
        AddSnake(usage, "cache_read_input_tokens", ref _cacheReadTokens);
        AddSnake(usage, "cache_creation_input_tokens", ref _cacheWriteTokens);

        static void AddSnake(JsonElement o, string key, ref long total)
        {
            if (!o.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt64(out var n) || n < 0)
                return;
            total += n;
        }
    }

    private bool AddTurnUsage(JsonElement result)
    {
        if (!result.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var any = false;
        any |= Add(usage, "inputTokens", ref _inputTokens);
        any |= Add(usage, "outputTokens", ref _outputTokens);
        any |= Add(usage, "cachedReadTokens", ref _cacheReadTokens);
        any |= Add(usage, "cachedWriteTokens", ref _cacheWriteTokens);
        return any;

        static bool Add(JsonElement o, string key, ref long total)
        {
            if (!o.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt64(out var n) || n < 0)
                return false;
            total += n;
            return true;
        }
    }

    private static string ToolOf(JsonElement p)
    {
        if (p.TryGetProperty("toolCall", out var call) && call.ValueKind == JsonValueKind.Object)
            return StringOf(call, "title") ?? StringOf(call, "kind") ?? "tool";
        return StringOf(p, "title") ?? "tool";
    }

    private static string InputOf(JsonElement p)
    {
        if (p.TryGetProperty("toolCall", out var call) && call.ValueKind == JsonValueKind.Object
            && call.TryGetProperty("rawInput", out var raw) && raw.ValueKind is not JsonValueKind.Undefined
            && raw.ValueKind is not JsonValueKind.Null)
            return raw.GetRawText();
        return "{}";
    }

    private static List<(string Id, string? Kind)> ReadPermissionOptions(JsonElement p)
    {
        var list = new List<(string, string?)>();
        if (!p.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
                continue;
            if (StringOf(option, "optionId") is not { Length: > 0 } id)
                continue;
            list.Add((id, StringOf(option, "kind")));
        }
        return list;
    }

    private static string? FirstOfKind(List<(string Id, string? Kind)> options, string kind)
    {
        foreach (var option in options)
            if (option.Kind == kind)
                return option.Id;
        return null;
    }

    private void CompletePending(JsonElement id, JsonElement root)
    {
        if (!TryReadId(id, out var key) || !_pending.TryRemove(key, out var pending))
            return;

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = StringOf(error, "message") ?? "the agent returned an error";
            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : 0;
            pending.TrySetException(new AcpProtocolException($"{message} (JSON-RPC {code})", code));
            return;
        }

        // Cloned because the owning JsonDocument is disposed the moment this line's `using`
        // block ends, and the awaiting caller reads the result well after that.
        var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
        pending.TrySetResult(new AcpResult(result));
    }

    private async Task<AcpResult> RequestAsync(string method, Action<Utf8JsonWriter> writeParams, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<AcpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;

        try
        {
            await SendAsync(id, method, writeParams, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        // No timeout here on purpose. A prompt turn legitimately runs for hours, and the
        // clocks that decide whether a worker is stuck already exist and are better
        // informed than a fixed number here would be: the plane's per-task liveness moves
        // on the tool-call events this client emits, the no-progress ceiling bounds a
        // silent one, and the stop deadline bounds a cancelled one. A second timeout here
        // would only be able to disagree with them.
        using (ct.Register(static s => ((TaskCompletionSource<AcpResult>)s!).TrySetCanceled(), pending))
            return await pending.Task.ConfigureAwait(false);
    }

    /// <summary>A request or a notification: <paramref name="id"/> null makes it the latter.</summary>
    private Task SendAsync(long? id, string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct) =>
        WriteMessageAsync(
            w =>
            {
                if (id is { } numeric)
                    w.WriteNumber("id", numeric);
                w.WriteString("method", method);
                if (writeParams is not null)
                {
                    w.WriteStartObject("params");
                    writeParams(w);
                    w.WriteEndObject();
                }
            },
            ct);

    /// <summary>A successful response to an agent-initiated request, echoing its id verbatim.</summary>
    private Task SendResponseAsync(JsonElement id, Action<Utf8JsonWriter> writeResult, CancellationToken ct) =>
        WriteMessageAsync(
            w =>
            {
                w.WritePropertyName("id");
                id.WriteTo(w);
                w.WriteStartObject("result");
                writeResult(w);
                w.WriteEndObject();
            },
            ct);

    private Task SendErrorAsync(JsonElement id, int code, string message, CancellationToken ct) =>
        WriteMessageAsync(
            w =>
            {
                w.WritePropertyName("id");
                id.WriteTo(w);
                w.WriteStartObject("error");
                w.WriteNumber("code", code);
                w.WriteString("message", message);
                w.WriteEndObject();
            },
            ct);

    /// <summary>
    /// Writes one JSON-RPC message as one line. Serialized on <see cref="_writeLock"/>
    /// because the transport is newline-delimited and messages MUST NOT contain embedded
    /// newlines: two concurrent writers — the driver mid-handshake and a
    /// <see cref="CancelAsync"/> arriving from the supervisor's stop path — would otherwise
    /// interleave into a line that parses as neither.
    /// </summary>
    private async Task WriteMessageAsync(Action<Utf8JsonWriter> writeBody, CancellationToken ct)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("the ACP client is not connected");

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            writeBody(w);
            w.WriteEndObject();
        }

        // Utf8JsonWriter never emits a raw newline inside a value (it escapes them), so the
        // one appended here is unambiguously the frame delimiter the transport requires.
        var line = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stdin.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailPending(Exception e)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var pending))
                pending.TrySetException(e);
    }

    /// <summary>
    /// The ACP counterpart of the terminal reader's silent-stream warning (§11), and it
    /// diagnoses the same failure: a worker whose whole run produced no session and no tool
    /// call. Here it means the spawn argv is not an ACP agent — the far more likely mistake
    /// than a mismapped stream, since ACP has nothing to map.
    /// </summary>
    private void ReportSilentStream()
    {
        if (_sessionId is not null || _messages > 0)
            return;

        _warn(
            $"docketd: task {_task.Value}: protocol is 'acp' and the worker produced no JSON-RPC message at " +
            "all — not even a reply to initialize. Its spawn argv is almost certainly not an ACP agent (an " +
            "ACP-mode profile spawns something like `opencode acp`, not the harness's ordinary run command). " +
            "The task did no work (§10).");
    }

    private static bool TryReadId(JsonElement id, out long key)
    {
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out key))
            return true;

        // A string id is legal JSON-RPC and some agents echo ours back as one.
        if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out key))
            return true;

        key = 0;
        return false;
    }

    private static string? StringOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Everything one ACP session needs that comes from the dispatch rather than the profile:
/// where the agent works, what it is being asked to do, and how it reaches the plane.
/// </summary>
/// <param name="WorkDir">
/// The session <c>cwd</c>, which ACP requires to be absolute. The same
/// <c>{work_root}/{session_id}</c> the process is spawned in — a session is directory-scoped
/// in ACP exactly as a harness transcript is in §11, so the two agree.
/// </param>
/// <param name="Prompt">
/// The worker's opening turn, from <c>profiles[].prompt</c> with the usual <c>{...}</c>
/// substitutions applied. In stream mode this text lives in the spawn argv; ACP agents take
/// no prompt on argv, so it moves here.
/// </param>
/// <param name="FollowUp">
/// The turn sent to wake this session when there is new input on the assignment
/// (<c>profiles[].follow_up</c>). Configuration, not content: it tells the worker to read,
/// and the reading is what the plane treats as delivery.
/// </param>
/// <param name="McpServers">The plane's MCP server, translated from the generated config.</param>
/// <param name="ResumeSessionRef">§11: the session to <c>session/load</c>, when the plane has one.</param>
/// <param name="ConfigOptions">
/// <c>profiles[].config_options</c>: ACP <c>session/set_config_option</c> pins,
/// sent only when this session advertised the <c>configId</c> and the value.
/// </param>
/// <param name="SessionMode">
/// <c>profiles[].session_mode</c>: ACP <c>session/set_mode</c> after the session
/// opens, only when this session advertised that <c>modeId</c>.
/// </param>
public sealed record AcpSessionRequest(
    string WorkDir,
    string Prompt,
    string FollowUp,
    IReadOnlyList<AcpMcpServer> McpServers,
    string? ResumeSessionRef = null,
    string? AuthMethod = null,
    IReadOnlyDictionary<string, string>? ConfigOptions = null,
    string? SessionMode = null);

/// <summary>One MCP server as ACP's <c>session/new</c> wants it: HTTP, named, with headers.</summary>
/// <summary>What the agent is asking the plane to decide.</summary>
public readonly record struct AcpPermissionAsk(string Tool, string InputJson);

/// <summary>The plane's verdict, before it is mapped onto the agent's options.</summary>
public readonly record struct AcpPermissionDecision(bool Allow, string? Message);

public sealed record AcpMcpServer(
    string Name,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// Translates the plane's generated MCP config (§13) into ACP's <c>mcpServers</c> shape.
///
/// <para>The generated document is Claude Code's <c>--mcp-config</c> form — an object of
/// named servers, each with a <c>url</c> and a <c>headers</c> object — and ACP wants an
/// array of servers whose headers are an array of <c>{name, value}</c> pairs. Same
/// information, different spelling, so this is a transliteration and nothing more: no
/// server is invented, renamed, or given a header the plane did not write.</para>
///
/// <para>Only HTTP servers cross over. ACP also defines a stdio transport, but the plane
/// generates exactly one server and it is always the HTTP MCP endpoint, so an entry with no
/// <c>url</c> is something this translator has never seen and declines to guess at.</para>
/// </summary>
public static class AcpMcpServers
{
    /// <summary>
    /// Parses a generated config into ACP servers. A malformed or absent document yields an
    /// empty list rather than throwing: the worker is already spawned by the time this runs,
    /// and <see cref="AcpClient"/> warns about a toolless session far more usefully than a
    /// spawn-time crash would.
    /// </summary>
    public static IReadOnlyList<AcpMcpServer> FromGeneratedConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object)
                return [];

            var built = new List<AcpMcpServer>();
            foreach (var entry in servers.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (!entry.Value.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                    continue;
                if (url.GetString() is not { Length: > 0 } address)
                    continue;

                var headers = new List<KeyValuePair<string, string>>();
                if (entry.Value.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                    foreach (var header in h.EnumerateObject())
                        if (header.Value.ValueKind == JsonValueKind.String)
                            headers.Add(new KeyValuePair<string, string>(header.Name, header.Value.GetString()!));

                built.Add(new AcpMcpServer(entry.Name, address, headers));
            }

            return built;
        }
    }
}

/// <summary>A JSON-RPC <c>result</c>, cloned off the line's document so it outlives the read.</summary>
public readonly record struct AcpResult(JsonElement Value);

/// <summary>
/// A failure of the conversation itself — a JSON-RPC error, a missing sessionId, or a
/// stream that ended mid-request. Distinct from an I/O failure, which means the worker
/// died and is the supervisor's story to tell.
/// </summary>
public sealed class AcpProtocolException(string message, int code = 0) : Exception(message)
{
    /// <summary>
    /// The JSON-RPC error code the agent sent, or 0 when the failure was not a JSON-RPC
    /// error. Carried rather than folded into the message because one code is load-bearing:
    /// ACP's <see cref="AcpClient.AuthRequiredCode"/> is how an agent asks to be
    /// authenticated, and it has to be matched on rather than string-sniffed.
    /// </summary>
    public int Code { get; } = code;
}
