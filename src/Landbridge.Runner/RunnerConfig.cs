using System.Text.Json;

namespace Landbridge.Runner;

/// <summary>
/// The validated runner configuration, spec §10 runner config. Everything
/// harness-specific is data: <c>landbridged</c> contains no harness knowledge, so
/// supporting a new harness is a config file, never a code change (§10). (§11
/// wants that config gated by a conformance run before the machine joins; that
/// run is not built, so today the gate is the enroll skill's manual smoke test.)
/// Parsed from JSON via a source-gen'd context
/// (<see cref="RunnerJsonContext"/>) to stay AOT-clean.
/// </summary>
public sealed record RunnerConfig(
    MachineConfig Machine,
    IReadOnlyDictionary<string, ProfileConfig> Profiles)
{

    /// <summary>
    /// Exact-string profile resolution (§7, §10). The name is required; there
    /// is no fallback. A requested-but-undeclared profile is not resolvable
    /// here — dispatch against it never reaches the runner because the machine
    /// does not declare the name (§9 check 5).
    /// </summary>
    public ProfileConfig? Resolve(string requested) =>
        Profiles.TryGetValue(requested, out var p) ? p : null;

    /// <summary>The profile names this machine declares, for the heartbeat/snapshot (§7, §10).</summary>
    public IReadOnlySet<string> DeclaredProfiles => new HashSet<string>(Profiles.Keys);

    /// <summary>
    /// Parses and validates a config document. Throws
    /// <see cref="RunnerConfigException"/> listing every problem — no
    /// profiles, an empty spawn argv, a missing <c>work_root</c>, and so on
    /// (§10 validation).
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
        if (dto.Services is { Count: > 0 })
        {
            problems.Add(
                "services[] is gone — landbridged no longer supervises operator fixtures. " +
                "Session-scoped long work is start_process; something that must survive a " +
                "landbridged restart belongs to systemd or launchd.");
        }

        if (problems.Count > 0)
        {
            errors = problems;
            return false;
        }

        config = new RunnerConfig(machine!, profiles!);
        errors = [];
        return true;
    }

    /// <summary>
    /// <b>A security control, not hygiene.</b> A process name becomes a directory
    /// name under the state dir, so it occupies the slot a <c>SessionId</c> Guid used to
    /// fill — and the Guid is precisely why the transcript path builder could be
    /// called closed. An arbitrary string there would reopen it: <c>..</c>, a
    /// separator, an absolute path, a NUL, or a Windows reserved name would all steer
    /// writes outside the root. This allowlist restores the property the Guid was
    /// silently providing, at the config boundary where it can be reported.
    /// </summary>
    internal static bool IsValidProcessName(string name) =>
        name.Length is > 0 and <= 64
        && name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

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
            problems.Add("at least one profile is required (§10)");
            return null;
        }

        var built = new Dictionary<string, ProfileConfig>(StringComparer.Ordinal);

        foreach (var dto in dtos)
        {
            var name = dto.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add("every profile needs a name (§10)");
                continue;
            }

            if (built.ContainsKey(name))
            {
                problems.Add($"duplicate profile name '{name}' (§10 — profile names are identifiers)");
                continue;
            }

            if (dto.Spawn is null || dto.Spawn.Count == 0)
                problems.Add($"profile '{name}' has an empty spawn argv — landbridged needs a command to run (§10)");

            // §12 capture knobs, when present, must be sane: a non-positive cap would
            // truncate every transcript to just the marker, and a negative prune window
            // is meaningless (0 is the documented "disable pruning").
            if (dto.Logs?.MaxBytes is { } mb && mb < 1)
                problems.Add($"profile '{name}' logs.max_bytes must be >= 1 when set (§12)");
            if (dto.Logs?.PruneAfterDays is { } pd && pd < 0)
                problems.Add($"profile '{name}' logs.prune_after_days must be >= 0 when set; 0 disables pruning (§12)");

            // §10: every profile drives its worker over ACP, and an ACP agent takes no
            // prompt on argv — the client sends it as `session/prompt`. So a profile without
            // one spawns an agent that completes the handshake, waits, and does nothing.
            // Required rather than defaulted: there is no generic text that would be right,
            // since the prompt has to name the landbridge tools the way this harness spells them.
            if (string.IsNullOrWhiteSpace(dto.Prompt))
                problems.Add(
                    $"profile '{name}' has no `prompt` — an ACP agent takes no prompt on argv, so the " +
                    "worker's opening turn has to be declared here and sent as session/prompt (§10)");

            // #112 G3: reserved names fail the load rather than being dropped at spawn.
            // Silently ignoring LANDBRIDGE_WORKER_TOKEN would leave an operator believing
            // they had overwritten a per-instance secret.
            if (dto.Env is { Count: > 0 })
            {
                foreach (var key in dto.Env.Keys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        problems.Add($"profile '{name}' env has an empty key");
                        continue;
                    }

                    if (key.Contains('=', StringComparison.Ordinal) || key.Contains('\0'))
                    {
                        problems.Add(
                            $"profile '{name}' env key '{key}' is not a variable name — keys cannot " +
                            "contain '=' or NUL");
                        continue;
                    }

                    if (HarnessTelemetry.IsReserved(key))
                        problems.Add(
                            $"profile '{name}' env cannot set {key} — landbridged stamps it on every " +
                            "spawn, not configurably (§10)");
                }
            }

            if (dto.Files is { Count: > 0 })
            {
                for (var i = 0; i < dto.Files.Count; i++)
                {
                    var file = dto.Files[i];
                    if (string.IsNullOrWhiteSpace(file.Path))
                        problems.Add($"profile '{name}' files[{i}] has an empty path");
                    if (file.Contents is null)
                        problems.Add($"profile '{name}' files[{i}] is missing contents");
                    if (file.Mode is { } mode && !IsOctalFileMode(mode))
                        problems.Add(
                            $"profile '{name}' files[{i}] mode '{mode}' is not an octal permission " +
                            "(e.g. 0600 or 644)");
                }
            }

            if (dto.Hooks?.BeforeSpawn is { Count: > 0 } before
                && string.IsNullOrWhiteSpace(before[0]))
                problems.Add($"profile '{name}' hooks.before_spawn has an empty argv[0]");
            if (dto.Hooks?.AfterExit is { Count: > 0 } after
                && string.IsNullOrWhiteSpace(after[0]))
                problems.Add($"profile '{name}' hooks.after_exit has an empty argv[0]");

            if (dto.ConfigOptions is { Count: > 0 })
            {
                foreach (var (key, value) in dto.ConfigOptions)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        problems.Add($"profile '{name}' config_options has an empty key");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(value))
                        problems.Add($"profile '{name}' config_options '{key}' has an empty value");
                }
            }

            if (dto.SessionMode is { Length: 0 })
                problems.Add($"profile '{name}' session_mode is empty");

            built[name] = BuildProfile(dto);
        }

        return problems.Count == 0 ? built : null;
    }

    private static ProfileConfig BuildProfile(ProfileDto dto)
    {
        var windDown = dto.Stop?.WindDownSeconds is { } w and > 0
            ? TimeSpan.FromSeconds(w)
            : TimeSpan.FromSeconds(30);
        var stop = new StopConfig(windDown);

        var telemetry = new TelemetryConfig(
            dto.Telemetry?.Otel ?? false,
            dto.Telemetry?.Endpoint,
            dto.Telemetry?.Env ?? new Dictionary<string, string>());
        var logs = new LogsConfig(
            dto.Logs?.Capture ?? false,
            dto.Logs?.MaxBytes ?? TranscriptDefaults.MaxBytes,
            dto.Logs?.PruneAfterDays ?? TranscriptDefaults.PruneAfterDays);

        return new ProfileConfig(
            dto.Name!,
            dto.Spawn ?? [],
            stop,
            telemetry,
            logs,
            dto.Processes is null
                ? null
                : new ProfileProcessesConfig(
                    dto.Processes.AgentInitiated ?? false,
                    dto.Processes.Max is { } cap and > 0 ? cap : 8),
            dto.Env,
            dto.Files?.Select(f => new ProfileFile(f.Path ?? "", f.Contents ?? "", f.Mode)).ToArray(),
            dto.Hooks is null
                ? null
                : new ProfileHooks(dto.Hooks.BeforeSpawn, dto.Hooks.AfterExit),
            dto.Prompt,
            dto.FollowUp,
            dto.AuthMethod,
            dto.ConfigOptions,
            dto.SessionMode);
    }

    internal static bool IsOctalFileMode(string raw)
    {
        var s = raw.Trim();
        if (s.Length is < 3 or > 4)
            return false;
        if (s.Length == 4 && s[0] != '0')
            return false;
        for (var i = s.Length == 4 ? 1 : 0; i < s.Length; i++)
            if (s[i] is < '0' or > '7')
                return false;
        return true;
    }

    private static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback) where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var v) ? v : fallback;
}

