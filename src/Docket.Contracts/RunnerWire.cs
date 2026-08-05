using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Docket.Contracts;

/// <summary>
/// The wire boundary for the frozen contract (§10). Both sides talk through it:
/// <c>docketd</c> encodes events/heartbeats and decodes commands; the control
/// plane encodes commands and decodes events/heartbeats. The rule that defines
/// the contract holds on decode — <b>anything outside the vocabulary is
/// rejected</b> (returns <c>null</c>) rather than guessed at.
///
/// The envelope is a type-discriminated object: the record's own fields plus a
/// <c>type</c> property. Encoding serializes the record through the source-gen
/// context (AOT-clean) into a <see cref="JsonObject"/> and adds <c>type</c>;
/// decoding switches on <c>type</c> and deserializes the concrete record, which
/// ignores the extra discriminator.
/// </summary>
public static class RunnerWire
{
    public const string Dispatch = "dispatch";
    public const string Stop = "stop";
    public const string Kill = "kill";
    public const string OpenForward = "open-forward";
    public const string ReadTranscript = "read-transcript";
    public const string StartService = "start-service";

    public const string Started = "started";
    public const string SessionStarted = "session-started";
    public const string Alive = "alive";
    public const string ToolCall = "tool-call";
    public const string SubagentSpawned = "subagent-spawned";
    public const string Exited = "exited";
    public const string AuthFailed = "auth-failed";
    public const string ForwardOpened = "forward-opened";
    public const string ForwardClosed = "forward-closed";
    public const string Rebooted = "rebooted";
    public const string TranscriptChunk = "transcript-chunk";
    public const string ServiceStarted = "service-started";

    /// <summary>The machine-heartbeat envelope discriminator (§10 — docketd's own timer).
    /// Not part of the frozen command/event enum; the heartbeat is the runner's
    /// periodic self-report, carried on the same channel.</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>The closed outbound (control plane → runner) vocabulary.</summary>
    public static IReadOnlySet<string> Commands { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Dispatch, Stop, Kill, OpenForward, ReadTranscript, StartService };

    /// <summary>The closed inbound (runner → control plane) vocabulary.</summary>
    public static IReadOnlySet<string> Events { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Started, SessionStarted, Alive, ToolCall, SubagentSpawned, Exited, AuthFailed, ForwardOpened, ForwardClosed, Rebooted,
            TranscriptChunk, ServiceStarted,
        };

    public static bool IsKnownCommand(string? type) => type is not null && Commands.Contains(type);

    public static bool IsKnownEvent(string? type) => type is not null && Events.Contains(type);

    /// <summary>The optional envelope property carrying the W3C traceparent (§1
    /// end-to-end tracing). Transport metadata alongside <c>type</c> — NOT part of
    /// the frozen §10 command vocabulary, and ignored by the concrete-record
    /// deserialize, so the domain <see cref="RunnerCommand"/> is unchanged.</summary>
    public const string TraceParent = "traceparent";

    // ── Encode ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a command to its type-discriminated JSON envelope. When
    /// <paramref name="traceparent"/> is non-null it rides the envelope alongside
    /// <c>type</c> as opaque transport metadata (§1 tracing); a null leaves the
    /// envelope byte-for-byte as before (backward compatible).
    /// </summary>
    public static string EncodeCommand(RunnerCommand command, string? traceparent = null)
    {
        var (obj, type) = command switch
        {
            DispatchCommand d => (ToObject(d, RunnerWireContext.Default.DispatchCommand), Dispatch),
            StopCommand s => (ToObject(s, RunnerWireContext.Default.StopCommand), Stop),
            KillCommand k => (ToObject(k, RunnerWireContext.Default.KillCommand), Kill),
            OpenForwardCommand o => (ToObject(o, RunnerWireContext.Default.OpenForwardCommand), OpenForward),
            ReadTranscriptCommand r => (ToObject(r, RunnerWireContext.Default.ReadTranscriptCommand), ReadTranscript),
            StartServiceCommand ss => (ToObject(ss, RunnerWireContext.Default.StartServiceCommand), StartService),
            // Unreachable: the hierarchy is closed (§10). Explicit so a future
            // member cannot silently serialize as a typeless envelope.
            _ => throw new ArgumentOutOfRangeException(
                nameof(command), command.GetType().Name, "outside the runner command vocabulary"),
        };
        obj["type"] = type;
        if (traceparent is not null)
            obj[TraceParent] = traceparent;
        return obj.ToJsonString();
    }

    /// <summary>Encodes an event to its type-discriminated JSON envelope.</summary>
    public static string EncodeEvent(RunnerEvent evt)
    {
        var (obj, type) = evt switch
        {
            StartedEvent e => (ToObject(e, RunnerWireContext.Default.StartedEvent), Started),
            SessionStartedEvent e => (ToObject(e, RunnerWireContext.Default.SessionStartedEvent), SessionStarted),
            AliveEvent e => (ToObject(e, RunnerWireContext.Default.AliveEvent), Alive),
            ToolCallEvent e => (ToObject(e, RunnerWireContext.Default.ToolCallEvent), ToolCall),
            SubagentSpawnedEvent e => (ToObject(e, RunnerWireContext.Default.SubagentSpawnedEvent), SubagentSpawned),
            ExitedEvent e => (ToObject(e, RunnerWireContext.Default.ExitedEvent), Exited),
            AuthFailedEvent e => (ToObject(e, RunnerWireContext.Default.AuthFailedEvent), AuthFailed),
            ForwardOpenedEvent e => (ToObject(e, RunnerWireContext.Default.ForwardOpenedEvent), ForwardOpened),
            ForwardClosedEvent e => (ToObject(e, RunnerWireContext.Default.ForwardClosedEvent), ForwardClosed),
            RebootedEvent e => (ToObject(e, RunnerWireContext.Default.RebootedEvent), Rebooted),
            TranscriptChunkEvent e => (ToObject(e, RunnerWireContext.Default.TranscriptChunkEvent), TranscriptChunk),
            ServiceStartedEvent e => (ToObject(e, RunnerWireContext.Default.ServiceStartedEvent), ServiceStarted),
            _ => throw new ArgumentOutOfRangeException(
                nameof(evt), evt.GetType().Name, "outside the runner event vocabulary"),
        };
        obj["type"] = type;
        return obj.ToJsonString();
    }

