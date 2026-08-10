using System.Text.Json;
using Docket.Contracts;
using Docket.Core;

namespace Docket.Runner;

/// <summary>
/// §10 event relay, <see cref="EventsSource.Terminal"/>: maps a harness's stdout
/// NDJSON stream to the frozen runner event vocabulary
/// (<see cref="Docket.Contracts.RunnerEvent"/>). One reader supervises one task's
/// stream; the supervisor starts it draining stdout for the worker's whole
/// lifetime (see <see cref="ProcessSupervisor.Spawn"/>) — the reader <b>is</b> the
/// drain that keeps the OS pipe from filling and deadlocking the worker.
///
/// <para><b>Harness-agnostic (§10).</b> docketd carries no harness knowledge in
/// code: the claude <c>--output-format stream-json</c> shape is the built-in
/// <em>default</em>, and every discriminator/key it keys off is overridable
/// through <see cref="EventsConfig.Mapping"/>, so another harness whose stream
/// uses different names is a config change, not a code change. Recognized mapping
/// keys (claude defaults in parens):
/// <list type="bullet">
///   <item><c>type_key</c> (<c>type</c>) — the top-level discriminator property.</item>
///   <item><c>system_type</c> (<c>system</c>) — <c>type</c> value for meta lines.</item>
///   <item><c>assistant_type</c> (<c>assistant</c>) — <c>type</c> value for an assistant turn.</item>
///   <item><c>subtype_key</c> (<c>subtype</c>) — sub-discriminator on system lines.</item>
///   <item><c>init_subtype</c> (<c>init</c>) — <c>subtype</c> value for the session-init line.</item>
///   <item><c>session_id_key</c> (<c>session_id</c>) — top-level property carrying the session id.</item>
///   <item><c>message_key</c> (<c>message</c>) — wrapper object holding the content array.</item>
///   <item><c>content_key</c> (<c>content</c>) — the array of content blocks.</item>
///   <item><c>block_type_key</c> (<c>type</c>) — discriminator on a content block.</item>
///   <item><c>tool_use_block_type</c> (<c>tool_use</c>) — block <c>type</c> value for a tool call.</item>
///   <item><c>tool_name_key</c> (<c>name</c>) — property on a tool_use block naming the tool.</item>
/// </list></para>
///
/// <para><b>Flat tool-call mode (§10, issue #111).</b> The eleven keys above all assume
/// claude's nesting: an assistant turn wrapping an <em>array</em> of content blocks, one of
/// which is the tool call. A harness that emits one event object <em>per</em> tool call —
/// Codex's <c>{"type":"item.started","item":{…}}</c> — cannot be described by renaming
/// alone, because renames never change nesting or arity. Two more keys describe that shape
/// directly, and declaring both switches the mode on for the lines they match:
/// <list type="bullet">
///   <item><c>tool_event_type</c> (unset) — the <c>type_key</c> value of a line that <em>is</em>
///     one tool call.</item>
///   <item><c>tool_name_path</c> (unset) — a dotted path from the line root to the string
///     naming the tool (<c>item.tool</c>). Comma-separated alternatives are tried in order
///     and the first that resolves to a non-empty string wins, because one harness commonly
///     spells the name differently per tool kind (Codex: <c>item.command</c> for a shell
///     call, <c>item.tool</c> for an MCP call). At most one <see cref="ToolCallEvent"/> per
///     line either way — that is what "flat" means.</item>
/// </list>
/// A line whose <c>type</c> matches but where no path resolves emits nothing, and that
/// absence is the discriminator: Codex's non-tool items (<c>agent_message</c>,
/// <c>reasoning</c>) arrive under the same <c>item.started</c> type and are filtered
/// precisely by not carrying the named property, so no extra "which item kind is a tool"
/// key is needed. The two modes coexist — flat mode keys off its own <c>type</c> value, so a
/// profile may declare both — and the emitted event is the same frozen
/// <see cref="ToolCallEvent"/> wire member; nothing about the contract widens.</para>
///
/// <para><b>What the path resolver deliberately does not do.</b> Segments are object
/// property names, split on <c>.</c>, and the walk must end on a JSON string: no wildcards,
/// no array indexing, no filters, no escaping for property names that themselves contain a
/// dot. Every shape this exists to read (one tool call, one level down, named by a string
/// property) is reachable without them, and each addition would be a step from "declarative
/// path" toward a query language embedded in config — which §10's config-only onboarding
/// promise does not need and could not validate. A malformed path (empty or
/// whitespace-padded segment) is rejected by
/// <see cref="RunnerConfig.TryLoad"/> at load, so it can never be a silent no-op here.</para>
///
/// <para><b>Silent-stream warning (§11, issue #111).</b> A <c>terminal</c> profile whose
/// mapping matches nothing parses every line and extracts nothing — resume degrades to a
/// permanent cold start and the task's progress clock never advances, with no signal anywhere
/// that it happened. So this reader reports exactly one line per task, at the end of the
/// drain, when it saw well-formed event lines and got neither a session ref nor a tool-call.
/// Nothing to report is the common case and stays silent, and a profile with
/// <c>events.source: none</c> never constructs a reader at all, so it cannot be warned about
/// something it never claimed.</para>
///
/// <para><b>AOT (§10).</b> The stream is parsed with <see cref="JsonDocument"/>, not a
/// source-gen'd <c>JsonSerializerContext</c> DTO: the mapping seam makes the
/// property <em>names</em> a runtime value, which a compile-time DTO cannot
/// express. <see cref="JsonDocument"/>/<see cref="JsonElement"/> are reflection-free
/// readers, so this stays clean under <c>IsAotCompatible</c> — no
/// <c>RunnerJsonContext</c> entry is needed.</para>
///
/// <para><b>Robustness.</b> A line that is not a well-formed JSON object is skipped,
/// never fatal — stray harness stdout (a banner, a warning, a partial write) must
/// not crash the drain or wedge the worker.</para>
/// </summary>
public sealed class TerminalEventReader
{
    private readonly TaskId _task;
    private readonly OutboundEventRing _ring;
    private readonly Action<TaskId> _recordActivity;
    private readonly Action<string>? _onSessionId;
    private readonly Action<string>? _rawLineSink;
    private readonly Action<string> _warn;
    private readonly bool _mappingDeclared;
    private readonly TimeProvider _clock;
    private readonly TerminalStreamMapping _map;

