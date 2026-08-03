using System.Text.Json;

namespace Docket.Runner;

/// <summary>
/// The validated runner configuration, spec §10 runner config. Everything
/// harness-specific is data: <c>docketd</c> contains no harness knowledge, so
/// supporting a new harness is a config file plus a passing conformance run,
/// never a code change (§10). Parsed from JSON via a source-gen'd context
/// (<see cref="RunnerJsonContext"/>) to stay AOT-clean.
/// </summary>
public sealed record RunnerConfig(MachineConfig Machine, IReadOnlyDictionary<string, ProfileConfig> Profiles)
{
    /// <summary>The required <c>default</c> profile (§10 — exactly one).</summary>
    public ProfileConfig Default => Profiles[MachineSnapshotDefaults.DefaultProfile];

    /// <summary>
    /// Exact-string profile resolution (§7, §10). Absent a request, <c>default</c>.
    /// A requested-but-undeclared profile is not resolvable here — dispatch
    /// against it never reaches the runner because the machine does not declare
    /// the name (§9 check 5).
    /// </summary>
    public ProfileConfig? Resolve(string? requested) =>
        Profiles.TryGetValue(requested ?? MachineSnapshotDefaults.DefaultProfile, out var p) ? p : null;

    /// <summary>The profile names this machine declares, for the heartbeat/snapshot (§7, §10).</summary>
    public IReadOnlySet<string> DeclaredProfiles => new HashSet<string>(Profiles.Keys);

    /// <summary>
    /// Parses and validates a config document. Throws
    /// <see cref="RunnerConfigException"/> listing every problem — a config
    /// with zero or multiple <c>default</c> profiles, an empty spawn argv, a
    /// missing <c>work_root</c>, and so on (§10 validation).
    /// </summary>
    public static RunnerConfig Load(string json)
    {
        if (!TryLoad(json, out var config, out var errors))
            throw new RunnerConfigException(errors);
        return config;
    }

    /// <summary>Non-throwing variant; returns the errors instead.</summary>
    public static bool TryLoad(string json, out RunnerConfig config, out IReadOnlyList<string> errors)
    {
        config = null!;
        var problems = new List<string>();

        RunnerConfigDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, RunnerJsonContext.Default.RunnerConfigDto);
        }
        catch (JsonException e)
        {
            errors = new[] { $"config is not valid JSON: {e.Message}" };
            return false;
        }

        if (dto is null)
        {
            errors = new[] { "config is empty" };
            return false;
        }

        var machine = ValidateMachine(dto.Machine, problems);
        var profiles = ValidateProfiles(dto.Profiles, problems);

        if (problems.Count > 0)
        {
            errors = problems;
            return false;
        }

        config = new RunnerConfig(machine!, profiles!);
        errors = [];
        return true;
    }

    private static MachineConfig? ValidateMachine(MachineDto? dto, List<string> problems)
    {
        if (dto is null)
        {
            problems.Add("machine section is required (§10 runner config)");
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.WorkRoot))
            problems.Add("machine.work_root is required — the per-task scratch root (§10)");

        var heartbeat = dto.HeartbeatSeconds is { } h and > 0
            ? TimeSpan.FromSeconds(h)
            : TimeSpan.FromSeconds(15);

        var bp = dto.BackPressure is { } b
            ? new BackPressureThresholds(
                b.MaxCpuLoad ?? BackPressureThresholds.Default.MaxCpuLoad,
                b.MaxMemoryLoad ?? BackPressureThresholds.Default.MaxMemoryLoad,
                b.MaxDiskUsage ?? BackPressureThresholds.Default.MaxDiskUsage)
            : BackPressureThresholds.Default;

        return string.IsNullOrWhiteSpace(dto.WorkRoot)
            ? null
            : new MachineConfig(dto.WorkRoot!, heartbeat, bp);
    }

    private static IReadOnlyDictionary<string, ProfileConfig>? ValidateProfiles(
        List<ProfileDto>? dtos, List<string> problems)
    {
        if (dtos is null || dtos.Count == 0)
        {
            problems.Add("at least one profile is required, including a 'default' (§10)");
            return null;
        }

        var built = new Dictionary<string, ProfileConfig>(StringComparer.Ordinal);
        var defaults = 0;

        foreach (var dto in dtos)
        {
            var name = dto.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add("every profile needs a name (§10)");
                continue;
            }

            if (name == MachineSnapshotDefaults.DefaultProfile)
                defaults++;

            if (built.ContainsKey(name))
            {
                problems.Add($"duplicate profile name '{name}' (§10 — profile names are identifiers)");
                continue;
            }

            if (dto.Spawn is null || dto.Spawn.Count == 0)
                problems.Add($"profile '{name}' has an empty spawn argv — docketd needs a command to run (§10)");

            if (dto.MaxConcurrent is { } mc && mc < 1)
                problems.Add($"profile '{name}' max_concurrent must be >= 1 when set (§10)");

            // §12 capture knobs, when present, must be sane: a non-positive cap would
            // truncate every transcript to just the marker, and a negative prune window
            // is meaningless (0 is the documented "disable pruning").
            if (dto.Logs?.MaxBytes is { } mb && mb < 1)
                problems.Add($"profile '{name}' logs.max_bytes must be >= 1 when set (§12)");
            if (dto.Logs?.PruneAfterDays is { } pd && pd < 0)
                problems.Add($"profile '{name}' logs.prune_after_days must be >= 0 when set; 0 disables pruning (§12)");

            built[name] = BuildProfile(dto);
        }

        // §10: exactly one profile is required to be 'default'.
        if (defaults == 0)
            problems.Add("no 'default' profile declared — exactly one is required (§10)");
        else if (defaults > 1)
            problems.Add("more than one 'default' profile declared — exactly one is required (§10)");

        return problems.Count == 0 ? built : null;
    }

    private static ProfileConfig BuildProfile(ProfileDto dto)
    {
        var stopMode = ParseEnum(dto.Stop?.Mode, StopMode.Signal);
        var windDown = dto.Stop?.WindDownSeconds is { } w and > 0
            ? TimeSpan.FromSeconds(w)
            : TimeSpan.FromSeconds(30);
        var stop = new StopConfig(stopMode, dto.Stop?.Signal, dto.Stop?.Message, windDown);

        var resume = dto.Resume?.Args is { Count: > 0 } args
            ? new ResumeConfig(args)
            : null;

        var events = new EventsConfig(
            ParseEnum(dto.Events?.Source, EventsSource.None),
            dto.Events?.Mapping ?? new Dictionary<string, string>());

        var telemetry = new TelemetryConfig(
            dto.Telemetry?.Otel ?? false,
            dto.Telemetry?.Endpoint,
            dto.Telemetry?.Env ?? new Dictionary<string, string>());
        var logs = new LogsConfig(
            dto.Logs?.Path,
            dto.Logs?.Format,
            dto.Logs?.Capture ?? false,
            dto.Logs?.MaxBytes ?? TranscriptDefaults.MaxBytes,
            dto.Logs?.PruneAfterDays ?? TranscriptDefaults.PruneAfterDays);

        return new ProfileConfig(
            dto.Name!,
            dto.Spawn ?? [],
            stop,
            resume,
            events,
            telemetry,
            logs,
            dto.MaxConcurrent);
    }

    private static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback) where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var v) ? v : fallback;
}

