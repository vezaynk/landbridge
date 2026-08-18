using System.Text.Json;
using System.Text.Json.Serialization;

namespace Landbridge.Contracts;

/// <summary>
/// Source-generated JSON metadata for the wire vocabulary (§10 — "a source-gen'd
/// JsonSerializerContext keeps it AOT-clean"). Covers every command, every
/// event, and the machine heartbeat — both sides encode and decode through it.
/// The runner's config document keeps its own separate context (config stays
/// runner-only). Snake_case matches the documented wire shape; string enums
/// keep dispositions legible.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DispatchCommand))]
[JsonSerializable(typeof(StopCommand))]
[JsonSerializable(typeof(KillCommand))]
[JsonSerializable(typeof(OpenForwardCommand))]
[JsonSerializable(typeof(CloseForwardCommand))]
[JsonSerializable(typeof(ReadTranscriptCommand))]
[JsonSerializable(typeof(StartProcessCommand))]
[JsonSerializable(typeof(StopProcessCommand))]
[JsonSerializable(typeof(WriteProcessCommand))]
[JsonSerializable(typeof(StartedEvent))]
[JsonSerializable(typeof(SessionStartedEvent))]
[JsonSerializable(typeof(AliveEvent))]
[JsonSerializable(typeof(ToolCallEvent))]
[JsonSerializable(typeof(UsageReportedEvent))]
[JsonSerializable(typeof(SubagentSpawnedEvent))]
[JsonSerializable(typeof(PromptCommand))]
[JsonSerializable(typeof(TurnEndedEvent))]
[JsonSerializable(typeof(ExitedEvent))]
[JsonSerializable(typeof(AuthFailedEvent))]
[JsonSerializable(typeof(ForwardOpenedEvent))]
[JsonSerializable(typeof(ForwardClosedEvent))]
[JsonSerializable(typeof(RebootedEvent))]
[JsonSerializable(typeof(TranscriptChunkEvent))]
[JsonSerializable(typeof(ProcessStartedEvent))]
[JsonSerializable(typeof(ProcessStoppedEvent))]
[JsonSerializable(typeof(ProcessWrittenEvent))]
[JsonSerializable(typeof(MachineHeartbeat))]
internal sealed partial class RunnerWireContext : JsonSerializerContext;
