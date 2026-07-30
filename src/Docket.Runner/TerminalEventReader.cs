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
/// </list>
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
    private readonly TimeProvider _clock;
    private readonly TerminalStreamMapping _map;

    /// <summary>Set once, from the first <c>system/init</c>: the id is stable for the run.</summary>
    private string? _sessionId;

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
    public TerminalEventReader(
        TaskId task,
        OutboundEventRing ring,
        Action<TaskId> recordActivity,
        IReadOnlyDictionary<string, string> mapping,
        TimeProvider clock,
        Action<string>? onSessionId = null)
    {
        _task = task;
        _ring = ring;
        _recordActivity = recordActivity;
        _onSessionId = onSessionId;
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
                ProcessLine(line);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancelled on teardown, or the worker's stdout pipe was closed by a
            // kill mid-read — either way the stream is over; end the drain quietly.
        }
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

            var type = GetString(root, _map.TypeKey);

            if (type == _map.SystemType)
            {
                CaptureSessionId(root);
                return;
            }

            if (type == _map.AssistantType)
                EmitToolCalls(root);

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
        }
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
    string ToolNameKey)
{
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
        Pick(mapping, "tool_name_key", "name"));

    private static string Pick(IReadOnlyDictionary<string, string> mapping, string key, string fallback) =>
        mapping.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
}
