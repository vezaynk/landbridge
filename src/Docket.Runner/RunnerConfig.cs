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

        var telemetry = new TelemetryConfig(dto.Telemetry?.Otel ?? false, dto.Telemetry?.Endpoint);
        var logs = new LogsConfig(dto.Logs?.Path, dto.Logs?.Format);

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

/// <summary>§11: how to resume a parked task's transcript (directory-scoped).</summary>
public sealed record ResumeConfig(IReadOnlyList<string> Args);

/// <summary>§10 event relay: where lifecycle events come from and how names map.</summary>
public sealed record EventsConfig(EventsSource Source, IReadOnlyDictionary<string, string> Mapping);

/// <summary>
/// §10: an honest <c>none</c> is supported — liveness degrades to process-alive
/// and progress renders as "not reported," which beats a fabricated mapping.
/// </summary>
public enum EventsSource { Hooks, Otel, Terminal, None }

/// <summary>§10 telemetry ingest: OTel toggle + endpoint for budget attribution.</summary>
public sealed record TelemetryConfig(bool Otel, string? Endpoint);

/// <summary>§10 log streaming: transcript path and format for tail-and-stream.</summary>
public sealed record LogsConfig(string? Path, string? Format);

/// <summary>Thrown by <see cref="RunnerConfig.Load"/> with every validation failure.</summary>
public sealed class RunnerConfigException(IReadOnlyList<string> errors)
    : Exception("invalid docketd config:\n  - " + string.Join("\n  - ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