/// <summary>§10: work_root for per-task scratch dirs; heartbeat cadence; back-pressure thresholds.</summary>
public sealed record MachineConfig(string WorkRoot, TimeSpan HeartbeatInterval, BackPressureThresholds BackPressure);

/// <summary>
/// §10 concurrency: landbridged stops accepting dispatch when any of these
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
/// <para><b>Dead-man's switch convention (§10).</b> landbridged redirects a worker's stdin to a
/// pipe and holds the write end for the worker's whole lifetime (see
/// <see cref="ProcessSupervisor.Spawn"/>). Under ACP that pipe is also the JSON-RPC request
/// channel, and the two uses coincide exactly: the protocol defines shutdown as the client
/// closing stdin, so EOF means landbridged is gone (crashed or SIGKILLed) and a well-behaved
/// agent exits, killing anything it spawned, rather than burning tokens against a task the
/// control plane has already requeued. This is cooperative and immediate;
/// <see cref="StrayReaper"/> is the non-cooperative backstop that runs on the next landbridged
/// start.
///
/// <para>There is no longer a per-profile opt-out. <c>stdin: closed</c> existed for
/// harnesses that blocked reading a held-open pipe while resolving an argv prompt — a
/// stream-mode problem that cannot arise when stdin carries a protocol rather than a
/// prompt.</para></para>
/// </summary>
public sealed record ProfileConfig(
    string Name,
    IReadOnlyList<string> Spawn,
    StopConfig Stop,
    TelemetryConfig Telemetry,
    LogsConfig Logs,
    ProfileProcessesConfig? Processes = null,
    IReadOnlyDictionary<string, string>? Env = null,
    IReadOnlyList<ProfileFile>? Files = null,
    ProfileHooks? Hooks = null,
    string? Prompt = null,
    string? FollowUp = null,
    string? AuthMethod = null,
    IReadOnlyDictionary<string, string>? ConfigOptions = null,
    string? SessionMode = null)
{
    /// <summary>
    /// §11 / <c>ideas/sessions.md</c>: the turn that wakes this profile's live session when
    /// there is new input on the assignment. Never the input itself — the answer is pulled
    /// over the authenticated MCP call, which is what makes the read a receipt (see
    /// <see cref="Landbridge.Contracts.PromptCommand"/>).
    ///
    /// <para>The default names no tool, because the spelling is per-harness: claude and
    /// Codex see <c>mcp__landbridge__get_session</c>, OpenCode <c>landbridge_get_session</c>, Grok and
    /// Goose <c>landbridge__get_session</c>. A profile should say the right one — a worker that was told
    /// to call a tool it does not have goes hunting, and one that was told nothing specific
    /// has been observed reaching for a shell instead.</para>
    /// </summary>
    public string FollowUpTurn => FollowUp is { Length: > 0 } text
        ? text
        : "There is new input on your assignment. Read your assignment again before continuing.";

    /// <summary>This profile's agent-process policy; the closed default when unstated.</summary>
    public ProfileProcessesConfig ProcessPolicy => Processes ?? new ProfileProcessesConfig();

    /// <summary>
    /// §10 / #112 G3: extra environment stamped on every spawn (and resume) of this
    /// profile, after the reserved <c>LANDBRIDGE_*</c> variables and before
    /// <c>telemetry.env</c>. Values take the same <c>{session_id}</c> / <c>{machine_id}</c>
    /// / <c>{work_dir}</c> / <c>{mcp_config}</c> / <c>{mcp_url}</c> /
    /// <c>{worker_token}</c> / <c>{session_id}</c> substitutions
    /// <see cref="Spawn"/> does. Never null: an absent <c>env</c> block is an empty map.
    /// </summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        Env ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>#112 G2: files written under the work dir before the harness starts.</summary>
    public IReadOnlyList<ProfileFile> Files { get; init; } = Files ?? [];

    /// <summary>Argv hooks. Never null: an absent block is an empty record.</summary>
    public ProfileHooks Hooks { get; init; } = Hooks ?? new ProfileHooks();

    /// <summary>
    /// ACP <c>session/set_config_option</c> pins for this profile. Each key is a
    /// <c>configId</c> the agent advertised; the value must be one of that option's
    /// listed values or the pair is skipped. Never null: an absent block is an
    /// empty map.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConfigOptions { get; init; } =
        ConfigOptions ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// A file <see cref="ProcessSupervisor"/> writes into the task work dir before spawn
/// (#112 G2). <see cref="Path"/> and <see cref="Contents"/> take the same
/// <c>{…}</c> substitutions as <see cref="ProfileConfig.Spawn"/>. After substitution
/// the path must stay under the work dir.
/// </summary>
public sealed record ProfileFile(string Path, string Contents, string? Mode = null);

/// <summary>
/// Argv hooks on a profile, never a shell (§10). <see cref="BeforeSpawn"/> is
/// fail-closed; <see cref="AfterExit"/> is best-effort and skipped for superseded
/// instances.
/// </summary>
public sealed record ProfileHooks(
    IReadOnlyList<string>? BeforeSpawn = null,
    IReadOnlyList<string>? AfterExit = null)
{
    public IReadOnlyList<string> BeforeSpawn { get; init; } = BeforeSpawn ?? [];
    public IReadOnlyList<string> AfterExit { get; init; } = AfterExit ?? [];
}

/// <summary>
/// How a <c>stop</c> is delivered (§10, §11): <c>session/cancel</c> on the live ACP
/// connection, then a deadline.
///
/// <para><b>There is no mode and no message template.</b> Both existed to describe how a
/// stream-mode harness might be told to wind down — <c>message</c> wrote a free-text turn
/// to the worker's stdin, <c>signal</c> admitted the harness would not read one — and
/// neither survives the protocol. ACP's transport forbids writing anything to the agent's
/// stdin that is not an ACP message, so a free-text turn is a protocol violation rather
/// than merely an unread one, and <c>session/cancel</c> replaces it with something the
/// agent is <em>specified</em> to honour: it stops its model requests and tool calls and
/// ends the turn with a <c>cancelled</c> stop reason.</para>
///
/// <para><see cref="WindDown"/> is therefore what it always claimed to be and previously
/// was not: the window an agent gets to wind down cooperatively before the portable
/// tree-kill backstops it. Under stream mode that window sat behind a turn most harnesses
/// never read.</para>
/// </summary>
public sealed record StopConfig(TimeSpan WindDown);

/// <summary>
/// §10 telemetry ingest: the per-profile opt-in that sends a harness's own
/// token/cost telemetry to the operator's collector, attributed to the Landbridge task
/// that caused it (<see cref="HarnessTelemetry"/> resolves the spawn environment;
/// <c>docs/TELEMETRY.md</c> is the operator guide).
///
/// <para><see cref="Otel"/> is off by default and gates everything — it is the
/// operator's data going to the operator's collector. <see cref="Endpoint"/> beats
/// the endpoint landbridged itself inherited; with neither, telemetry stays off rather
/// than pointing a worker's exporter at nothing.</para>
///
/// <para><see cref="Env"/> holds harness-specific opt-in variables as data, so
/// landbridged's own code stays vendor-neutral (§10 — no harness knowledge). Claude
/// Code needs <c>CLAUDE_CODE_ENABLE_TELEMETRY=1</c> here; the same map is the seam
/// for anything else an operator wants on the worker's exporter (headers, export
/// interval, the trace beta).</para>
///
/// <para><b>Visibility, not enforcement.</b> No ceiling, no accounting, and the
/// control plane ingests none of it (§10 — Landbridge does not sit between harness and
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
/// <para><b>Capture (this increment).</b> When <see cref="Capture"/> is set landbridged tees
/// the harness's stdout (its stream-json transcript) and captures stderr to per-instance
/// files under <c>&lt;state&gt;/transcripts/&lt;task&gt;/</c> (see <see cref="TranscriptStore"/>),
/// bounded by <see cref="MaxBytes"/> per stream and swept after <see cref="PruneAfterDays"/>
/// days of no writes. Machine-local only — nothing leaves the box in this increment.
/// Default OFF: an operator opts in per profile.</para>
///
/// <para><b>No <c>path</c> or <c>format</c>.</b> Both were documented for a plane-side
/// "tail-and-stream" that was never built, and both were carried here — parsed, stored,
/// and read by nothing — after capture settled on a fixed state-dir layout
/// (<see cref="TranscriptStore"/>) that <c>path</c> cannot influence and a shape the
/// reader derives from the profile's <c>events</c> rather than from a <c>format</c>
/// label. They are gone rather than retained as advisory: a config field that looks like
/// a knob and moves nothing is the failure mode this area keeps having. Existing configs
/// keep parsing — unknown keys are ignored — so a declared <c>format</c> is inert exactly
/// as it already was.</para>
/// </summary>
public sealed record LogsConfig(
    bool Capture = false,
    long MaxBytes = TranscriptDefaults.MaxBytes,
    int PruneAfterDays = TranscriptDefaults.PruneAfterDays);

/// <summary>
/// §10 per-profile policy for agent-started processes. <b>Off by default</b>: a worker
/// starting a background process is a machine capability the operator grants, not a
/// default. Deliberately gated by profile rather than by an allowlist of permitted
/// commands — a worker on an open profile can already run a dev server by hand, so
/// restricting the sanctioned tool below its existing capability would only push agents
/// back to the <c>setsid</c>/env-scrubbing route the worker skill forbids. A strict
/// profile with no shell cannot start a service either way, and refuses honestly.
/// </summary>
/// <param name="Max">
/// Resource bound, not an authority control: the gate answers <em>may this task start
/// processes</em>, this answers <em>how many</em>. Back-pressure cannot gate a looping
/// <c>start_process</c> the way it gates dispatch, so an agent needs a ceiling.
/// </param>
public sealed record ProfileProcessesConfig(bool AgentInitiated = false, int Max = 8);

/// <summary>Defaults for §10 process supervision.</summary>
public static class ProcessDefaults
{
    /// <summary>
    /// How long <c>stop_process</c> waits after closing stdin before taking the tree — the same
    /// graceful-then-kill shape a message-mode worker stop uses (§10/§11). Long enough for a
    /// build to flush, short enough that a wedged process cannot stall a cleanup task.
    /// </summary>
    public static readonly TimeSpan StopWindDown = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Runtime record for an agent-started process (§10 <c>start_process</c>):
/// landbridged's own child, never restarted, machine-scoped.
/// See <see cref="AgentProcessSupervisor"/>.
/// </summary>
public sealed record AgentProcessConfig(
    string Name,
    IReadOnlyList<string> Spawn,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Env,
    LogsConfig Logs);

/// <summary>Thrown by <see cref="RunnerConfig.Load"/> with every validation failure.</summary>
public sealed class RunnerConfigException(IReadOnlyList<string> errors)
    : Exception("invalid landbridged config:\n  - " + string.Join("\n  - ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