    /// <summary>Set once, from the first <c>system/init</c>: the id is stable for the run.</summary>
    private string? _sessionId;

    /// <summary>Well-formed JSON object lines seen — the denominator of the silent-stream warning.</summary>
    private int _eventLines;

    /// <summary>Set the moment this reader gets anything out of the stream (session ref or
    /// tool-call): the one condition that makes the mapping demonstrably right for this harness.</summary>
    private bool _extractedSomething;

    /// <summary>Guards the one-per-task warning against a second <see cref="ReadToEndAsync"/>.</summary>
    private bool _warned;

    /// <param name="recordActivity">
    /// The per-task liveness signal (<see cref="ProcessSupervisor.RecordActivity"/>):
    /// every well-formed line bumps it, so "the worker is still producing output"
    /// keeps <see cref="ProcessSupervisor.IsTaskLive"/> true between richer events.
    /// </param>
    /// <param name="onSessionId">
    /// Invoked once with the harness session id from <c>system/init</c>. The
    /// supervisor stores it on <see cref="SupervisedTask.SessionId"/> and emits a
    /// <see cref="SessionStartedEvent"/> so the plane can stamp the ref for §11
    /// resume. This reader itself does events and liveness only — the ref is
    /// opaque to it.
    /// </param>
    /// <param name="rawLineSink">
    /// §12 transcript capture tee: invoked with every raw stdout line — verbatim,
    /// before parsing, including blank and non-JSON lines — so the one drain that maps
    /// events also captures the transcript. Null when capture is off; then this is
    /// exactly the pre-capture event-only reader. Teeing, not diverting: the sink never
    /// sees event mapping and mapping never sees the sink.
    /// </param>
    /// <param name="warn">
    /// Where the once-per-task silent-stream line goes. Defaults to docketd's log sink
    /// (stderr), which is where every other operator-facing degradation notice in the
    /// supervisor is written, so the production call site needs no wiring; tests inject a
    /// collector instead of redirecting the process's console.
    /// </param>
    public TerminalEventReader(
        TaskId task,
        OutboundEventRing ring,
        Action<TaskId> recordActivity,
        IReadOnlyDictionary<string, string> mapping,
        TimeProvider clock,
        Action<string>? onSessionId = null,
        Action<string>? rawLineSink = null,
        Action<string>? warn = null)
    {
        _task = task;
        _ring = ring;
        _recordActivity = recordActivity;
        _onSessionId = onSessionId;
        _rawLineSink = rawLineSink;
        _warn = warn ?? Console.Error.WriteLine;
        _mappingDeclared = mapping.Count > 0;
        _clock = clock;
        _map = TerminalStreamMapping.From(mapping);
    }