/// <summary>Shared constant so the runner and control-plane snapshot agree on the default name.</summary>
public static class MachineSnapshotDefaults
{
    public const string DefaultProfile = "default";
}

/// <summary>§10: work_root for per-task scratch dirs; heartbeat cadence; back-pressure thresholds.</summary>
public sealed record MachineConfig(string WorkRoot, TimeSpan HeartbeatInterval, BackPressureThresholds BackPressure);

/// <summary>
/// §10 concurrency: docketd stops accepting dispatch when any of these
/// observed signals crosses its threshold. Ratios in [0, 1].
/// </summary>
public sealed record BackPressureThresholds(double MaxCpuLoad, double MaxMemoryLoad, double MaxDiskUsage)
{
    public static BackPressureThresholds Default { get; } = new(MaxCpuLoad: 0.90, MaxMemoryLoad: 0.90, MaxDiskUsage: 0.95);
}

/// <summary>
/// One named runner configuration (§10). Describes <b>how</b> to run an agent,
/// never <b>what</b> work it does — profiles are identifiers a human chose, not
/// a capability manifest (§10, §15).
///
/// <para><b>Dead-man's switch convention (§10).</b> docketd redirects a worker's
/// stdin to a pipe and holds the write end for the worker's whole lifetime (see
/// <see cref="ProcessSupervisor.Spawn"/>). A well-behaved harness must therefore
/// exit — killing anything it spawned — when it observes EOF on stdin: EOF means
/// docketd is gone (crashed or SIGKILLed), and the worker is burning tokens
/// against a task the control plane has already requeued. This is cooperative and
/// immediate; <see cref="StrayReaper"/> is the non-cooperative backstop that runs
/// on the next docketd start.</para>
/// </summary>
public sealed record ProfileConfig(
    string Name,
    IReadOnlyList<string> Spawn,
    StopConfig Stop,
    ResumeConfig? Resume,
    EventsConfig Events,
    TelemetryConfig Telemetry,
    LogsConfig Logs,
    int? MaxConcurrent);

/// <summary>How <c>stop</c> is delivered for this profile (§10). The frozen
/// vocabulary names the command; the config names the transport.</summary>
public sealed record StopConfig(StopMode Mode, string? Signal, string? MessageTemplate, TimeSpan WindDown);