    /// <summary>Encodes a machine heartbeat to its type-discriminated JSON envelope.</summary>
    public static string EncodeHeartbeat(MachineHeartbeat heartbeat)
    {
        var obj = ToObject(heartbeat, RunnerWireContext.Default.MachineHeartbeat);
        obj["type"] = Heartbeat;
        return obj.ToJsonString();
    }

    // ── Decode ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes one command from its JSON envelope, or returns <c>null</c> when
    /// the <c>type</c> is missing or outside the vocabulary — the rejection §10
    /// requires. The concrete-type deserialize ignores the <c>type</c> property.
    /// </summary>
    public static RunnerCommand? DecodeCommand(string json) => DecodeCommand(json, out _);

    /// <summary>
    /// Decodes one command and also surfaces the optional <c>traceparent</c>
    /// envelope property (§1 tracing), or <c>null</c> when the command is missing
    /// or outside the §10 vocabulary. <paramref name="traceparent"/> is <c>null</c>
    /// when the envelope carries none (an older sender, or a non-traced dispatch).
    /// The domain record is identical either way — the traceparent lives only on
    /// the envelope, never on the concrete record.
    /// </summary>
    public static RunnerCommand? DecodeCommand(string json, out string? traceparent)
    {
        traceparent = null;
        if (!TryParse(json, out var doc))
            return null;
        using (doc)
        {
            if (!TryReadType(doc.RootElement, out var type))
                return null;
            var command = type switch
            {
                Dispatch => (RunnerCommand?)doc.RootElement.Deserialize(RunnerWireContext.Default.DispatchCommand),
                Stop => doc.RootElement.Deserialize(RunnerWireContext.Default.StopCommand),
                Kill => doc.RootElement.Deserialize(RunnerWireContext.Default.KillCommand),
                OpenForward => doc.RootElement.Deserialize(RunnerWireContext.Default.OpenForwardCommand),
                ReadTranscript => doc.RootElement.Deserialize(RunnerWireContext.Default.ReadTranscriptCommand),
                StartService => doc.RootElement.Deserialize(RunnerWireContext.Default.StartServiceCommand),
                _ => null, // §10: reject anything outside the vocabulary.
            };
            if (command is not null && TryReadString(doc.RootElement, TraceParent, out var tp))
                traceparent = tp;
            return command;
        }
    }

    /// <summary>Decodes one event, or <c>null</c> when the <c>type</c> is missing
    /// or outside the vocabulary (§10 symmetry with <see cref="DecodeCommand"/>).</summary>
    public static RunnerEvent? DecodeEvent(string json)
    {
        if (!TryParse(json, out var doc))
            return null;
        using (doc)
        {
            if (!TryReadType(doc.RootElement, out var type))
                return null;
            return type switch
            {
                Started => doc.RootElement.Deserialize(RunnerWireContext.Default.StartedEvent),
                SessionStarted => doc.RootElement.Deserialize(RunnerWireContext.Default.SessionStartedEvent),
                Alive => doc.RootElement.Deserialize(RunnerWireContext.Default.AliveEvent),
                ToolCall => doc.RootElement.Deserialize(RunnerWireContext.Default.ToolCallEvent),
                SubagentSpawned => doc.RootElement.Deserialize(RunnerWireContext.Default.SubagentSpawnedEvent),
                Exited => doc.RootElement.Deserialize(RunnerWireContext.Default.ExitedEvent),
                AuthFailed => doc.RootElement.Deserialize(RunnerWireContext.Default.AuthFailedEvent),
                ForwardOpened => doc.RootElement.Deserialize(RunnerWireContext.Default.ForwardOpenedEvent),
                ForwardClosed => doc.RootElement.Deserialize(RunnerWireContext.Default.ForwardClosedEvent),
                Rebooted => doc.RootElement.Deserialize(RunnerWireContext.Default.RebootedEvent),
                TranscriptChunk => doc.RootElement.Deserialize(RunnerWireContext.Default.TranscriptChunkEvent),
                ServiceStarted => doc.RootElement.Deserialize(RunnerWireContext.Default.ServiceStartedEvent),
                _ => null,
            };
        }
    }

    /// <summary>Decodes a machine heartbeat, or <c>null</c> when the envelope is
    /// not a heartbeat.</summary>
    public static MachineHeartbeat? DecodeHeartbeat(string json)
    {
        if (!TryParse(json, out var doc))
            return null;
        using (doc)
        {
            if (!TryReadType(doc.RootElement, out var type) || type != Heartbeat)
                return null;
            return doc.RootElement.Deserialize(RunnerWireContext.Default.MachineHeartbeat);
        }
    }

    // ── Internals ───────────────────────────────────────────────────────────────

    private static JsonObject ToObject<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToNode(value, typeInfo) as JsonObject
        ?? throw new InvalidOperationException("wire envelope must serialize to a JSON object");

    private static bool TryParse(string json, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    private static bool TryReadType(JsonElement root, out string type)
    {
        type = "";
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        type = typeElement.GetString()!;
        return true;
    }

    private static bool TryReadString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString();
        return true;
    }
}
