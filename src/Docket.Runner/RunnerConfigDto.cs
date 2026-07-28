namespace Docket.Runner;

// Wire-shape DTOs for the config file: only simple types (strings, numbers,
// arrays, string maps) so System.Text.Json source-gen stays AOT-clean and no
// enum/duration converters are needed. Casing, enum names, and required-field
// checks are handled in RunnerConfig validation, which turns these into the
// validated domain records. Durations are seconds (human-friendly), not the
// TimeSpan "hh:mm:ss" wire format.

internal sealed class RunnerConfigDto
{
    public MachineDto? Machine { get; set; }
    public List<ProfileDto>? Profiles { get; set; }
}

internal sealed class MachineDto
{
    public string? WorkRoot { get; set; }
    public double? HeartbeatSeconds { get; set; }
    public BackPressureDto? BackPressure { get; set; }
}

internal sealed class BackPressureDto
{
    public double? MaxCpuLoad { get; set; }
    public double? MaxMemoryLoad { get; set; }
    public double? MaxDiskUsage { get; set; }
}

internal sealed class ProfileDto
{
    public string? Name { get; set; }
    public List<string>? Spawn { get; set; }
    public StopDto? Stop { get; set; }
    public ResumeDto? Resume { get; set; }
    public EventsDto? Events { get; set; }
    public TelemetryDto? Telemetry { get; set; }
    public LogsDto? Logs { get; set; }
    public int? MaxConcurrent { get; set; }
}

internal sealed class StopDto
{
    public string? Mode { get; set; }
    public string? Signal { get; set; }
    public string? Message { get; set; }
    public double? WindDownSeconds { get; set; }
}

internal sealed class ResumeDto
{
    public List<string>? Args { get; set; }
}

internal sealed class EventsDto
{
    public string? Source { get; set; }
    public Dictionary<string, string>? Mapping { get; set; }
}

internal sealed class TelemetryDto
{
    public bool? Otel { get; set; }
    public string? Endpoint { get; set; }
}

internal sealed class LogsDto
{
    public string? Path { get; set; }
    public string? Format { get; set; }
}