/// <summary>
/// §10: <c>message</c> injects a turn the agent reads (Claude Code:
/// <c>--input-format stream-json</c>) so it can honour the disposition;
/// <c>signal</c> cannot carry a disposition and is reserved for TTL expiry/kill.
/// </summary>
public enum StopMode { Message, Signal }

/// <summary>
/// §11: how to resume a parked task's transcript (directory-scoped). When a
/// dispatch carries a <see cref="DispatchCommand.ResumeSessionRef"/> and this
/// profile declares <see cref="Args"/>, the supervisor spawns
/// <see cref="Args"/> instead of <see cref="ProfileConfig.Spawn"/>, substituting
/// two placeholders (the same brace syntax as the spawn argv):
/// <list type="bullet">
///   <item><c>{session_id}</c> — the opaque harness session ref to resume.</item>
///   <item><c>{mcp_config}</c> — the path to the generated MCP config; a resumed
///     harness still dials the plane, so it needs it exactly as a cold start does.</item>
/// </list>
/// A claude example:
/// <c>["claude","-p","Resume your task.","--resume","{session_id}",
/// "--mcp-config","{mcp_config}","--strict-mcp-config", ...]</c>. Absent this
/// config a resume ref is ignored and the task cold-starts (documented fallback).
/// </summary>
public sealed record ResumeConfig(IReadOnlyList<string> Args);

/// <summary>§10 event relay: where lifecycle events come from and how names map.</summary>
public sealed record EventsConfig(EventsSource Source, IReadOnlyDictionary<string, string> Mapping);

/// <summary>
/// §10: an honest <c>none</c> is supported — liveness degrades to process-alive
/// and progress renders as "not reported," which beats a fabricated mapping.
/// </summary>
public enum EventsSource { Hooks, Otel, Terminal, None }

/// <summary>
/// §10 telemetry ingest: the per-profile opt-in that sends a harness's own
/// token/cost telemetry to the operator's collector, attributed to the Docket task
/// that caused it (<see cref="HarnessTelemetry"/> resolves the spawn environment;
/// <c>docs/TELEMETRY.md</c> is the operator guide).
///
/// <para><see cref="Otel"/> is off by default and gates everything — it is the
/// operator's data going to the operator's collector. <see cref="Endpoint"/> beats
/// the endpoint docketd itself inherited; with neither, telemetry stays off rather
/// than pointing a worker's exporter at nothing.</para>
///
/// <para><see cref="Env"/> holds harness-specific opt-in variables as data, so
/// docketd's own code stays vendor-neutral (§10 — no harness knowledge). Claude
/// Code needs <c>CLAUDE_CODE_ENABLE_TELEMETRY=1</c> here; the same map is the seam
/// for anything else an operator wants on the worker's exporter (headers, export
/// interval, the trace beta).</para>
///
/// <para><b>Visibility, not enforcement.</b> No ceiling, no accounting, and the
/// control plane ingests none of it (§10 — Docket does not sit between harness and
/// provider, so attribution is best-effort by construction).</para>
/// </summary>
public sealed record TelemetryConfig(
    bool Otel,
    string? Endpoint,
    IReadOnlyDictionary<string, string>? Env = null)
{
    /// <summary>Never null: an absent <c>env</c> block is an empty map.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        Env ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// §12 transcript capture, plus the two original §10 log-streaming hints.
///
/// <para><b>Capture (this increment).</b> When <see cref="Capture"/> is set docketd tees
/// the harness's stdout (its stream-json transcript) and captures stderr to per-instance
/// files under <c>&lt;state&gt;/transcripts/&lt;task&gt;/</c> (see <see cref="TranscriptStore"/>),
/// bounded by <see cref="MaxBytes"/> per stream and swept after <see cref="PruneAfterDays"/>
/// days of no writes. Machine-local only — nothing leaves the box in this increment.
/// Default OFF: an operator opts in per profile.</para>
///
/// <para><b><see cref="Path"/> / <see cref="Format"/>.</b> Originally documented for a
/// plane-side "tail-and-stream" that was never built (a stub). Capture now writes to a
/// fixed state-dir layout, so <see cref="Path"/> is not consulted; <see cref="Format"/>
/// stays an advisory label for the stdout stream's shape (e.g. <c>stream-json</c>).
/// Both are retained so existing configs keep parsing; the plane's serving increment
/// decides how a transcript is exposed.</para>
/// </summary>
public sealed record LogsConfig(
    string? Path,
    string? Format,
    bool Capture = false,
    long MaxBytes = TranscriptDefaults.MaxBytes,
    int PruneAfterDays = TranscriptDefaults.PruneAfterDays);

/// <summary>Thrown by <see cref="RunnerConfig.Load"/> with every validation failure.</summary>
public sealed class RunnerConfigException(IReadOnlyList<string> errors)
    : Exception("invalid docketd config:\n  - " + string.Join("\n  - ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
