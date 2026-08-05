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
    public List<ServiceDto>? Services { get; set; }
}

// §10 operator-declared services: long-lived processes docketd supervises as its own
// children, outside any task's process tree. Config-declared only in v1 — an
// agent-initiated service would need a new worker tool and a new wire command.
internal sealed class ServiceDto
{
    public string? Name { get; set; }
    public List<string>? Spawn { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public int? Port { get; set; }
    public ReadinessDto? Readiness { get; set; }
    public RestartDto? Restart { get; set; }
    public LogsDto? Logs { get; set; }
    public string? Backend { get; set; }

    // The honest "stop": desired state lives in config, so turning a service off is an
    // operator edit rather than a dashboard command whose effect a restart would undo.
    public bool? Enabled { get; set; }
}

internal sealed class ReadinessDto
{
    public int? TcpPort { get; set; }
    public double? TimeoutSeconds { get; set; }
}

internal sealed class RestartDto
{
    public double? MaxBackoffSeconds { get; set; }
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

    // §10 agent-initiated services: whether a task on this profile may call
    // start_service, and how many it may hold. Off by default — enabling it is the
    // machine owner's deliberate choice, the same shape as the open/strict archetypes.
    public ProfileServicesDto? Services { get; set; }
}

internal sealed class ProfileServicesDto
{
    public bool? AgentInitiated { get; set; }
    public int? MaxAgentInitiated { get; set; }
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

    // §10 telemetry ingest: harness-specific opt-in variables, as data — docketd
    // itself sets only vendor-neutral OTEL_* (see HarnessTelemetry). Claude Code
    // needs { "CLAUDE_CODE_ENABLE_TELEMETRY": "1" } here. Applied only when
    // `otel` is on and a destination resolves.
    public Dictionary<string, string>? Env { get; set; }
}

internal sealed class LogsDto
{
    public string? Path { get; set; }
    public string? Format { get; set; }

    // §12 machine-local transcript capture. `capture` toggles it (default off);
    // `max_bytes` caps each captured stream per instance; `prune_after_days` is the
    // local hygiene window (0 disables). See LogsConfig / TranscriptStore.
    public bool? Capture { get; set; }
    public long? MaxBytes { get; set; }
    public int? PruneAfterDays { get; set; }
}
