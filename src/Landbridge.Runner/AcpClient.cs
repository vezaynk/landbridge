using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AcpKit;
using AcpKit.Protocol.V1;
using Landbridge.Contracts;
using Landbridge.Core;
using SessionId = Landbridge.Core.SessionId;
using AgentSessionId = AcpKit.Protocol.V1.SessionId;
using AcpPermissionOption = AcpKit.Protocol.V1.PermissionOption;

namespace Landbridge.Runner;

/// <summary>
/// §10: the Agent Client Protocol client that <b>drives</b> a worker. JSON-RPC 2.0,
/// newline-delimited, over the worker's stdin/stdout — landbridged sends <c>initialize</c>,
/// opens or loads a session, and sends the profile's <c>prompt</c> as the opening turn.
///
/// <para>Framing and the typed v1 surface come from AcpKit. This type is the Landbridge
/// session policy on top: lazy authenticate, advertised config/mode pins, the permission
/// bridge, usage accumulation, follow-up turns, and the event ring.</para>
///
/// <para><b>The only way landbridged talks to a worker.</b> It previously also read whatever
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
/// stdin", which is exactly landbridged's convention: the held write end means landbridged is
/// alive, and its EOF means landbridged is gone. The two mechanisms coincide, so the switch
/// costs nothing here — and the per-profile opt-out it used to need is unreachable, because
/// a harness that blocked reading a held-open pipe was blocking on a <em>prompt</em>, and
/// stdin no longer carries one.</para>
///
/// <para><b>Stop is a real cancel.</b> The transport forbids writing anything to the agent's
/// stdin that is not an ACP message, so there is no free-text wind-down turn to write —
/// and nothing lost by that, since <c>session/cancel</c> is a notification the agent is
/// specified to honour, ending its turn with a <c>cancelled</c> stop reason.
/// <see cref="ProcessSupervisor.StopAsync"/> sends it before the deadline kill backstops
/// it. The prompt call's token is deliberately not cancelled: that would send
/// <c>$/cancel_request</c>, which is not the ACP stop.</para>
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
/// <para><b>AOT (§10).</b> AcpKit's converters are generated for concrete types. There is
/// no <c>JsonSerializer</c> call that resolves a contract by <c>Type</c> at runtime, so
/// the runner stays clean under <c>IsAotCompatible</c>.</para>
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
    public const int AuthRequiredCode = AcpErrorCode.AuthRequired;

    /// <summary>
    /// What landbridged calls itself in <c>clientInfo</c>. Read from the assembly rather than
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
    private readonly InboundHandler _inbound;

    /// <summary>
    /// Tool calls already reported, keyed by <c>toolCallId</c>. ACP sends one
    /// <c>tool_call</c> and then any number of <c>tool_call_update</c>s as the call moves
    /// through its status values, so without this one call would move the progress clock
    /// several times — the same mistake as mapping Codex's <c>item.updated</c> alongside
    /// <c>item.started</c>.
    /// </summary>
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>
    /// Last <c>rawInput</c> (and title) seen on a <c>tool_call</c> /
    /// <c>tool_call_update</c>, keyed by <c>toolCallId</c>. Codex often asks
    /// <c>session/request_permission</c> with only <c>kind: execute</c> and no
    /// <c>rawInput</c> — the command already rode the preceding update. Without
    /// this the Lead sees <c>proposed_input: {}</c> and cannot decide.
    /// </summary>
    private readonly Dictionary<string, (string? Title, string InputJson)> _toolInputs
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Permission handlers waiting for a later <c>tool_call_update</c> to fill
    /// <see cref="_toolInputs"/>. AcpKit holds the JSON-RPC response until the
    /// handler returns, so the waiter is a completion source rather than a
    /// deferred write.
    /// </summary>
    private readonly Dictionary<string, TaskCompletionSource> _rawInputWaiters
        = new(StringComparer.Ordinal);

    private readonly object _stateGate = new();

    /// <summary>How long an empty permission waits for a later <c>rawInput</c>.</summary>
    internal static readonly TimeSpan RawInputGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Agent-request methods already refused and reported, so the operator-facing line is
    /// once per method per task rather than once per call.
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
    /// Guards the session usage totals and the last-reported snapshot.
    /// <c>session/update</c> arrives on AcpKit's notification pump; <c>session/prompt</c>
    /// returns on the drive loop. Those two can interleave.
    /// </summary>
    private readonly object _usageGate = new();

    /// <summary>
    /// Running totals for the whole session, in the four disjoint buckets §12 measures.
    /// Cumulative rather than per-turn on purpose: <c>SessionStore.RecordUsageAsync</c> keeps a
    /// high-water mark per bucket, so a client that reported each turn separately would
    /// leave the row holding the <em>largest</em> turn instead of the session's spend.
    /// Feeding it a monotonically rising total makes the max a no-op and the last report the
    /// truth.
    /// </summary>
    private long _inputTokens, _outputTokens, _cacheReadTokens, _cacheWriteTokens;

    /// <summary>
    /// The most recent dollar figure the agent put on this session, or null if it has never
    /// named one. An explicit zero is a reported zero, not "never".
    /// </summary>
    private decimal? _costUsd;

    private long _lastReportedInput, _lastReportedOutput, _lastReportedCacheRead, _lastReportedCacheWrite;
    private decimal? _lastReportedCost;
    private bool _usageReported;

    /// <summary>Method ids the agent declared at <c>initialize</c>, in its own order.</summary>
    private readonly List<string> _authMethods = [];

    private AgentConnection? _connection;
    private SessionConfigOption[]? _configOptions;
    private SessionModeState? _modes;
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
        _inbound = new InboundHandler(this);
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
    /// <para>The pump is also the drain that keeps the worker's stdout pipe from
    /// filling, so it must run for the process's whole lifetime and must never throw into
    /// its host — a torn-down pipe or a cancelled token ends it quietly.</para>
    /// </summary>
    public async Task RunAsync(Stream stdout, Stream stdin, CancellationToken ct)
    {
        await using var connection = AgentConnection.Create(
            stdout,
            stdin,
            _inbound,
            onUnknownNotification: OnUnknownNotificationAsync,
            onFrame: OnFrame);
        _connection = connection;

        var pump = connection.RunAsync(ct);
        _ = pump.ContinueWith(
            static (_, state) => ((AcpClient)state!).OnPumpEnded(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await DriveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Teardown, or the worker died mid-conversation. Its exit code is the louder
            // signal and the supervisor already reports it; nothing to add here.
        }
        catch (Exception e) when (e is AcpException or AcpProtocolException)
        {
            // Only a failed initialize / session/new is a handshake. A later
            // stdout-end (dispose killing connect while session/prompt is still
            // open) used to print this same line and claim the worker did no
            // work — after report_result had already landed.
            if (_sessionId is null)
            {
                _warn(
                    $"landbridged: task {_task.Value}: the ACP handshake failed — {e.Message}. The worker was " +
                    "spawned but never reached a prompt turn, so it did no work; check that this profile's " +
                    "spawn argv really starts an ACP agent (§10).");
            }
        }

        // Whatever happened above, the stream still has to be drained to EOF: the worker
        // may keep writing, and an undrained pipe wedges it.
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or AcpException)
        {
            // The pump ending is how a dead worker is observed, not a failure of this method.
        }

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
            if (_connection is not { } connection)
                return false;
            await connection.SessionCancelAsync(
                new CancelNotification { SessionId = new AgentSessionId(session) },
                ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or AcpException)
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
        var connection = _connection ?? throw new InvalidOperationException("the ACP client is not connected");

        var init = await connection.InitializeAsync(
            new InitializeRequest
            {
                ProtocolVersion = new ProtocolVersion((ushort)LatestProtocolVersion),
                ClientCapabilities = new ClientCapabilities
                {
                    Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
                    Terminal = false,
                },
                ClientInfo = new Implementation
                {
                    Name = "landbridged",
                    Title = "Landbridge runner",
                    Version = ClientVersion,
                },
            },
            ct).ConfigureAwait(false);
        ReadAgentCapabilities(init);

        var session = await OpenSessionAsync(connection, ct).ConfigureAwait(false);

        if (session is not { Length: > 0 })
            throw new AcpProtocolException("the agent returned no sessionId");

        _sessionId = session;
        _onSessionId?.Invoke(session);
        await MaybeApplyConfigOptionsAsync(connection, session, ct).ConfigureAwait(false);
        await MaybeSetSessionModeAsync(connection, session, ct).ConfigureAwait(false);

        // A cancel that arrived between the spawn and here has nothing to cancel yet; now
        // that a session exists, honour it instead of prompting into a stop already asked for.
        if (_cancelSent)
            return;

        // The session outlives the turn (ideas/sessions.md stage 1). A cold start gets
        // the profile's opening prompt. A session/load is a wake, not a re-brief — the
        // follow-up says "read your assignment" (and, on the park bar, "report the
        // remembered value"). Sending the opening prompt again after load told the
        // worker to re-ask the question it had already asked.
        var resumed = _request.ResumeSessionRef is { Length: > 0 } && AgentSupportsLoadSession;
        var opening = resumed && _request.FollowUp is { Length: > 0 }
            ? _request.FollowUp
            : _request.Prompt;
        await PromptAsync(connection, opening, ct).ConfigureAwait(false);

        try
        {
            while (!_cancelSent)
            {
                var next = await _followUps.Reader.ReadAsync(ct).ConfigureAwait(false);
                await PromptAsync(connection, next, ct).ConfigureAwait(false);
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
    ///
    /// <para>Sent raw on purpose: <c>PromptResponse.usage</c> is how claude-agent-acp and
    /// opencode report token buckets, and the generated v1.20 model does not carry that
    /// field. A typed deserialize would drop the spend.</para>
    /// </summary>
    private async Task PromptAsync(AgentConnection connection, string text, CancellationToken ct)
    {
        var session = _sessionId!;
        var request = new PromptRequest
        {
            SessionId = new AgentSessionId(session),
            Prompt = [new ContentBlockText { Value = new TextContent { Text = text } }],
        };
        var payload = AcpPayload.Serialize(request, AcpJsonContext.Default.PromptRequest);
        using var result = await connection.Peer.SendRawRequestAsync(AcpMethods.SessionPrompt, payload, ct)
            .ConfigureAwait(false);

        StopReason = result.RootElement.TryGetProperty("stopReason", out var sr)
            && sr.ValueKind == JsonValueKind.String
                ? sr.GetString()
                : null;
        Turns++;

        // §10 telemetry ingest, before the turn-ended event rather than after: turn-ended is
        // what the plane may act on (a turn that ended with nothing reported requeues), and
        // accounting for a dispatch should already be in when its fate is decided.
        //
        // Cost (and Grok's snake_case buckets) also flush as those notifications arrive —
        // PromptResponse is not the only source. AcpKit can deliver the prompt result
        // before a `usage_update` that preceded it on the wire has been ingested; a later
        // notification still reports. The store's high-water / newest-cost merge is built
        // for more than one report.
        //
        // No model name. Nothing in ACP attributes usage to a model — not the usage buckets,
        // not `usage_update` — so the report is deliberately unattributed rather than guessing
        // from the profile's argv, which names a CLI and not the model it happened to route to.
        AddTurnUsage(result.RootElement);
        ReportUsageIfAny();

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
    /// <see cref="Landbridge.Contracts.PromptCommand"/> for the three properties this
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

    private async Task<string?> NewSessionAsync(AgentConnection connection, CancellationToken ct)
    {
        if (_request.ResumeSessionRef is { Length: > 0 } && !AgentSupportsLoadSession)
            _warn(
                $"landbridged: task {_task.Value}: the plane handed back a resume ref but this agent does not " +
                "declare the ACP 'loadSession' capability, so the transcript cannot be reloaded and this " +
                "dispatch is a COLD START. Every redispatch of this task will be one (§11).");

        var result = await connection.SessionNewAsync(
            new NewSessionRequest
            {
                Cwd = _request.WorkDir,
                McpServers = McpServers(),
            },
            ct).ConfigureAwait(false);
        _configOptions = result.ConfigOptions;
        _modes = result.Modes;
        return result.SessionId.Value;
    }

    /// <summary>
    /// §11 resume in-process. <c>session/load</c> takes the same <c>cwd</c> and
    /// <c>mcpServers</c> as <c>session/new</c> — the spec requires them to match — and the
    /// agent replays the whole conversation as <c>session/update</c> notifications before
    /// answering. Those replayed updates flow through the same handler as live ones,
    /// which is why tool-call reporting is keyed on <c>toolCallId</c>: a replayed call must
    /// not move the progress clock a second time.
    /// </summary>
    private async Task<string?> LoadSessionAsync(AgentConnection connection, string sessionRef, CancellationToken ct)
    {
        var result = await connection.SessionLoadAsync(
            new LoadSessionRequest
            {
                SessionId = new AgentSessionId(sessionRef),
                Cwd = _request.WorkDir,
                McpServers = McpServers(),
            },
            ct).ConfigureAwait(false);
        _configOptions = result.ConfigOptions;
        _modes = result.Modes;
        return sessionRef;
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
    private async Task MaybeApplyConfigOptionsAsync(AgentConnection connection, string sessionId, CancellationToken ct)
    {
        if (_request.ConfigOptions is not { Count: > 0 } wanted)
            return;

        foreach (var (configId, value) in wanted)
        {
            if (string.IsNullOrWhiteSpace(configId) || string.IsNullOrWhiteSpace(value))
                continue;
            if (!AdvertisesSelectValue(_configOptions, configId, value))
                continue;

            // Sent as the untyped {sessionId, configId, value} shape measured on OpenCode,
            // not the schema's later `type: boolean` arm — a string pin is not a boolean.
            using var doc = BuildConfigOptionParams(sessionId, configId, value);
            var updated = await connection.SessionSetConfigOptionAsync(
                new SetSessionConfigOptionRequestUnknown
                {
                    SessionId = new AgentSessionId(sessionId),
                    ConfigId = new SessionConfigId(configId),
                    Kind = "",
                    Raw = doc.RootElement.Clone(),
                },
                ct).ConfigureAwait(false);
            _configOptions = updated.ConfigOptions;
        }
    }

    private static JsonDocument BuildConfigOptionParams(string sessionId, string configId, string value)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("sessionId", sessionId);
            w.WriteString("configId", configId);
            w.WriteString("value", value);
            w.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    /// <summary>
    /// ACP <c>session/set_mode</c> when the profile named a mode this session
    /// advertised. Goose 1.46 defaults to <c>auto</c> (auto-approve); pinning
    /// <c>approve</c> is how a Landbridge profile keeps permissions on the protocol.
    /// Unadvertised is skipped, same as <see cref="MaybeApplyConfigOptionsAsync"/>.
    /// </summary>
    private async Task MaybeSetSessionModeAsync(AgentConnection connection, string sessionId, CancellationToken ct)
    {
        if (_request.SessionMode is not { Length: > 0 } mode)
            return;
        if (!AdvertisesMode(_modes, mode))
            return;

        await connection.SessionSetModeAsync(
            new SetSessionModeRequest
            {
                SessionId = new AgentSessionId(sessionId),
                ModeId = new SessionModeId(mode),
            },
            ct).ConfigureAwait(false);
    }

    private static bool AdvertisesMode(SessionModeState? modes, string modeId)
    {
        if (modes is null)
            return false;
        foreach (var mode in modes.AvailableModes)
        {
            if (mode.Id.Value == modeId)
                return true;
        }

        return false;
    }

    private static bool AdvertisesSelectValue(SessionConfigOption[]? options, string configId, string value)
    {
        if (options is null)
            return false;
        foreach (var opt in options)
        {
            if (opt is not SessionConfigOptionSelect select || select.Id.Value != configId)
                continue;
            if (!select.Value.Options.TryGetSessionConfigSelectOptionArray(out var choices))
                return false;
            foreach (var choice in choices)
            {
                if (choice.Value.Value == value)
                    return true;
            }

            return false;
        }

        return false;
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
    /// <para>Retries exactly once. A second refusal is the credential being wrong rather
    /// than missing, and re-authenticating in a loop would turn a bad key into a spin.</para>
    /// </summary>
    private async Task<string?> OpenSessionAsync(AgentConnection connection, CancellationToken ct)
    {
        try
        {
            return await RequestSessionAsync(ct).ConfigureAwait(false);
        }
        catch (AcpException e) when (e.IsAuthRequired)
        {
            await AuthenticateAsync(connection, e, ct).ConfigureAwait(false);
            return await RequestSessionAsync(ct).ConfigureAwait(false);
        }

        Task<string?> RequestSessionAsync(CancellationToken token) =>
            _request.ResumeSessionRef is { Length: > 0 } resume && AgentSupportsLoadSession
                ? LoadSessionAsync(connection, resume, token)
                : NewSessionAsync(connection, token);
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
    private async Task AuthenticateAsync(AgentConnection connection, AcpException refusal, CancellationToken ct)
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
            await connection.AuthenticateAsync(
                new AuthenticateRequest { MethodId = new AuthMethodId(method) },
                ct).ConfigureAwait(false);
        }
        catch (AcpException e)
        {
            _ring.Enqueue(new AuthFailedEvent(
                _task, "authenticate", method, e.Message, null));
            throw new AcpProtocolException(
                $"the agent refused ACP authenticate with method '{method}' ({e.Message}). "
                + $"Declared methods: {(_authMethods.Count == 0 ? "(none)" : string.Join(", ", _authMethods))}. "
                + "Set the profile's `auth_method` to pick a different one, or give the harness "
                + "its credential in the profile's `env` (§10).",
                e.Code);
        }

        _ = refusal;
    }

    private void ReadAgentCapabilities(InitializeResponse init)
    {
        var version = (int)init.ProtocolVersion.Value;
        NegotiatedProtocolVersion = version;

        // Deliberately NOT "anything but the latest is a warning". Every agent in the
        // wild answers 1, so that rule would put a line on every task of every ACP
        // profile — and a warning that fires always is a warning an operator learns to
        // scroll past, which costs more than it can ever report. Only a version outside
        // the range this client can actually hold a session over is worth saying.
        if (version < OldestProtocolVersion || version > LatestProtocolVersion)
            _warn(
                $"landbridged: task {_task.Value}: the agent negotiated ACP protocol version {version}, which " +
                $"is outside the {OldestProtocolVersion}–{LatestProtocolVersion} range this client speaks. " +
                "Continuing on the assumption that the session methods are unchanged, but if this task " +
                "reads oddly — no session ref, no tool calls — that assumption is the first thing to " +
                "doubt (§10).");

        _authMethods.Clear();
        if (init.AuthMethods is { Length: > 0 } methods)
        {
            foreach (var method in methods)
            {
                if (method is AuthMethodAuthMethodAgent agent && agent.Value.Id.Value is { Length: > 0 } id)
                    _authMethods.Add(id);
            }
        }

        var caps = init.AgentCapabilities;
        AgentSupportsLoadSession = caps?.LoadSession == true;
        AgentSupportsHttpMcp = caps?.McpCapabilities?.Http == true;

        if (!AgentSupportsHttpMcp && _request.McpServers.Count > 0)
            _warn(
                $"landbridged: task {_task.Value}: this agent does not declare the ACP 'mcpCapabilities.http' " +
                "capability, so the plane's MCP server cannot be handed to it over the wire. The worker will " +
                "start with NO landbridge tools unless the machine wires them in out of band — it cannot call " +
                "get_session or report_result, so it will do nothing useful (§10).");
    }

    private McpServer[] McpServers()
    {
        if (_request.McpServers.Count == 0)
            return [];

        var servers = new McpServer[_request.McpServers.Count];
        for (var i = 0; i < _request.McpServers.Count; i++)
        {
            var server = _request.McpServers[i];
            var headers = new HttpHeader[server.Headers.Count];
            for (var h = 0; h < server.Headers.Count; h++)
            {
                var header = server.Headers[h];
                headers[h] = new HttpHeader { Name = header.Key, Value = header.Value };
            }

            servers[i] = new McpServerHttp
            {
                Name = server.Name,
                Url = server.Url,
                Headers = headers,
            };
        }

        return servers;
    }

    private void OnFrame(ReadOnlyMemory<byte> frame)
    {
        if (_rawLineSink is not null)
        {
            try { _rawLineSink(Encoding.UTF8.GetString(frame.Span)); }
            catch { /* capture is never allowed to affect the worker */ }
        }

        if (frame.Length > 0 && frame.Span[0] == (byte)'{')
            Interlocked.Increment(ref _messages);

        // Typed session/update drops a tool_call with no title (required on the generated
        // model). Agents omit it; the kind is the fallback the event log needs. Reading
        // the frame directly is what keeps that progress signal.
        TryIngestToolCallFrame(frame);
    }

    private void TryIngestToolCallFrame(ReadOnlyMemory<byte> frame)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (!root.TryGetProperty("method", out var method) || method.GetString() != "session/update")
                return;
            if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object)
                return;
            if (!p.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
                return;
            var kindOf = update.TryGetProperty("sessionUpdate", out var su) ? su.GetString() : null;
            if (kindOf is not ("tool_call" or "tool_call_update"))
                return;

            var id = update.TryGetProperty("toolCallId", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            if (id is not { Length: > 0 })
                return;

            var title = update.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                ? titleEl.GetString()
                : null;
            var kind = update.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
                ? kindEl.GetString()
                : null;
            JsonElement? raw = update.TryGetProperty("rawInput", out var rawEl) ? rawEl : null;
            IngestToolCall(id, title, kind, raw);
        }
    }

    private ValueTask OnUnknownNotificationAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (method == "_x.ai/session_notification")
            RecordXaiUsage(parameters);
        return ValueTask.CompletedTask;
    }

    private void OnPumpEnded()
    {
        _followUps.Writer.TryComplete();
        List<TaskCompletionSource> waiters;
        lock (_stateGate)
        {
            waiters = _rawInputWaiters.Values.ToList();
            _rawInputWaiters.Clear();
        }

        foreach (var waiter in waiters)
            waiter.TrySetCanceled();
    }

    private Task HandleSessionUpdateAsync(SessionNotification notification, CancellationToken ct)
    {
        switch (notification.Update)
        {
            case SessionUpdateUsageUpdate usage:
                RecordUsageUpdate(usage.Value);
                break;
            case SessionUpdateToolCall call:
                IngestToolCall(
                    call.Value.ToolCallId.Value,
                    call.Value.Title,
                    call.Value.Kind?.Value,
                    call.Value.RawInput);
                break;
            case SessionUpdateToolCallUpdate update:
                IngestToolCall(
                    update.Value.ToolCallId.Value,
                    update.Value.Title,
                    update.Value.Kind?.Value,
                    update.Value.RawInput);
                break;
        }

        _ = ct;
        return Task.CompletedTask;
    }

    private void IngestToolCall(string id, string? title, string? kind, JsonElement? rawInput)
    {
        if (id.Length == 0)
            return;

        var name = title is { Length: > 0 } ? title : kind ?? "tool";
        RememberToolInput(id, name, rawInput);

        TaskCompletionSource? waiter = null;
        var first = false;
        lock (_stateGate)
        {
            if (_rawInputWaiters.TryGetValue(id, out var pending) && !ShouldWaitForRawInput(id, name))
            {
                _rawInputWaiters.Remove(id);
                waiter = pending;
            }

            first = _reported.Add(id);
        }

        if (waiter is not null)
            waiter.TrySetResult();

        if (!first)
            return;

        _ring.Enqueue(new ToolCallEvent(_task, name, _clock.GetUtcNow()));
    }

    private void RememberToolInput(string toolCallId, string? title, JsonElement? rawInput)
    {
        var input = rawInput is { } raw
            && raw.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
                ? raw.GetRawText()
                : null;
        if (input is null && title is null)
            return;

        lock (_stateGate)
        {
            if (_toolInputs.TryGetValue(toolCallId, out var prior))
            {
                _toolInputs[toolCallId] = (
                    title ?? prior.Title,
                    input is { Length: > 2 } ? input : prior.InputJson);
                return;
            }

            _toolInputs[toolCallId] = (title, input is { Length: > 2 } ? input : "{}");
        }
    }

    private async Task<RequestPermissionResponse> HandlePermissionAsync(
        RequestPermissionRequest request, CancellationToken ct)
    {
        var toolCallId = request.ToolCall.ToolCallId.Value;
        var title = ToolOf(request);
        if (ShouldWaitForRawInput(toolCallId, title))
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateGate)
                _rawInputWaiters[toolCallId] = waiter;

            using var grace = new CancellationTokenSource();
            // Wall clock, not the injected TimeProvider: the test clock is frozen, and a
            // real Codex fill arrives as a later tool_call_update, not as time passing.
            var delay = Task.Delay(RawInputGrace, TimeProvider.System, grace.Token);
            try
            {
                var completed = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
                if (completed == waiter.Task)
                    await waiter.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                await grace.CancelAsync().ConfigureAwait(false);
                lock (_stateGate)
                    _rawInputWaiters.Remove(toolCallId);
            }
        }

        var option = await ResolvePermissionOptionAsync(request, ct).ConfigureAwait(false);
        return new RequestPermissionResponse
        {
            Outcome = option is null
                ? new RequestPermissionOutcomeCancelled()
                : new RequestPermissionOutcomeSelected
                {
                    Value = new SelectedPermissionOutcome { OptionId = new PermissionOptionId(option) },
                },
        };
    }

    /// <summary>
    /// §11 permission bridge, ACP edition. The agent asks via
    /// <c>session/request_permission</c>; this client puts the same
    /// <see cref="InputRequestKind.Permission"/> request on the plane that the
    /// old harness prompt-tool did, waits for a Lead or human verdict, and maps
    /// it onto the agent's own options.
    ///
    /// <para>When the plane returns an <c>optionId</c> the Lead picked, that id
    /// is sent back as-is (including <c>allow_always</c>, if the Lead chose it).
    /// A classifier or legacy allow still maps to <c>allow_once</c> only — never
    /// <c>allow_always</c>. Deny without an optionId picks a reject option, or
    /// <c>cancelled</c>. A missing plane callback is also cancelled rather than
    /// auto-allowed.</para>
    /// </summary>
    private async Task<string?> ResolvePermissionOptionAsync(RequestPermissionRequest request, CancellationToken ct)
    {
        var options = request.Options;
        if (options.Length == 0)
            return null;

        if (_requestPermission is null)
            return null;

        var ask = new AcpPermissionAsk(ToolOf(request), InputOf(request), OptionsJsonOf(options));
        if (LooksLikeEmptyExecute(ask))
        {
            _warn(
                $"landbridged: task {_task.Value}: auto-allowed a permission request for '{ask.Tool}' "
                + "with no proposed command (empty rawInput). The harness asked to approve nothing — "
                + "a Codex-side miss. landbridged will not gate that.");
            return FirstOfKind(options, PermissionOptionKind.AllowOnce);
        }

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
                $"landbridged: task {_task.Value}: the plane permission bridge failed ({ex.GetType().Name}: {ex.Message}); " +
                "answering the agent with cancelled so it is not wedged inside the tool call.");
            return null;
        }

        if (decision.OptionId is { Length: > 0 } picked)
        {
            foreach (var o in options)
            {
                if (o.OptionId.Value == picked)
                {
                    if (!decision.Allow)
                        WarnPermissionDenied(ask.Tool, decision.Message);
                    return picked;
                }
            }

            _warn(
                $"landbridged: task {_task.Value}: the plane picked optionId '{picked}' which this " +
                "request did not offer; answering cancelled.");
            return null;
        }

        if (decision.Allow)
        {
            // Never allow_always: that is a standing bypass in the agent, not a
            // one-shot plane decision. If the agent offered no allow_once, cancel
            // rather than promote the allow into a lasting grant.
            return FirstOfKind(options, PermissionOptionKind.AllowOnce);
        }

        WarnPermissionDenied(ask.Tool, decision.Message);
        return FirstOfKind(options, PermissionOptionKind.RejectOnce)
            ?? FirstOfKind(options, PermissionOptionKind.RejectAlways);
    }

    private void WarnPermissionDenied(string tool, string? message)
    {
        if (!TryMarkDeclined("session/request_permission"))
            return;
        _warn(
            $"landbridged: task {_task.Value}: the plane DENIED this worker's permission request for " +
            $"'{tool}'{(message is { Length: > 0 } why ? $" ({why})" : "")}. A worker whose " +
            "landbridge tools are denied cannot call get_session or report_result, so it will end its turn " +
            "having done nothing — check that a Lead is answering permission requests, or set this " +
            "profile to a permission mode that does not prompt (§10, §11).");
    }

    private static string OptionsJsonOf(AcpPermissionOption[] options)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var option in options)
            {
                w.WriteStartObject();
                w.WriteString("optionId", option.OptionId.Value);
                w.WriteString("name", option.Name);
                w.WriteString("kind", option.Kind.Value);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private bool ShouldWaitForRawInput(string toolCallId, string tool)
    {
        if (toolCallId.Length == 0)
            return false;
        var input = InputOf(toolCallId, rawInput: null);
        if (!IsBlankInput(input))
            return false;
        return !LooksLikeCommandTitle(tool);
    }

    private string ToolOf(RequestPermissionRequest request)
    {
        var call = request.ToolCall;
        var fromCall = call.Title ?? call.Kind?.Value;
        if (fromCall is { Length: > 0 } && fromCall != "execute" && fromCall != "tool")
            return fromCall;

        lock (_stateGate)
        {
            if (_toolInputs.TryGetValue(call.ToolCallId.Value, out var remembered)
                && remembered.Title is { Length: > 0 })
                return remembered.Title;
        }

        return fromCall ?? "tool";
    }

    private string InputOf(RequestPermissionRequest request) =>
        InputOf(request.ToolCall.ToolCallId.Value, request.ToolCall.RawInput);

    private string InputOf(string toolCallId, JsonElement? rawInput)
    {
        if (rawInput is { } raw && HasContent(raw))
            return raw.GetRawText();

        lock (_stateGate)
        {
            if (_toolInputs.TryGetValue(toolCallId, out var remembered)
                && remembered.InputJson is { Length: > 2 })
                return remembered.InputJson;
        }

        return "{}";
    }

    private static bool HasContent(JsonElement raw) => raw.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => false,
        JsonValueKind.Object => raw.EnumerateObject().Any(),
        JsonValueKind.Array => raw.GetArrayLength() > 0,
        JsonValueKind.String => raw.GetString() is { Length: > 0 },
        _ => true,
    };

    private static bool LooksLikeEmptyExecute(AcpPermissionAsk ask) =>
        IsBlankInput(ask.InputJson) && GenericToolNames.Contains(ask.Tool.Trim());

    private static bool IsBlankInput(string input)
    {
        var t = input.Trim();
        return t.Length == 0 || t == "{}";
    }

    private static readonly HashSet<string> GenericToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "execute", "tool", "bash", "shell", "sh", "zsh", "cmd", "powershell",
        "terminal", "run_shell_command", "local_shell", "shell_command",
        "bash_tool", "run_command", "execute_command",
    };

    private static readonly HashSet<string> BareCommandTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "pwd", "git", "cat", "head", "tail", "wc", "echo", "date",
        "whoami", "uname", "which", "true", "false", "env", "id", "hostname",
    };

    private static bool LooksLikeCommandTitle(string tool)
    {
        var s = tool.Trim();
        if (s.Length == 0 || GenericToolNames.Contains(s))
            return false;
        if (s.Contains(' ') || s.Contains('\t'))
            return true;
        if (s.StartsWith("Execute `", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("Run `", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("Shell `", StringComparison.OrdinalIgnoreCase))
            return true;
        return BareCommandTitles.Contains(s);
    }

    private static string? FirstOfKind(AcpPermissionOption[] options, PermissionOptionKind kind)
    {
        foreach (var option in options)
        {
            if (option.Kind == kind)
                return option.OptionId.Value;
        }

        return null;
    }

    /// <summary>
    /// Takes the dollar figure off a <c>usage_update</c> and reports it now. The rest of
    /// that notification — <c>used</c> and <c>size</c> — is a context-window gauge, not
    /// spend, and §12's measured view has nowhere honest to put it; a gauge written into
    /// a cumulative column would read as consumption that never happened.
    ///
    /// Relays the agent's own figure. A zero is stored as zero — Landbridge does
    /// not second-guess a harness that prices a turn at $0.
    /// </summary>
    private void RecordUsageUpdate(UsageUpdate update)
    {
        if (update.Cost is not { } cost)
            return;
        lock (_usageGate)
            _costUsd = Convert.ToDecimal(cost.Amount);
        ReportUsageIfAny();
    }

    /// <summary>
    /// Grok's <c>_x.ai/session_notification</c> usage: snake_case buckets, no cost.
    /// Added into the same session totals <see cref="AddTurnUsage"/> feeds, so the
    /// high-water-mark store still sees one rising report.
    /// </summary>
    private void RecordXaiUsage(JsonElement p)
    {
        if (!p.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            return;
        if (!update.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;

        var any = false;
        lock (_usageGate)
        {
            any |= AddLocked(usage, "input_tokens", ref _inputTokens);
            any |= AddLocked(usage, "output_tokens", ref _outputTokens);
            any |= AddLocked(usage, "cache_read_input_tokens", ref _cacheReadTokens);
            any |= AddLocked(usage, "cache_creation_input_tokens", ref _cacheWriteTokens);
        }

        if (any)
            ReportUsageIfAny();
    }

    private void AddTurnUsage(JsonElement result)
    {
        if (!result.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;

        lock (_usageGate)
        {
            AddLocked(usage, "inputTokens", ref _inputTokens);
            AddLocked(usage, "outputTokens", ref _outputTokens);
            AddLocked(usage, "cachedReadTokens", ref _cacheReadTokens);
            AddLocked(usage, "cachedWriteTokens", ref _cacheWriteTokens);
        }
    }

    private static bool AddLocked(JsonElement o, string key, ref long total)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt64(out var n) || n < 0)
            return false;
        total += n;
        return true;
    }

    /// <summary>
    /// One cumulative report of whatever we have so far. Skips a true empty (no buckets,
    /// no cost) so an agent that never named spend does not look like a free dispatch,
    /// and skips a duplicate of the last report so a prompt-return flush after a live
    /// notification is a no-op when nothing changed.
    /// </summary>
    private void ReportUsageIfAny()
    {
        long input, output, cacheRead, cacheWrite;
        decimal? cost;
        lock (_usageGate)
        {
            input = _inputTokens;
            output = _outputTokens;
            cacheRead = _cacheReadTokens;
            cacheWrite = _cacheWriteTokens;
            cost = _costUsd;
            if (cost is null && input + output + cacheRead + cacheWrite == 0)
                return;
            if (_usageReported
                && input == _lastReportedInput
                && output == _lastReportedOutput
                && cacheRead == _lastReportedCacheRead
                && cacheWrite == _lastReportedCacheWrite
                && cost == _lastReportedCost)
                return;
            _lastReportedInput = input;
            _lastReportedOutput = output;
            _lastReportedCacheRead = cacheRead;
            _lastReportedCacheWrite = cacheWrite;
            _lastReportedCost = cost;
            _usageReported = true;
        }

        _ring.Enqueue(new UsageReportedEvent(
            _task,
            Model: null,
            input,
            output,
            cacheRead,
            cacheWrite,
            ReasoningOutputTokens: null,
            cost,
            _clock.GetUtcNow()));
    }

    /// <summary>
    /// The ACP counterpart of the terminal reader's silent-stream warning (§11), and it
    /// diagnoses the same failure: a worker whose whole run produced no session and no tool
    /// call. Here it means the spawn argv is not an ACP agent — the far more likely mistake
    /// than a mismapped stream, since ACP has nothing to map.
    /// </summary>
    private void ReportSilentStream()
    {
        if (_sessionId is not null || Volatile.Read(ref _messages) > 0)
            return;

        _warn(
            $"landbridged: task {_task.Value}: protocol is 'acp' and the worker produced no JSON-RPC message at " +
            "all — not even a reply to initialize. Its spawn argv is almost certainly not an ACP agent (an " +
            "ACP-mode profile spawns something like `opencode acp`, not the harness's ordinary run command). " +
            "The task did no work (§10).");
    }

    private bool TryMarkDeclined(string method)
    {
        lock (_stateGate)
            return _declined.Add(method);
    }

    private void WarnDeclined(string method)
    {
        if (!TryMarkDeclined(method))
            return;
        _warn(
            $"landbridged: task {_task.Value}: the agent asked landbridged to perform '{method}' and was refused — " +
            "this client declares the ACP fs and terminal capabilities UNSUPPORTED, because a Landbridge " +
            "worker is expected to use its own work dir and its own shell. An agent that delegates all " +
            "of its file or command access to the client cannot work under that declaration, and this " +
            "line is the only sign of it: check whether this harness needs a client-side terminal (§10).");
    }

    private Task<T> Refuse<T>(string method)
    {
        WarnDeclined(method);
        return Task.FromException<T>(
            new AcpException(AcpErrorCode.MethodNotFound, $"landbridged does not implement '{method}'"));
    }

    /// <summary>The methods the agent may call on landbridged. Permission is answered; fs and terminal are not.</summary>
    private sealed class InboundHandler(AcpClient owner) : IAcpClient
    {
        public Task<RequestPermissionResponse> SessionRequestPermissionAsync(
            RequestPermissionRequest request, CancellationToken cancellationToken) =>
            owner.HandlePermissionAsync(request, cancellationToken);

        public Task SessionUpdateAsync(SessionNotification request, CancellationToken cancellationToken) =>
            owner.HandleSessionUpdateAsync(request, cancellationToken);

        public Task<WriteTextFileResponse> FsWriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken) =>
            owner.Refuse<WriteTextFileResponse>("fs/write_text_file");

        public Task<ReadTextFileResponse> FsReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken) =>
            owner.Refuse<ReadTextFileResponse>("fs/read_text_file");

        public Task<CreateTerminalResponse> TerminalCreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken) =>
            owner.Refuse<CreateTerminalResponse>("terminal/create");

        public Task<TerminalOutputResponse> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken) =>
            owner.Refuse<TerminalOutputResponse>("terminal/output");

        public Task<ReleaseTerminalResponse> TerminalReleaseAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken) =>
            owner.Refuse<ReleaseTerminalResponse>("terminal/release");

        public Task<WaitForTerminalExitResponse> TerminalWaitForExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken) =>
            owner.Refuse<WaitForTerminalExitResponse>("terminal/wait_for_exit");

        public Task<KillTerminalResponse> TerminalKillAsync(KillTerminalRequest request, CancellationToken cancellationToken) =>
            owner.Refuse<KillTerminalResponse>("terminal/kill");
    }
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