    /// <summary>
    /// Drains <paramref name="reader"/> line-by-line to EOF, mapping each line to
    /// events. Returns cleanly on EOF, on <paramref name="ct"/> cancellation, or
    /// when the underlying pipe is torn down by a kill — the drain never throws
    /// into its host background task (that would defeat the anti-deadlock guarantee).
    /// </summary>
    public async Task ReadToEndAsync(TextReader reader, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                // §12 capture tee: hand the verbatim line to the transcript sink first,
                // then map it to events. A sink throw must not kill the drain (that
                // would defeat the anti-deadlock guarantee), so it is best-effort.
                if (_rawLineSink is not null)
                {
                    try { _rawLineSink(line); }
                    catch { /* capture is never allowed to affect the worker */ }
                }
                ProcessLine(line);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancelled on teardown, or the worker's stdout pipe was closed by a
            // kill mid-read — either way the stream is over; end the drain quietly.
        }

        // The stream is over one way or another, which for this reader is the worker's
        // exit: last chance to say that a whole task's worth of output yielded nothing.
        // Deliberately outside the try — an unexpected throw is a louder signal than
        // this line and should not be dressed up as a mapping problem.
        ReportSilentStream();
    }

    /// <summary>
    /// §11 resume honesty (issue #111): one line per task when this <c>terminal</c> profile
    /// read well-formed event lines and extracted neither a session ref nor a tool-call.
    ///
    /// <para>The guard is deliberately "saw event lines but got nothing", not "got nothing":
    /// a worker whose stdout carried no parseable event line at all has a different diagnosis
    /// — the harness is not streaming JSON, or it died before its first line — which its exit
    /// code and transcript already show, and warning there would fire on every fast failure.
    /// The case that hides is the stream that parses perfectly and yields nothing, which is
    /// always a mapping that does not match the harness.</para>
    /// </summary>
    private void ReportSilentStream()
    {
        if (_warned || _extractedSomething || _eventLines == 0)
            return;

        _warned = true;
        var cause = _mappingDeclared
            ? "the profile's events.mapping matched none of them"
            : "the profile declares no events.mapping and the built-in claude stream-json defaults matched none of them";
        _warn(
            $"docketd: task {_task.Value}: events.source is 'terminal' and the worker's stdout parsed as " +
            $"{_eventLines} JSON event line(s), but {cause} — no session ref and no tool-call came out of the " +
            "whole task. So §11 resume has no ref to resume from (every redispatch of this task is a cold " +
            "start) and the task had no progress signal: the no-progress ceiling was the only clock governing " +
            "it, and a wedged agent could not have been told from a busy one. Check events.mapping against a " +
            "real run of this harness (§10).");
    }

    private void ProcessLine(string line)
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
            return; // stray non-JSON stdout — skip it and keep draining.
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return; // a bare scalar/array is not a stream event.

            // A well-formed object line is forward progress: bump per-task liveness.
            _recordActivity(_task);
            _eventLines++;

            var type = GetString(root, _map.TypeKey);

            if (type == _map.SystemType)
            {
                CaptureSessionId(root);
                return;
            }

            if (type == _map.AssistantType)
                EmitToolCalls(root);
            else if (_map.FlatToolCalls && type == _map.ToolEventType)
                EmitFlatToolCall(root);

            // SubagentSpawnedEvent (§10 telemetry ingest) is intentionally NOT
            // emitted here. The claude stream-json shape has no first-class
            // subagent-spawned line carrying a clean (AgentId, ParentAgentId) pair:
            // a subagent surfaces only as a `Task` tool_use plus `parent_tool_use_id`
            // back-references, which is inference, not a clean signal. Per §10 that
            // makes the subagent tree a documented dashboard empty-state rather than
            // a fabricated lineage. A harness that DOES emit clean lineage can be
            // wired later behind additional Mapping keys.
        }
    }

    /// <summary>§11 resume seam: capture the session id from the first <c>system/init</c>.</summary>
    private void CaptureSessionId(JsonElement root)
    {
        if (_sessionId is not null)
            return;
        if (GetString(root, _map.SubtypeKey) != _map.InitSubtype)
            return;
        if (GetString(root, _map.SessionIdKey) is not { Length: > 0 } sid)
            return;

        _sessionId = sid;
        _extractedSomething = true;
        _onSessionId?.Invoke(sid);
    }

    /// <summary>Emit one <see cref="ToolCallEvent"/> per <c>tool_use</c> block in an assistant turn.</summary>
    private void EmitToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty(_map.MessageKey, out var message) || message.ValueKind != JsonValueKind.Object)
            return;
        if (!message.TryGetProperty(_map.ContentKey, out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
                continue;
            if (GetString(block, _map.BlockTypeKey) != _map.ToolUseBlockType)
                continue;
            if (GetString(block, _map.ToolNameKey) is not { Length: > 0 } tool)
                continue; // a tool_use with no name is not an actionable progress signal.

            _ring.Enqueue(new ToolCallEvent(_task, tool, _clock.GetUtcNow()));
            _extractedSomething = true;
        }
    }

    /// <summary>
    /// Flat mode: this whole line <em>is</em> one tool call, so emit at most one
    /// <see cref="ToolCallEvent"/> from it — the first declared
    /// <c>tool_name_path</c> alternative that resolves to a non-empty string. A line where
    /// none resolves is not a tool call (Codex's <c>agent_message</c> items ride the same
    /// <c>type</c>), and silence is the right answer for it.
    /// </summary>
    private void EmitFlatToolCall(JsonElement root)
    {
        foreach (var path in _map.ToolNamePaths)
        {
            if (ResolvePath(root, path) is not { Length: > 0 } tool)
                continue;

            _ring.Enqueue(new ToolCallEvent(_task, tool, _clock.GetUtcNow()));
            _extractedSomething = true;
            return;
        }
    }

    /// <summary>
    /// Walks a pre-split dotted path of object property names and returns the string it lands
    /// on, or null if any segment is missing, any intermediate value is not an object, or the
    /// destination is not a string. Strict by construction: there is no wildcard, no index, and
    /// no coercion of a number or bool into a tool name, so a path either describes the
    /// harness's shape exactly or reads nothing.
    /// </summary>
    private static string? ResolvePath(JsonElement root, string[] segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return null;
            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// The resolved discriminator/key set the <see cref="TerminalEventReader"/> reads
/// with — claude <c>stream-json</c> defaults, each overridable via
/// <see cref="EventsConfig.Mapping"/> (§10). Kept as a plain struct-of-strings so
/// the hot path is field reads, not dictionary lookups.
/// </summary>
internal readonly record struct TerminalStreamMapping(
    string TypeKey,
    string SystemType,
    string AssistantType,
    string SubtypeKey,
    string InitSubtype,
    string SessionIdKey,
    string MessageKey,
    string ContentKey,
    string BlockTypeKey,
    string ToolUseBlockType,
    string ToolNameKey,
    string? ToolEventType,
    string[][] ToolNamePaths)
{
    /// <summary>
    /// True when the profile declared the flat tool-call mode (issue #111). Both keys are
    /// required: <c>tool_event_type</c> alone would match lines it cannot name a tool from,
    /// and <c>tool_name_path</c> alone would never be consulted. <c>RunnerConfig</c> rejects a
    /// half-declared pair at load, so this only ever reads false because nothing was asked for.
    /// </summary>
    public bool FlatToolCalls => ToolEventType is { Length: > 0 } && ToolNamePaths.Length > 0;

    public static TerminalStreamMapping From(IReadOnlyDictionary<string, string> mapping) => new(
        Pick(mapping, "type_key", "type"),
        Pick(mapping, "system_type", "system"),
        Pick(mapping, "assistant_type", "assistant"),
        Pick(mapping, "subtype_key", "subtype"),
        Pick(mapping, "init_subtype", "init"),
        Pick(mapping, "session_id_key", "session_id"),
        Pick(mapping, "message_key", "message"),
        Pick(mapping, "content_key", "content"),
        Pick(mapping, "block_type_key", "type"),
        Pick(mapping, "tool_use_block_type", "tool_use"),
        Pick(mapping, "tool_name_key", "name"),
        PickOrNull(mapping, "tool_event_type"),
        ParseToolNamePaths(PickOrNull(mapping, "tool_name_path")));

    /// <summary>
    /// Resolves a mapping key against the claude default. Shared with
    /// <see cref="RunnerConfig"/> so load-time validation reasons about the same effective
    /// values the reader will use, rather than a second copy of the defaults.
    /// </summary>
    internal static string Pick(IReadOnlyDictionary<string, string> mapping, string key, string fallback) =>
        mapping.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    private static string? PickOrNull(IReadOnlyDictionary<string, string> mapping, string key) =>
        mapping.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>
    /// Splits <c>tool_name_path</c> once, at construction: commas separate alternatives (tried
    /// in declaration order), dots separate object property names. Whitespace around an
    /// alternative is tolerated so a config author can breathe; whitespace <em>inside</em> a
    /// segment is not, because a property name is exact. A malformed alternative is dropped
    /// here and reported as a config error by <see cref="RunnerConfig.TryLoad"/> — the load
    /// error is the operator-facing half, this is just the reader refusing to guess.
    /// </summary>
    internal static string[][] ParseToolNamePaths(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var paths = new List<string[]>();
        foreach (var alternative in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var segments = alternative.Split('.');
            if (Array.Exists(segments, s => s.Length == 0 || s.Trim() != s))
                continue;
            paths.Add(segments);
        }

        return [.. paths];
    }
}
