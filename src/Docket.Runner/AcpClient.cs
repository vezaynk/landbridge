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
    /// The protocol MAJOR version this client speaks. The spec requires the client to send
    /// the latest it supports and the agent to answer with the same value or its own
    /// latest; <see cref="NegotiatedProtocolVersion"/> records what came back, and a
    /// downgrade is reported rather than treated as fatal — an older agent that still
    /// answers <c>session/new</c> is more useful than a refused dispatch.
    /// </summary>
    public const int LatestProtocolVersion = 2;

    /// <summary>
    /// The oldest version this client can hold a session over. Every shipping agent
    /// measured on 2026-08-15 — <c>claude-agent-acp</c> 0.68.0, <c>codex-acp</c> 1.3.0,
    /// <c>opencode</c> 1.18.18 — negotiates <b>1</b>, so 1 is the version this actually
    /// speaks in practice and must not be treated as a degradation. The spec still requires
    /// the client to <em>offer</em> its latest, which is why
    /// <see cref="LatestProtocolVersion"/> is what goes on the wire; the methods this client
    /// uses (<c>session/new</c>, <c>session/load</c>, <c>session/prompt</c>,
    /// <c>session/update</c>, <c>session/cancel</c>, <c>session/request_permission</c>) are
    /// common to both, so anything in this range is silently fine.
    /// </summary>
    public const int OldestProtocolVersion = 1;

    /// <summary>
    /// What docketd calls itself in <c>clientInfo</c>. Read from the assembly rather than
    /// written as a constant so it cannot drift; agents log it, and a wrong version in
    /// someone else's log is a wasted afternoon.
    /// </summary>
    private static readonly string ClientVersion =
        typeof(AcpClient).Assembly.GetName().Version?.ToString() ?? "0.0";

    private readonly TaskId _task;
    private readonly OutboundEventRing _ring;
    private readonly TimeProvider _clock;
    private readonly AcpSessionRequest _request;
    private readonly Action<string>? _onSessionId;
    private readonly Action<string>? _rawLineSink;
    private readonly Action<string> _warn;

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

    private TextWriter? _stdin;
    private long _nextId;
    private string? _sessionId;
    private bool _cancelSent;

    /// <summary>Well-formed JSON-RPC lines seen, for the silent-stream report at the end.</summary>
    private int _messages;

    public AcpClient(
        TaskId task,
        OutboundEventRing ring,
        TimeProvider clock,
        AcpSessionRequest request,
        Action<string>? onSessionId = null,
        Action<string>? rawLineSink = null,
        Action<string>? warn = null)
    {
        _task = task;
        _ring = ring;
        _clock = clock;
        _request = request;
        _onSessionId = onSessionId;
        _rawLineSink = rawLineSink;
        _warn = warn ?? Console.Error.WriteLine;
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
            _warn(
                $"docketd: task {_task.Value}: the ACP handshake failed — {e.Message}. The worker was " +
                "spawned but never reached a prompt turn, so it did no work; check that this profile's " +
                "spawn argv really starts an ACP agent (§10).");
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

        var session = _request.ResumeSessionRef is { Length: > 0 } resume && AgentSupportsLoadSession
            ? await LoadSessionAsync(resume, ct).ConfigureAwait(false)
            : await NewSessionAsync(ct).ConfigureAwait(false);

        if (session is not { Length: > 0 })
            throw new AcpProtocolException("the agent returned no sessionId");

        _sessionId = session;
        _onSessionId?.Invoke(session);

        // A cancel that arrived between the spawn and here has nothing to cancel yet; now
        // that a session exists, honour it instead of prompting into a stop already asked for.
        if (_cancelSent)
            return;

        // The session outlives the turn (ideas/sessions.md stage 1). The opening prompt is
        // the profile's; every one after it arrives through PromptAsync — a `prompt` command
        // from the plane carrying, for instance, the answer to a question this worker asked.
        //
        // This loop is the whole shape change. Under the task model there was nothing to
        // loop over: a worker that finished a turn had also exited, so "the turn ended" and
        // "the conversation ended" were one observation. Here they are two, and only the
        // second ends this method.
        await PromptAsync(_request.Prompt, ct).ConfigureAwait(false);

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

        // session/load's result carries no sessionId of its own — the ref we asked for IS
        // the session — so an agent that echoes one is honoured and one that does not is
        // not an error.
        return SessionIdOf(result) ?? sessionRef;
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
                "get_task or report_result, so it will do nothing useful (§10).");
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
                HandleNotification(method, root);
            else if (hasId)
                CompletePending(id, root);
        }
    }

    /// <summary>
    /// The only notification that carries anything docketd's event vocabulary has a place
    /// for. Everything else ACP streams — message chunks, thoughts, plans, mode changes —
    /// is conversation content, which §12 captures verbatim through the transcript tee and
    /// which the frozen runner vocabulary deliberately has no member for.
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
            var option = ChoosePermissionOption(root);
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
    /// §11 permission bridge, ACP edition — <b>and deliberately only half of it in this
    /// increment.</b> This picks the agent's own "allow" option, which reproduces exactly
    /// what today's profiles buy with <c>--permission-mode bypassPermissions</c>,
    /// <c>--dangerously-bypass-approvals-and-sandbox</c> and <c>--auto</c>: a headless
    /// worker that runs to completion without a human.
    ///
    /// <para>What it is <em>not</em> yet is the bridge. Docket already has a
    /// <see cref="InputRequestKind.Permission"/> flow that relays a harness's permission
    /// prompt to a Lead and answers it with a <see cref="PermissionVerdict"/>, and ACP is a
    /// far better fit for it than the per-harness prompt-tool it was built on — the request
    /// arrives structured, with the agent's own options attached. Routing this into the
    /// plane instead of auto-allowing is the next increment; auto-allow is what keeps this
    /// one honest about being a like-for-like port rather than a silent policy change.</para>
    ///
    /// <para>Preference order: an always-allow option beats a once-only one, because a
    /// worker asked the same question forty times is forty round trips through the plane
    /// for a decision already made. Ordering is by the option's declared <c>kind</c>, never
    /// by its human-readable name.</para>
    /// </summary>
    private static string? ChoosePermissionOption(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        if (!p.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            return null;

        string? allowOnce = null;
        string? first = null;

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
                continue;
            if (StringOf(option, "optionId") is not { Length: > 0 } id)
                continue;

            first ??= id;
            switch (StringOf(option, "kind"))
            {
                case "allow_always":
                    return id;
                case "allow_once":
                    allowOnce ??= id;
                    break;
            }
        }

        // Nothing the agent labelled as an allow: fall back to its first option rather than
        // cancelling, since an agent that offers only bespoke options still needs an answer.
        return allowOnce ?? first;
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
            pending.TrySetException(new AcpProtocolException($"{message} (JSON-RPC {code})"));
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
/// <c>{work_root}/{task_id}</c> the process is spawned in — a session is directory-scoped
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
public sealed record AcpSessionRequest(
    string WorkDir,
    string Prompt,
    string FollowUp,
    IReadOnlyList<AcpMcpServer> McpServers,
    string? ResumeSessionRef = null);

/// <summary>One MCP server as ACP's <c>session/new</c> wants it: HTTP, named, with headers.</summary>
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
public sealed class AcpProtocolException(string message) : Exception(message);