/// <summary>What the agent is asking the plane to decide.</summary>
public readonly record struct AcpPermissionAsk(string Tool, string InputJson, string OptionsJson = "[]");

/// <summary>The plane's verdict, before it is mapped onto the agent's options.</summary>
public readonly record struct AcpPermissionDecision(bool Allow, string? Message, string? OptionId = null);

/// <summary>One MCP server as ACP's <c>session/new</c> wants it: HTTP, named, with headers.</summary>
public sealed record AcpMcpServer(
    string Name,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// The plane's MCP server as ACP <c>session/new</c> wants it: HTTP, named, bearer header.
/// Built from the dispatch token and public URL — not from a generated config file.
/// </summary>
public static class AcpMcpServers
{
    public const string ServerName = "landbridge";

    /// <summary>
    /// One HTTP server for the plane, or empty when the dispatch carried no URL or token.
    /// </summary>
    public static IReadOnlyList<AcpMcpServer> ForPlane(string? url, string? workerToken)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(workerToken))
            return [];

        return
        [
            new AcpMcpServer(
                ServerName,
                url,
                [new KeyValuePair<string, string>("Authorization", "Bearer " + workerToken)]),
        ];
    }

    /// <summary>
    /// The <c>{mcp_config}</c> file body a profile may still ask spawn to point at.
    /// Same facts as <see cref="ForPlane"/>, Claude Code's <c>--mcp-config</c> spelling,
    /// because that is what those argv still expect. Null when there is nothing to write.
    /// </summary>
    public static string? ConfigFileJson(string? url, string? workerToken)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(workerToken))
            return null;

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("mcpServers");
            w.WriteStartObject();
            w.WritePropertyName(ServerName);
            w.WriteStartObject();
            w.WriteString("type", "http");
            w.WriteString("url", url);
            w.WritePropertyName("headers");
            w.WriteStartObject();
            w.WriteString("Authorization", "Bearer " + workerToken);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

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
