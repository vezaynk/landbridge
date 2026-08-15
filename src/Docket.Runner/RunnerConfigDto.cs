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
// agent-started process is declared over the wire instead (§10 start_process).
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

    // §10 how docketd talks to the worker: `stream` (spawn it, read whatever NDJSON it
    // prints — the original behaviour and the default) or `acp` (drive it over the Agent
    // Client Protocol). A bare string for the same reason `stdin` is one: there is one
    // decision here, and `protocol: acp` is what a human types.
    public string? Protocol { get; set; }

    // §10 the worker's opening turn, for `protocol: acp` only. A stream-mode profile puts
    // its prompt in the spawn argv; an ACP agent takes no argv prompt, so the text has to
    // travel over the wire instead. Same `{...}` substitutions as the argv.
    public string? Prompt { get; set; }
    public StopDto? Stop { get; set; }
    public ResumeDto? Resume { get; set; }
    public EventsDto? Events { get; set; }
    public TelemetryDto? Telemetry { get; set; }
    public LogsDto? Logs { get; set; }
    public int? MaxConcurrent { get; set; }

    // §10 dead-man switch: `deadman` (hold the stdin pipe open for the worker's whole
    // life — the default and every profile's behaviour before this key existed) or
    // `closed` (EOF right after spawn) for a harness that blocks reading piped stdin.
    // A bare string rather than a block: there is one decision here and no room for a
    // second, and a `stdin: closed` line is what a human types.
    public string? Stdin { get; set; }

    // §10 agent-started processes: whether a task on this profile may call start_process, and
    // how many the machine may hold. Off by default — enabling it is the machine owner's
    // deliberate choice, the same shape as the open/strict archetypes. Named `processes`, not
    // `services`, because a process and a service are different things (§10) and this is the
    // key a human types.
    public ProfileProcessesDto? Processes { get; set; }

    // §10 / #112 G3: per-spawn environment. Substituted with the same {task_id} /
    // {machine_id} / {work_dir} / {mcp_config} / {session_id} tokens spawn gets. The
    // four DOCKET_* variables docketd stamps itself are refused at load, not silently
    // dropped — a profile that thinks it overwrote DOCKET_WORKER_TOKEN must not start.
    public Dictionary<string, string>? Env { get; set; }

    // #112 G2: files written into {work_dir} before the harness starts. Paths are
    // substituted then jailed to the work dir at spawn; a path that escapes fails
    // the spawn rather than writing outside it.
    public List<ProfileFileDto>? Files { get; set; }

    // Argv hooks (never a shell). before_spawn is fail-closed; after_exit is
    // best-effort. See ProfileHooks.
    public ProfileHooksDto? Hooks { get; set; }
}

internal sealed class ProfileFileDto
{
    public string? Path { get; set; }
    public string? Contents { get; set; }
    public string? Mode { get; set; }
}

internal sealed class ProfileHooksDto
{
    public List<string>? BeforeSpawn { get; set; }
    public List<string>? AfterExit { get; set; }
}

internal sealed class ProfileProcessesDto
{
    public bool? AgentInitiated { get; set; }
    public int? Max { get; set; }
}

internal sealed class StopDto
{
    public string? Mode { get; set; }
    public string? Message { get; set; }
    public double? WindDownSeconds { get; set; }

    // No `signal` property: the deadline's kill is always the portable tree-kill, so a
    // signal name had nothing to select. A config still carrying the key parses fine —
    // unknown members are ignored — and now says as little as it always did.
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
    // No `path` or `format`: capture writes a fixed state-dir layout no path can steer,
    // and nothing ever read the format label. Configs still carrying either key parse
    // unchanged — unknown members are ignored.

    // §12 machine-local transcript capture. `capture` toggles it (default off);
    // `max_bytes` caps each captured stream per instance; `prune_after_days` is the
    // local hygiene window (0 disables). See LogsConfig / TranscriptStore.
    public bool? Capture { get; set; }
    public long? MaxBytes { get; set; }
    public int? PruneAfterDays { get; set; }
}
