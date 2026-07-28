using System.Text.Json;
using System.Text.Json.Serialization;

namespace Docket.Runner;

/// <summary>
/// Source-generated JSON metadata (§10 — "a source-gen'd JsonSerializerContext
/// keeps it AOT-clean"). Covers both the config document and the inbound
/// command types the wire boundary decodes. Snake_case matches the config's
/// documented shape (<c>work_root</c>, <c>max_concurrent</c>, …); string enums
/// keep dispositions legible on the wire.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunnerConfigDto))]
[JsonSerializable(typeof(DispatchCommand))]
[JsonSerializable(typeof(StopCommand))]
[JsonSerializable(typeof(KillCommand))]
[JsonSerializable(typeof(OpenForwardCommand))]
internal sealed partial class RunnerJsonContext : JsonSerializerContext;
