using System.Text.Json;

namespace Docket.Runner;

/// <summary>
/// The validated runner configuration, spec §10 runner config. Everything
/// harness-specific is data: <c>docketd</c> contains no harness knowledge, so
/// supporting a new harness is a config file, never a code change (§10). (§11
/// wants that config gated by a conformance run before the machine joins; that
/// run is not built, so today the gate is the enroll skill's manual smoke test.)
/// Parsed from JSON via a source-gen'd context
/// (<see cref="RunnerJsonContext"/>) to stay AOT-clean.
/// </summary>
public sealed record RunnerConfig(
    MachineConfig Machine,
    IReadOnlyDictionary<string, ProfileConfig> Profiles,
    IReadOnlyList<ServiceConfig>? Services = null)
{
    /// <summary>§10 operator-declared services; empty when the section is absent.</summary>
    public IReadOnlyList<ServiceConfig> DeclaredServices => Services ?? [];

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
    /// §10 event relay: warnings for profiles that will not produce a usable
    /// per-task liveness signal. <see cref="EventsSource.Terminal"/> is the only
    /// implemented source (the stdout drain in <see cref="ProcessSupervisor"/>);
    /// <see cref="EventsSource.Hooks"/> and <see cref="EventsSource.Otel"/> parse
    /// but are consumed nowhere, so they behave exactly as
    /// <see cref="EventsSource.None"/>.
    ///
    /// <para>What this costs, stated accurately: such a profile has <b>no progress
    /// signal</b>. It still emits <c>started</c>, <c>exited</c>, and the periodic
    /// <c>alive</c> — <c>EmitAliveEvents</c> is not gated on the events source — so the
    /// short aliveness clock is satisfied and a task is not requeued merely for being
    /// quiet. What it loses is <c>tool-call</c>, and therefore the progress clock: the
    /// §10 no-progress ceiling (30 minutes by default) is the only thing governing such a
    /// task, so work that legitimately runs longer than that without a tool call is
    /// requeued, and a wedged agent cannot be told from a busy one in the meantime.
    /// Worth a loud startup line, but not the catastrophe an earlier version of this
    /// comment described — that text predated the <c>alive</c> emitter this repo now has
    /// (see <c>DefaultNoProgressCeiling</c> and the process-alive half in §10).</para>
    ///
    /// <para>Returned rather than logged so it is testable; the daemon prints these
    /// alongside the <c>max_cpu_load is inert</c> notice.</para>
    /// </summary>
    public IReadOnlyList<string> EventRelayWarnings()
    {
        var blind = Profiles
            .Where(p => p.Value.Events.Source != EventsSource.Terminal)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToArray();

        if (blind.Length == 0)
            return [];

        var warnings = new List<string>(blind.Length + 1);
        foreach (var (name, profile) in blind)
        {
            var declared = profile.Events.Source.ToString().ToLowerInvariant();
            var unimplemented = profile.Events.Source is EventsSource.Hooks or EventsSource.Otel
                ? $"events.source '{declared}' is not implemented and behaves as 'none'"
                : "events.source is 'none'";
            warnings.Add(
                $"docketd: profile '{name}': {unimplemented} — no tool-call events, so this " +
                "profile has no progress signal: a hung agent cannot be told from a busy one.");
        }

        warnings.Add(
            "docketd: the periodic alive event still satisfies the short liveness clock, so such a " +
            "task is not requeued merely for being quiet — but with no progress signal the " +
            "no-progress ceiling (30 minutes by default) is the only clock governing it, so work " +
            "that legitimately runs longer than that without a tool call is requeued. Use " +
            "events.source 'terminal' if the harness can stream structured output on stdout (§10).");
        return warnings;
    }

    /// <summary>
    /// §10 dead-man's switch: warnings for profiles that declared
    /// <see cref="StdinPolicy.Closed"/> and have therefore given it up. Not a
    /// misconfiguration — it is the documented trade a harness like <c>codex exec</c>
    /// requires (it blocks reading a held-open stdin and never reaches its first turn) —
    /// but it is a real reduction in containment, so the machine says it out loud at
    /// startup rather than letting an operator believe the cooperative kill is still
    /// there.
    ///
    /// <para>What is actually lost: docketd's own death no longer takes such a worker
    /// down. Nothing else changes — <see cref="StrayReaper"/> still sweeps it on the next
    /// docketd start, Windows Job Objects still contain it, and Linux PDEATHSIG still
    /// fires if the harness arms it. So the window is "docketd is dead and has not
    /// restarted yet", during which a worker keeps burning tokens on a task the plane has
    /// already requeued.</para>
    ///
    /// <para>Returned rather than logged so it is testable; the daemon prints these
    /// alongside <see cref="EventRelayWarnings"/> and the <c>max_cpu_load is inert</c>
    /// notice.</para>
    /// </summary>
    public IReadOnlyList<string> StdinPolicyWarnings() =>
        Profiles
            .Where(p => p.Value.Stdin == StdinPolicy.Closed)
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p =>
                $"docketd: profile '{p.Key}': stdin is 'closed', so the worker sees EOF at once and " +
                "this profile has NO dead-man's switch — if docketd dies, its workers keep running " +
                "on already-requeued tasks until the next docketd start sweeps them. That is the " +
                "declared trade for a harness that blocks reading a held-open stdin (§10).")
            .ToArray();

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
        var services = ValidateServices(dto.Services, problems);

        if (problems.Count > 0)
        {
            errors = problems;
            return false;
        }

        config = new RunnerConfig(machine!, profiles!, services);
        errors = [];
        return true;
    }

    /// <summary>
    /// §10 operator-declared services. Absent or empty is the normal case, so a
    /// missing section is not a problem — but a declared one is validated strictly,
    /// because a service's name becomes a filesystem path segment (see
    /// <see cref="IsValidServiceName"/>) and its backend decides whether docketd owns
    /// the process at all.
    /// </summary>
    private static IReadOnlyList<ServiceConfig> ValidateServices(
        List<ServiceDto>? dtos, List<string> problems)
    {
        if (dtos is null || dtos.Count == 0)
            return [];

        var built = new List<ServiceConfig>(dtos.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // §8.2 refuse-at-dial resolves a dial target to a service by port, so two
        // services claiming one port would make that lookup answer for whichever it
        // happened to find first — and a dial refused on that basis is unexplainable
        // from the outside. Rejected here, where both names can be named.
        var portOwners = new Dictionary<int, string>();

        foreach (var dto in dtos)
        {
            var name = dto.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                problems.Add("a service is missing `name` (§10 services)");
                continue;
            }

            if (!IsValidServiceName(name))
            {
                problems.Add(
                    $"service name '{name}' is invalid: use 1-64 characters of a-z, A-Z, 0-9, " +
                    "'-' or '_' (§10 — the name becomes a directory name under the state dir, " +
                    "so it must not be able to steer a path)");
                continue;
            }

            if (!seen.Add(name))
            {
                problems.Add($"duplicate service name '{name}' (§10 — service names are identifiers)");
                continue;
            }

            if (dto.Spawn is null || dto.Spawn.Count == 0)
            {
                problems.Add($"service '{name}' has an empty spawn argv — docketd needs a command to run (§10)");
                continue;
            }

            // v1 supervises services as docketd's own children. The key exists so a
            // config asking for a delegated backend fails loudly instead of being
            // silently ignored and quietly supervised the other way.
            var backend = dto.Backend?.Trim();
            if (!string.IsNullOrEmpty(backend) && !string.Equals(backend, ServiceBackends.Direct, StringComparison.Ordinal))
            {
                problems.Add(
                    $"service '{name}' declares backend '{backend}', which is not implemented — " +
                    $"'{ServiceBackends.Direct}' (docketd supervises the process itself) is the only " +
                    "supported value (§10)");
                continue;
            }

            if (dto.Port is { } p && p is < 1 or > 65535)
            {
                problems.Add($"service '{name}' port must be in 1..65535 when set (§10)");
                continue;
            }

            // The effective port is the one a forward can dial and the one
            // ServiceSupervisor.IsServiceOnPort resolves — `port` when declared, else the
            // readiness port. A separate readiness-only port is not part of this rule
            // because nothing dials it.
            var effectivePort = dto.Port ?? dto.Readiness?.TcpPort;
            if (effectivePort is { } ep && !portOwners.TryAdd(ep, name))
            {
                problems.Add(
                    $"services '{portOwners[ep]}' and '{name}' both claim port {ep} — declared " +
                    "ports must be unique on a machine (§10), because a forward dial is " +
                    "resolved to a service by port");
                continue;
            }

            if (dto.Readiness?.TcpPort is { } rp && rp is < 1 or > 65535)
            {
                problems.Add($"service '{name}' readiness.tcp_port must be in 1..65535 when set (§10)");
                continue;
            }

            if (dto.Logs?.MaxBytes is { } mb && mb < 1)
            {
                problems.Add($"service '{name}' logs.max_bytes must be >= 1 when set (§12)");
                continue;
            }

            built.Add(BuildService(dto, name));
        }

        return built;
    }

    private static ServiceConfig BuildService(ServiceDto dto, string name)
    {
        var readiness = dto.Readiness?.TcpPort is { } tcpPort
            ? new ReadinessConfig(
                tcpPort,
                dto.Readiness.TimeoutSeconds is { } t and > 0
                    ? TimeSpan.FromSeconds(t)
                    : ServiceDefaults.ReadinessTimeout)
            : null;

        var maxBackoff = dto.Restart?.MaxBackoffSeconds is { } b and > 0
            ? TimeSpan.FromSeconds(b)
            : ServiceDefaults.MaxBackoff;

        return new ServiceConfig(
            name,
            dto.Spawn!,
            dto.WorkingDirectory,
            dto.Env ?? new Dictionary<string, string>(StringComparer.Ordinal),
            dto.Port,
            readiness,
            maxBackoff,
            new LogsConfig(
                dto.Logs?.Capture ?? false,
                dto.Logs?.MaxBytes ?? TranscriptDefaults.MaxBytes,
                dto.Logs?.PruneAfterDays ?? TranscriptDefaults.PruneAfterDays),
            dto.Enabled ?? true);
    }

    /// <summary>
    /// <b>A security control, not hygiene.</b> A service name becomes a directory
    /// name under the state dir, so it occupies the slot a <c>TaskId</c> Guid used to
    /// fill — and the Guid is precisely why the transcript path builder could be
    /// called closed. An arbitrary string there would reopen it: <c>..</c>, a
    /// separator, an absolute path, a NUL, or a Windows reserved name would all steer
    /// writes outside the root. This allowlist restores the property the Guid was
    /// silently providing, at the config boundary where it can be reported.
    /// </summary>
    internal static bool IsValidServiceName(string name) =>
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

            // §10 events seam: an events.mapping that cannot do anything is the failure mode
            // this whole area is trying to stamp out, so a half-declared or unwalkable flat
            // tool-call mapping fails the load instead of going quietly inert.
            foreach (var problem in EventsMappingProblems(dto.Events?.Mapping))
                problems.Add($"profile '{name}' {problem}");

            // §10 stdin policy. Parsed STRICTLY, unlike stop.mode and events.source: a
            // typo in those degrades to a documented default, but a typo here silently
            // restores the very pipe the profile was written to escape — and for a harness
            // that blocks reading it (codex exec) that is a worker which hangs forever
            // before its first turn, with nothing said anywhere. Name the two values
            // instead of guessing.
            if (!TryParseStdin(dto.Stdin, out var stdin))
            {
                problems.Add(
                    $"profile '{name}' declares stdin '{dto.Stdin}', which is not a policy — use " +
                    "'deadman' (hold the pipe open for the worker's life; the §10 dead-man's switch, " +
                    "and the default) or 'closed' (EOF immediately after spawn, for a harness that " +
                    "blocks reading piped stdin)");
            }
            // A message-mode stop writes its wind-down turn to the worker's stdin, so
            // declaring it on a closed-stdin profile asks docketd to deliver a turn to a
            // pipe it closed at spawn. Refused here rather than degrading at stop time: the
            // stop path would silently fall through to the deadline kill, which is exactly
            // the "declared one thing, did another" failure §10's stop honesty rules out.
            else if (stdin == StdinPolicy.Closed && ParseEnum(dto.Stop?.Mode, StopMode.Signal) == StopMode.Message)
            {
                problems.Add(
                    $"profile '{name}' declares stop.mode 'message' with stdin 'closed' — the " +
                    "wind-down turn is written to the worker's stdin, and a closed stdin gives it " +
                    "nowhere to land. Declare stop.mode 'signal' (the honest choice for a harness " +
                    "that does not read stdin turns, and what a closed-stdin harness is by " +
                    "definition) or stdin 'deadman' (§10)");
            }

            // #112 G3: reserved names fail the load rather than being dropped at spawn.
            // Silently ignoring DOCKET_WORKER_TOKEN would leave an operator believing
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
                            $"profile '{name}' env cannot set {key} — docketd stamps it on every " +
                            "spawn, not configurably (§10)");
                }
            }

            built[name] = BuildProfile(dto);
        }

        // §10: exactly one profile is required to be 'default'.
        if (defaults == 0)
            problems.Add("no 'default' profile declared — exactly one is required (§10)");
        else if (defaults > 1)
            problems.Add("more than one 'default' profile declared — exactly one is required (§10)");

        return problems.Count == 0 ? built : null;
    }

    /// <summary>
    /// §10 validation for the flat tool-call mapping mode (issue #111). The reader ignores
    /// unknown mapping keys by design — the seam is a bag of optional overrides — but these
    /// three mistakes are worth failing the load for, because every one of them produces a
    /// profile that looks configured and emits nothing:
    /// <list type="number">
    ///   <item><c>tool_event_type</c> without <c>tool_name_path</c> or the reverse: neither key
    ///     does anything alone.</item>
    ///   <item>a <c>tool_name_path</c> that is not a walkable dotted path of property names —
    ///     an empty segment (<c>item..tool</c>), or a padded one (<c>item . tool</c>), which
    ///     would look up a property whose name has a space in it.</item>
    ///   <item>a <c>tool_event_type</c> equal to the effective <c>system_type</c> or
    ///     <c>assistant_type</c>: one line cannot be both the session-init line and a tool
    ///     call, and the reader's dispatch is first-match, so the collision would silently
    ///     shadow one of them.</item>
    /// </list>
    /// Wildcards and array indexes are not rejected specially: they are simply property names
    /// that no harness emits, so they resolve to nothing and the silent-stream warning reports
    /// it against the real stream, which is more honest than pretending to validate a syntax
    /// this resolver does not have.
    ///
    /// <para>Two more, both surfaced by the OpenCode integration (#142), both the same class of
    /// mistake — a profile that parses and reports nothing:
    /// <list type="number">
    ///   <item>an unwalkable <b>usage</b> path. The usage keys became dotted paths so a harness
    ///     that nests its counters is reachable, which means they can now be malformed the same
    ///     way <c>tool_name_path</c> can. Checked here for the same reason.</item>
    ///   <item><c>usage_type</c> equal to the effective <c>system_type</c>. This one is a real
    ///     trap rather than a typo: the reader <c>return</c>s early on a session-init line, so a
    ///     usage report riding that same type is never read, and the symptom is a profile with a
    ///     perfect resume ref and permanently empty usage. It bites hardest on a harness whose
    ///     stream has few distinct types to choose from — OpenCode has six — where reaching for
    ///     the same one twice is the natural mistake.</item>
    /// </list></para>
    /// </summary>
    private static IReadOnlyList<string> EventsMappingProblems(IReadOnlyDictionary<string, string>? mapping)
    {
        if (mapping is null || mapping.Count == 0)
            return [];

        var problems = new List<string>();
        var hasEventType = mapping.TryGetValue("tool_event_type", out var toolEventType)
            && !string.IsNullOrWhiteSpace(toolEventType);
        var hasNamePath = mapping.TryGetValue("tool_name_path", out var toolNamePath)
            && !string.IsNullOrWhiteSpace(toolNamePath);

        if (hasEventType != hasNamePath)
        {
            var declared = hasEventType ? "tool_event_type" : "tool_name_path";
            var missing = hasEventType ? "tool_name_path" : "tool_event_type";
            problems.Add(
                $"declares events.mapping {declared} without {missing} — the flat tool-call mode needs both keys, " +
                $"and {declared} alone emits no tool-call at all (§10)");
        }

        if (hasNamePath)
        {
            foreach (var raw in toolNamePath!.Split(','))
            {
                var alternative = raw.Trim();
                if (alternative.Length == 0)
                {
                    problems.Add(
                        $"has an empty alternative in events.mapping tool_name_path '{toolNamePath}' — each " +
                        "comma-separated entry must be a dotted property path (§10)");
                    continue;
                }

                if (Array.Exists(alternative.Split('.'), s => s.Length == 0 || s.Trim() != s))
                    problems.Add(
                        $"has an unwalkable events.mapping tool_name_path '{alternative}' — segments are object " +
                        "property names separated by '.', each non-empty and unpadded; there are no wildcards or " +
                        "array indexes (§10)");
            }
        }

        if (hasEventType)
        {
            var collision = toolEventType == TerminalStreamMapping.Pick(mapping, "system_type", "system")
                ? "system_type"
                : toolEventType == TerminalStreamMapping.Pick(mapping, "assistant_type", "assistant")
                    ? "assistant_type"
                    : null;
            if (collision is not null)
                problems.Add(
                    $"sets events.mapping tool_event_type '{toolEventType}', which is also the effective " +
                    $"{collision} — one stream line cannot be both, so the flat tool-call mode needs its own " +
                    "type value (§10)");
        }

        // The usage keys are dotted paths (#142), so they can be malformed exactly as
        // tool_name_path can. Only declared keys are checked: every default is a valid
        // one-segment path, so an absent key cannot be at fault.
        foreach (var key in UsagePathKeys)
        {
            if (!mapping.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                continue;
            if (Array.Exists(raw.Split('.'), s => s.Length == 0 || s.Trim() != s))
                problems.Add(
                    $"has an unwalkable events.mapping {key} '{raw}' — segments are object property names " +
                    "separated by '.', each non-empty and unpadded (§10)");
        }

        var usageType = TerminalStreamMapping.Pick(mapping, "usage_type", "result");
        if (usageType == TerminalStreamMapping.Pick(mapping, "system_type", "system"))
            problems.Add(
                $"sets events.mapping usage_type '{usageType}', which is also the effective system_type — the " +
                "reader stops at a session-init line, so usage on that same type is never read and the profile " +
                "reports none. Point one of the two at a different stream type (§10)");

        return problems;
    }

    /// <summary>
    /// The <c>events.mapping</c> keys whose values are dotted paths rather than plain strings
    /// (#142). Listed once here so validation cannot drift from
    /// <see cref="TerminalStreamMapping.From"/>; <c>usage_type</c> is deliberately absent because
    /// it is a stream <em>value</em>, and so is <c>tool_name_path</c>, which is checked above with
    /// its own comma-alternative rule.
    /// </summary>
    private static readonly string[] UsagePathKeys =
    [
        "usage_key", "usage_input_key", "usage_output_key", "usage_cache_read_key",
        "usage_cache_write_key", "usage_reasoning_key", "usage_cost_key", "usage_models_key",
        "model_input_key", "model_output_key", "model_cache_read_key", "model_cache_write_key",
        "model_cost_key",
    ];

    private static ProfileConfig BuildProfile(ProfileDto dto)
    {
        // Validation refused every value this cannot parse, so the out is the declared
        // policy or — for an absent key — the Deadman default it initialises to.
        TryParseStdin(dto.Stdin, out var stdin);

        var stopMode = ParseEnum(dto.Stop?.Mode, StopMode.Signal);
        var windDown = dto.Stop?.WindDownSeconds is { } w and > 0
            ? TimeSpan.FromSeconds(w)
            : TimeSpan.FromSeconds(30);
        var stop = new StopConfig(stopMode, dto.Stop?.Message, windDown);

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
            dto.MaxConcurrent,
            dto.Processes is null
                ? null
                : new ProfileProcessesConfig(
                    dto.Processes.AgentInitiated ?? false,
                    dto.Processes.Max is { } cap and > 0 ? cap : 8),
            stdin,
            dto.Env);
    }

    /// <summary>
    /// The two <c>stdin</c> spellings, matched by name only. Deliberately not
    /// <see cref="Enum.TryParse{TEnum}(string,bool,out TEnum)"/>, which also accepts the
    /// underlying numbers — <c>"stdin": "0"</c> quietly meaning <c>deadman</c> is not a
    /// config surface worth having.
    /// </summary>
    private static bool TryParseStdin(string? raw, out StdinPolicy policy)
    {
        policy = StdinPolicy.Deadman;
        if (string.IsNullOrWhiteSpace(raw))
            return true; // absent: the pre-existing behaviour, and the default
        switch (raw.Trim().ToLowerInvariant())
        {
            case "deadman": policy = StdinPolicy.Deadman; return true;
            case "closed": policy = StdinPolicy.Closed; return true;
            default: return false;
        }
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
/// <para><b>Dead-man's switch convention (§10), and the per-profile opt-out.</b> Under
/// the default <see cref="StdinPolicy.Deadman"/>, docketd redirects a worker's stdin to
/// a pipe and holds the write end for the worker's whole lifetime (see
/// <see cref="ProcessSupervisor.Spawn"/>). A well-behaved harness must therefore
/// exit — killing anything it spawned — when it observes EOF on stdin: EOF means
/// docketd is gone (crashed or SIGKILLed), and the worker is burning tokens
/// against a task the control plane has already requeued. This is cooperative and
/// immediate; <see cref="StrayReaper"/> is the non-cooperative backstop that runs
/// on the next docketd start. <see cref="StdinPolicy.Closed"/> keeps only the
/// backstop — see <see cref="Stdin"/>.</para>
/// </summary>
/// <param name="Stdin">
/// §10: what the worker's stdin is for this profile. The default
/// <see cref="StdinPolicy.Deadman"/> is the switch above.
/// <see cref="StdinPolicy.Closed"/> exists because the switch is not universally
/// survivable: a harness that blocks reading piped stdin never reaches its first turn
/// under it (<c>codex exec</c> reads stdin to EOF while resolving its prompt, even with
/// an argv prompt, and no flag escapes it — issue #110). Such a profile trades the
/// cooperative kill for the <see cref="StrayReaper"/>'s restart-time sweep alone, and
/// says so on docketd's startup line (<see cref="RunnerConfig.StdinPolicyWarnings"/>).
/// </param>
public sealed record ProfileConfig(
    string Name,
    IReadOnlyList<string> Spawn,
    StopConfig Stop,
    ResumeConfig? Resume,
    EventsConfig Events,
    TelemetryConfig Telemetry,
    LogsConfig Logs,
    int? MaxConcurrent,
    ProfileProcessesConfig? Processes = null,
    StdinPolicy Stdin = StdinPolicy.Deadman,
    IReadOnlyDictionary<string, string>? Env = null)
{
    /// <summary>This profile's agent-process policy; the closed default when unstated.</summary>
    public ProfileProcessesConfig ProcessPolicy => Processes ?? new ProfileProcessesConfig();

    /// <summary>
    /// §10 / #112 G3: extra environment stamped on every spawn (and resume) of this
    /// profile, after the reserved <c>DOCKET_*</c> variables and before
    /// <c>telemetry.env</c>. Values take the same <c>{task_id}</c> / <c>{machine_id}</c>
    /// / <c>{work_dir}</c> / <c>{mcp_config}</c> / <c>{session_id}</c> substitutions
    /// <see cref="Spawn"/> does. Never null: an absent <c>env</c> block is an empty map.
    /// </summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } =
        Env ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// §10 <c>profiles[].stdin</c>: whether the dead-man's pipe is held open for this
/// profile's workers. A <b>per-profile</b> decision rather than a machine-wide one
/// because a fleet is heterogeneous by design — one machine can run a claude profile
/// that survives the pipe and a codex profile that cannot, and only the profile knows
/// which harness it spawns.
/// </summary>
public enum StdinPolicy
{
    /// <summary>Hold the write end open for the worker's whole life: EOF means docketd
    /// died, and a cooperating harness kills its own tree (§10). The default, and the
    /// only behaviour before <c>stdin</c> existed.</summary>
    Deadman,

    /// <summary>Close the write end immediately after spawn, so the worker's first read
    /// is a deterministic EOF. For a harness that blocks on piped stdin — the same choice
    /// an agent-started process makes with <c>open_stdin: false</c> (#89). Gives up the
    /// cooperative kill; keeps the restart-time sweep.</summary>
    Closed,
}

/// <summary>
/// How <c>stop</c> is delivered for this profile (§10). The frozen vocabulary names the
/// command; the config names the transport.
///
/// <para>There is deliberately no signal <em>name</em> here. A <c>stop</c> is never
/// delivered as a signal that carries meaning — a signal cannot carry the disposition, so
/// signals are reserved for the deadline's own kill, and that kill is always the portable
/// tree-kill (<see cref="ProcessSupervisor.StopAsync"/>). A <c>stop.signal</c> key was
/// parsed and stored here for a while and never read by anything; it is gone rather than
/// left to imply a choice the runner does not offer. An existing config naming one still
/// loads — unknown keys are ignored — it simply never meant anything.</para>
/// </summary>
public sealed record StopConfig(StopMode Mode, string? MessageTemplate, TimeSpan WindDown);

/// <summary>
/// §10: <c>message</c> injects a turn the agent reads (Claude Code:
/// <c>--input-format stream-json</c>) so it can honour the disposition;
/// <c>signal</c> cannot carry a disposition and is reserved for TTL expiry/kill.
/// </summary>
public enum StopMode { Message, Signal }

/// <summary>
/// §11: how to resume a parked task's transcript (directory-scoped). When a
/// dispatch carries a <see cref="Docket.Contracts.DispatchCommand.ResumeSessionRef"/> and this
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
/// processes</em>, this answers <em>how many</em>. Services are already-running load that
/// back-pressure cannot gate the way it gates dispatch, so an agent looping on
/// <c>start_process</c> needs a ceiling.
/// </param>
public sealed record ProfileProcessesConfig(bool AgentInitiated = false, int Max = 8);

/// <summary>Accepted <c>services[].backend</c> values (§10).</summary>
public static class ServiceBackends
{
    /// <summary>docketd spawns and supervises the process itself — the only v1 backend.</summary>
    public const string Direct = "direct";
}

/// <summary>Defaults for §10 service supervision.</summary>
public static class ServiceDefaults
{
    /// <summary>How long a readiness port may take to answer before the start is a failure.</summary>
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Ceiling on the exponential restart backoff.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>First restart delay; doubles up to <see cref="MaxBackoff"/>.</summary>
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long <c>stop_process</c> waits after closing stdin before taking the tree — the same
    /// graceful-then-kill shape a message-mode worker stop uses (§10/§11). Long enough for a
    /// build to flush, short enough that a wedged process cannot stall a cleanup task.
    /// </summary>
    public static readonly TimeSpan StopWindDown = TimeSpan.FromSeconds(10);
}

/// <summary>
/// One operator-declared service (§10): a long-lived process <c>docketd</c> supervises
/// as its <b>own child</b>, outside any task's process tree. That placement is the
/// whole point — it is why the service survives the task that uses it without anyone
/// having to <c>setsid</c> or scrub <c>DOCKET_*</c> to escape supervision, and it keeps
/// the kill guarantee inside Docket on every OS rather than depending on a system
/// service manager that macOS and containers may not have.
///
/// <para>This record is also reused for an agent-started <b>process</b> (§10
/// <c>start_process</c>), which differs in policy rather than in shape: never restarted,
/// declared over the wire, and machine-scoped. See <see cref="ServiceSupervisor"/>.</para>
/// </summary>
public sealed record ServiceConfig(
    string Name,
    IReadOnlyList<string> Spawn,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Env,
    int? Port,
    ReadinessConfig? Readiness,
    TimeSpan MaxBackoff,
    LogsConfig Logs,
    bool Enabled = true);

/// <summary>
/// §10 readiness: the loopback port that must accept a connection before the service
/// counts as <see cref="Docket.Contracts.ServiceState.Running"/>. A real check, not a
/// sleep — it is what lets a holder task register only once the port actually answers
/// (§8.2), and what lets docketd refuse a forward dial for a service that is down.
/// </summary>
public sealed record ReadinessConfig(int TcpPort, TimeSpan Timeout);

/// <summary>Thrown by <see cref="RunnerConfig.Load"/> with every validation failure.</summary>
public sealed class RunnerConfigException(IReadOnlyList<string> errors)
    : Exception("invalid docketd config:\n  - " + string.Join("\n  - ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
