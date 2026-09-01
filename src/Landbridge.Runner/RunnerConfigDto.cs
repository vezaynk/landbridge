namespace Landbridge.Runner;

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
    // Parsed only so a leftover block can be refused. landbridged no longer
    // supervises operator fixtures.
    public List<ServiceDto>? Services { get; set; }
}

// Presence-only. A leftover non-empty services[] is refused at load; members are ignored.
internal sealed class ServiceDto { }

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

    // §10 the worker's opening turn, sent as `session/prompt`. An ACP agent takes no prompt
    // on argv, so this is where a worker is told what it is. Same `{...}` substitutions as
    // the spawn argv. Required.
    public string? Prompt { get; set; }

    // §11 / ideas/sessions.md: the turn sent to wake a live session when there is new input
    // on the assignment. Configuration, never content — the answer itself stays on the
    // assignment and is pulled over the authenticated MCP call, so this text only ever says
    // "go read", and must name the landbridge tools the way THIS harness spells them.
    public string? FollowUp { get; set; }

    // §10: which of the agent's declared ACP authMethods to use when it refuses session/new
    // with "authentication required". Only consulted on that refusal, so a profile whose
    // agent needs no authentication (claude-agent-acp declares none) leaves it unset. Unset
    // on an agent that DOES need it picks the first method the agent declared, which is the
    // only preference signal ACP gives; name one here when that guess is wrong — codex-acp
    // offers `api-key` and `chat-gpt`, and only the first works unattended.
    public string? AuthMethod { get; set; }

    // ACP session/set_config_option pins, keyed by the agent's advertised
    // configId. Consulted after session/new (or session/load): each pair is sent
    // only when that session advertised a select listing the exact value.
    // OpenCode ACP otherwise defaults to opencode/big-pickle and ignores
    // opencode.json — `{ "model": "anthropic/claude-haiku-4-5-20251001" }` is
    // the pin. An unadvertised key is skipped, not an error.
    public Dictionary<string, string>? ConfigOptions { get; set; }

    // ACP session/set_mode after session/new (or session/load). Only sent when
    // that session advertised the modeId. Goose 1.46 defaults to `auto`.
    public string? SessionMode { get; set; }

    public StopDto? Stop { get; set; }
    public TelemetryDto? Telemetry { get; set; }
    public LogsDto? Logs { get; set; }

    // §10 agent-started processes: whether a task on this profile may call start_process, and
    // how many the machine may hold. Off by default — enabling it is the machine owner's
    // deliberate choice, the same shape as the open/strict archetypes. Named `processes`
    // because leftover `services[]` is refused at load.
    public ProfileProcessesDto? Processes { get; set; }

    // §10 / #112 G3: per-spawn environment. Substituted with the same {session_id} /
    // {machine_id} / {work_dir} / {mcp_config} / {session_id} tokens spawn gets. The
    // four LANDBRIDGE_* variables landbridged stamps itself are refused at load, not silently
    // dropped — a profile that thinks it overwrote LANDBRIDGE_WORKER_TOKEN must not start.
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
    // No `mode` and no `message`. A stop is `session/cancel` on the live connection, which
    // the agent is specified to honour, followed by the deadline's portable tree-kill —
    // there is nothing left for a mode to select or a template to fill. Configs still
    // carrying either key parse fine (unknown members are ignored) and now say nothing.
    public double? WindDownSeconds { get; set; }
}

internal sealed class TelemetryDto
{
    public bool? Otel { get; set; }
    public string? Endpoint { get; set; }

    // §10 telemetry ingest: harness-specific opt-in variables, as data — landbridged
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
