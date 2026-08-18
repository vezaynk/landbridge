using System.Text.Json;
using System.Text.Json.Serialization;

namespace Landbridge.Runner;

/// <summary>
/// Source-generated JSON metadata for the runner's <b>config document only</b>
/// (§10 — "a source-gen'd JsonSerializerContext keeps it AOT-clean"). The wire
/// vocabulary (commands, events, heartbeat) moved to
/// <see cref="Landbridge.Contracts.RunnerWire"/> with its own context; config stays
/// runner-local because landbridged is the only thing that reads it. Snake_case
/// matches the config's documented shape (<c>work_root</c>, <c>max_concurrent</c>, …).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunnerConfigDto))]
internal sealed partial class RunnerJsonContext : JsonSerializerContext;
