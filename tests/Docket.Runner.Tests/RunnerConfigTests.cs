namespace Docket.Runner.Tests;

/// <summary>Runner config parsing and validation, spec §10 runner config.</summary>
public class RunnerConfigTests
{
    private const string ValidJson = """
    {
      "machine": {
        "work_root": "/var/lib/docket/work",
        "heartbeat_seconds": 5,
        "back_pressure": { "max_cpu_load": 0.8, "max_memory_load": 0.85, "max_disk_usage": 0.9 }
      },
      "profiles": [
        {
          "name": "default",
          "spawn": ["claude", "-p", "--input-format", "stream-json"],
          "stop": { "mode": "message", "message": "{disposition}", "wind_down_seconds": 20 },
          "events": { "source": "hooks", "mapping": { "PostToolUse": "tool-call" } },
          "telemetry": {
            "otel": true,
            "endpoint": "http://127.0.0.1:4318",
            "env": { "CLAUDE_CODE_ENABLE_TELEMETRY": "1" }
          },
          "logs": { "format": "stream-json", "capture": true, "max_bytes": 1048576, "prune_after_days": 3 },
          "max_concurrent": 3
        },
        {
          "name": "restricted",
          "spawn": ["claude", "-p", "--permission-mode", "plan"],
          "stop": { "mode": "signal" },
          "events": { "source": "none" }
        }
      ]
    }
    """;

    [Fact]
    public void Valid_config_loads_and_resolves_profiles()
    {
        var config = RunnerConfig.Load(ValidJson);

        Assert.Equal("/var/lib/docket/work", config.Machine.WorkRoot);
        Assert.Equal(TimeSpan.FromSeconds(5), config.Machine.HeartbeatInterval);
        Assert.Equal(0.85, config.Machine.BackPressure.MaxMemoryLoad);

        Assert.Equal(2, config.Profiles.Count);
        Assert.Equal(StopMode.Message, config.Default.Stop.Mode);
        Assert.Equal(TimeSpan.FromSeconds(20), config.Default.Stop.WindDown);
        Assert.Equal(3, config.Default.MaxConcurrent);
        Assert.Equal(EventsSource.Hooks, config.Default.Events.Source);
        Assert.Equal("tool-call", config.Default.Events.Mapping["PostToolUse"]);

        Assert.Same(config.Default, config.Resolve(null));               // absent → default (§7)
        Assert.Equal("restricted", config.Resolve("restricted")!.Name);   // exact-match (§7)
        Assert.Null(config.Resolve("frontend"));                          // requested-but-absent
        Assert.Equal(new HashSet<string> { "default", "restricted" }, config.DeclaredProfiles);

        // §10 telemetry: the opt-in, the destination, and the harness's own enable flag
        // (data, not docketd knowledge). A profile with no telemetry section is off with
        // an empty env — never null, so the spawn path needs no guard.
        Assert.True(config.Default.Telemetry.Otel);
        Assert.Equal("http://127.0.0.1:4318", config.Default.Telemetry.Endpoint);
        Assert.Equal("1", config.Default.Telemetry.Env["CLAUDE_CODE_ENABLE_TELEMETRY"]);
        Assert.False(config.Resolve("restricted")!.Telemetry.Otel);
        Assert.Null(config.Resolve("restricted")!.Telemetry.Endpoint);
        Assert.Empty(config.Resolve("restricted")!.Telemetry.Env);

        // §12 capture keys parse; a profile with no logs section takes the OFF default.
        Assert.True(config.Default.Logs.Capture);
        Assert.Equal(1048576, config.Default.Logs.MaxBytes);
        Assert.Equal(3, config.Default.Logs.PruneAfterDays);
        Assert.False(config.Resolve("restricted")!.Logs.Capture);
        Assert.Equal(TranscriptDefaults.MaxBytes, config.Resolve("restricted")!.Logs.MaxBytes);
        Assert.Equal(TranscriptDefaults.PruneAfterDays, config.Resolve("restricted")!.Logs.PruneAfterDays);
    }

    [Fact]
    public void Capture_defaults_to_off_when_logs_is_omitted()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["claude", "-p"] } ] }
        """;

        var logs = RunnerConfig.Load(json).Default.Logs;
        Assert.False(logs.Capture);
        Assert.Equal(TranscriptDefaults.MaxBytes, logs.MaxBytes);
        Assert.Equal(TranscriptDefaults.PruneAfterDays, logs.PruneAfterDays);
    }

    [Fact]
    public void Rejects_a_non_positive_max_bytes_and_a_negative_prune_window()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["claude"],
            "logs": { "capture": true, "max_bytes": 0, "prune_after_days": -1 } } ] }
        """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("max_bytes"));
        Assert.Contains(ex.Errors, e => e.Contains("prune_after_days"));
    }

    // ── Flat tool-call mapping validation (§10, issue #111) ────────────────────

    private static string WithMapping(string mappingJson) => $$"""
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["codex", "exec"],
            "events": { "source": "terminal", "mapping": {{mappingJson}} } } ] }
        """;

    /// <summary>
    /// The flat mode's two keys are only meaningful together, so a half-declared pair fails
    /// the load. Left inert it would be the exact failure #111 exists to remove: a profile
    /// that looks like it maps tool calls and emits none.
    /// </summary>
    [Theory]
    [InlineData("""{ "tool_event_type": "item.started" }""", "without tool_name_path")]
    [InlineData("""{ "tool_name_path": "item.tool" }""", "without tool_event_type")]
    public void Rejects_a_half_declared_flat_tool_call_mapping(string mapping, string expected)
    {
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(WithMapping(mapping)));

        Assert.Contains(ex.Errors, e => e.Contains(expected, StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("profile 'default'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>tool_name_path</c> must be a walkable list of property names. The resolver has no
    /// wildcards or indexes, so the shapes rejected here are the ones an author would write
    /// expecting a query language — better a load error than a profile that reads nothing.
    /// </summary>
    [Theory]
    [InlineData("item..tool")]      // empty middle segment
    [InlineData(".item.tool")]      // empty leading segment
    [InlineData("item.tool.")]      // empty trailing segment
    [InlineData("item . tool")]     // padded segments — would look up "item " and " tool"
    [InlineData("item.tool,,item.command")]  // an empty alternative
    public void Rejects_an_unwalkable_tool_name_path(string path)
    {
        var json = WithMapping($$"""{ "tool_event_type": "item.started", "tool_name_path": "{{path}}" }""");

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));

        Assert.Contains(ex.Errors, e =>
            e.Contains("tool_name_path", StringComparison.Ordinal)
            && (e.Contains("unwalkable", StringComparison.Ordinal) || e.Contains("empty alternative", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A <c>tool_event_type</c> that collides with the effective <c>system_type</c> or
    /// <c>assistant_type</c> is rejected: the reader dispatches on the first match, so one of
    /// the two meanings would be silently shadowed. The Codex mapping is the live example of
    /// why this is easy to hit — it deliberately points several keys at the one <c>type</c>
    /// property Codex emits, so the values must still be distinct.
    /// </summary>
    [Theory]
    // Collides with the claude default assistant_type…
    [InlineData("""{ "tool_event_type": "assistant", "tool_name_path": "item.tool" }""", "assistant_type")]
    // …with an explicitly remapped system_type (the Codex session-ref trick)…
    [InlineData("""{ "system_type": "thread.started", "tool_event_type": "thread.started", "tool_name_path": "item.tool" }""", "system_type")]
    // …and with an explicitly remapped assistant_type.
    [InlineData("""{ "assistant_type": "item.started", "tool_event_type": "item.started", "tool_name_path": "item.tool" }""", "assistant_type")]
    public void Rejects_a_tool_event_type_that_collides_with_another_discriminator(string mapping, string expected)
    {
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(WithMapping(mapping)));

        Assert.Contains(ex.Errors, e =>
            e.Contains("tool_event_type", StringComparison.Ordinal) && e.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// The worked Codex mapping loads clean, flat keys and all — the config half of the
    /// recoverability that <c>CodexStreamMappingTests</c> proves against the stream. Unknown
    /// keys stay tolerated (the seam is a bag of optional overrides, and
    /// <see cref="ValidJson"/> above carries a leftover hook-name mapping), so validation
    /// covers only mistakes that make a declared flat mode inert.
    /// </summary>
    [Fact]
    public void Accepts_the_worked_codex_flat_mapping_including_alternatives_and_unknown_keys()
    {
        var json = WithMapping("""
            { "system_type": "thread.started", "subtype_key": "type", "init_subtype": "thread.started",
              "session_id_key": "thread_id", "tool_event_type": "item.started",
              "tool_name_path": "item.command, item.tool", "some_future_key": "ignored" }
            """);

        var mapping = RunnerConfig.Load(json).Default.Events.Mapping;

        Assert.Equal("item.started", mapping["tool_event_type"]);
        Assert.Equal("item.command, item.tool", mapping["tool_name_path"]);
    }

    [Fact]
    public void Rejects_a_config_with_no_default_profile()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "primary", "spawn": ["claude"] } ] }
        """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("no 'default' profile"));
    }

    [Fact]
    public void Rejects_a_config_with_multiple_default_profiles()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [
            { "name": "default", "spawn": ["claude"] },
            { "name": "default", "spawn": ["codex"] }
          ] }
        """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        // Either the duplicate-name or the >1-default check fires; both name §10.
        Assert.Contains(ex.Errors, e => e.Contains("default") || e.Contains("duplicate"));
    }

    [Fact]
    public void Rejects_a_profile_with_an_empty_spawn_argv()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": [] } ] }
        """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("empty spawn argv"));
    }

    [Fact]
    public void Rejects_a_config_without_a_work_root()
    {
        var json = """
        { "machine": { },
          "profiles": [ { "name": "default", "spawn": ["claude"] } ] }
        """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("work_root"));
    }

    [Fact]
    public void Rejects_a_config_with_no_profiles()
    {
        var json = """{ "machine": { "work_root": "/w" }, "profiles": [] }""";

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("at least one profile"));
    }

    [Fact]
    public void Missing_optional_sections_take_honest_defaults()
    {
        // §10: events.source none is a supported, honest answer; heartbeat and
        // back-pressure have sensible defaults.
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["claude", "-p"] } ] }
        """;

        var config = RunnerConfig.Load(json);
        Assert.Equal(TimeSpan.FromSeconds(15), config.Machine.HeartbeatInterval);
        Assert.Equal(BackPressureThresholds.Default, config.Machine.BackPressure);
        Assert.Equal(EventsSource.None, config.Default.Events.Source);
        Assert.Equal(StopMode.Signal, config.Default.Stop.Mode);
        Assert.Null(config.Default.MaxConcurrent);
    }

    [Fact]
    public void Enum_parsing_is_case_insensitive()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["x"], "stop": { "mode": "MESSAGE" }, "events": { "source": "OTel" } } ] }
        """;

        var config = RunnerConfig.Load(json);
        Assert.Equal(StopMode.Message, config.Default.Stop.Mode);
        Assert.Equal(EventsSource.Otel, config.Default.Events.Source);
    }

    // §10 event relay: terminal is the only implemented source. hooks/otel parse but
    // are consumed nowhere, so they behave as none — one started event at spawn and
    // nothing after, which the plane's per-task liveness window turns into an endless
    // requeue. The warning is the only thing standing between an operator and that
    // trap, so these tests pin both who gets named and that the consequence is stated.

    [Fact]
    public void Terminal_event_source_warns_nothing()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["x"], "events": { "source": "terminal" } } ] }
        """;

        Assert.Empty(RunnerConfig.Load(json).EventRelayWarnings());
    }

    [Fact]
    public void Non_terminal_event_sources_are_each_warned_with_the_no_progress_consequence()
    {
        // ValidJson declares default=hooks and restricted=none: both are blind.
        var warnings = RunnerConfig.Load(ValidJson).EventRelayWarnings();

        Assert.Equal(3, warnings.Count);

        var perProfile = warnings.Take(2).ToArray();
        Assert.Contains(perProfile, w => w.Contains("profile 'default'", StringComparison.Ordinal)
            && w.Contains("'hooks' is not implemented and behaves as 'none'", StringComparison.Ordinal));
        Assert.Contains(perProfile, w => w.Contains("profile 'restricted'", StringComparison.Ordinal)
            && w.Contains("events.source is 'none'", StringComparison.Ordinal));
        Assert.All(perProfile, w =>
            Assert.Contains("no progress signal", w, StringComparison.Ordinal));

        // The consequence line is the whole point of the warning, and it must say the ACCURATE
        // thing: the periodic alive event satisfies the short clock, so what is actually lost is
        // the progress signal and the no-progress ceiling is what governs. An earlier version of
        // this test pinned the opposite claim, which went stale the moment the alive emitter
        // landed — the warning is only useful if its stated reason is true.
        var consequence = warnings[^1];
        Assert.Contains("alive", consequence, StringComparison.Ordinal);
        Assert.Contains("no-progress ceiling", consequence, StringComparison.Ordinal);
        Assert.Contains("30 minutes", consequence, StringComparison.Ordinal);
        Assert.Contains("terminal", consequence, StringComparison.Ordinal);
        Assert.DoesNotContain("60s", consequence, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_blind_profiles_are_named_when_sources_are_mixed()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [
            { "name": "default", "spawn": ["x"], "events": { "source": "terminal" } },
            { "name": "legacy", "spawn": ["y"], "events": { "source": "otel" } }
          ] }
        """;

        var warnings = RunnerConfig.Load(json).EventRelayWarnings();

        Assert.Equal(2, warnings.Count);
        Assert.Contains("profile 'legacy'", warnings[0], StringComparison.Ordinal);
        Assert.Contains("'otel' is not implemented", warnings[0], StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, w => w.Contains("'default'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_profile_that_omits_events_entirely_is_warned_too()
    {
        // The parse default is None (see Defaults_apply above), which is just as blind
        // as an explicit none — an omitted events block must not buy silence.
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["claude", "-p"] } ] }
        """;

        var warnings = RunnerConfig.Load(json).EventRelayWarnings();

        Assert.Equal(2, warnings.Count);
        Assert.Contains("events.source is 'none'", warnings[0], StringComparison.Ordinal);
    }

    // §10 stdin policy (#110). `deadman` holds the pipe open for the worker's life — the
    // dead-man's switch, and the only behaviour there used to be. `closed` sends EOF right
    // after spawn, which is the difference between a working Codex worker and one that
    // blocks forever inside prompt resolution. These pin the parse, the strictness, and the
    // one pairing that cannot mean anything.

    [Fact]
    public void Stdin_defaults_to_deadman_when_the_key_is_absent()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["claude", "-p"] } ] }
        """;

        // The pre-#110 behaviour is what an untouched config must still get: every existing
        // profile in the wild omits this key.
        Assert.Equal(StdinPolicy.Deadman, RunnerConfig.Load(json).Default.Stdin);
        Assert.Empty(RunnerConfig.Load(json).StdinPolicyWarnings());
    }

    [Fact]
    public void Stdin_closed_parses_and_is_per_profile()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [
            { "name": "default", "spawn": ["claude", "-p"] },
            { "name": "codex", "spawn": ["codex", "exec", "p"], "stdin": "closed" }
          ] }
        """;

        var config = RunnerConfig.Load(json);

        // The point of putting this on the profile rather than the machine: one box can run
        // both a harness that survives the pipe and one that cannot.
        Assert.Equal(StdinPolicy.Deadman, config.Default.Stdin);
        Assert.Equal(StdinPolicy.Closed, config.Resolve("codex")!.Stdin);
    }

    [Theory]
    [InlineData("deadman", StdinPolicy.Deadman)]
    [InlineData("DEADMAN", StdinPolicy.Deadman)]
    [InlineData("closed", StdinPolicy.Closed)]
    [InlineData("Closed", StdinPolicy.Closed)]
    [InlineData("  closed  ", StdinPolicy.Closed)]
    public void Stdin_accepts_either_spelling_case_insensitively(string declared, StdinPolicy expected)
    {
        var json = $$"""
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["x"], "stdin": "{{declared}}" } ] }
        """;

        Assert.Equal(expected, RunnerConfig.Load(json).Default.Stdin);
    }

    [Theory]
    [InlineData("close")]     // the plausible typo
    [InlineData("dead-man")]  // the plausible other typo
    [InlineData("open")]
    [InlineData("0")]         // an enum ordinal is not a config surface
    [InlineData("true")]
    public void An_unrecognized_stdin_value_is_refused_rather_than_silently_defaulted(string declared)
    {
        var json = $$"""
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["x"], "stdin": "{{declared}}" } ] }
        """;

        // Deliberately unlike stop.mode/events.source, which fall back to a documented
        // default on a typo. Falling back HERE restores the pipe the profile was written to
        // escape, and the symptom is a worker that hangs before its first turn with nothing
        // logged anywhere — the exact failure #110 exists to end. So it must be loud, and
        // the message has to name what to type instead.
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        var problem = Assert.Single(ex.Errors, e => e.Contains("stdin", StringComparison.Ordinal));
        Assert.Contains(declared, problem, StringComparison.Ordinal);
        Assert.Contains("'deadman'", problem, StringComparison.Ordinal);
        Assert.Contains("'closed'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_mode_stop_on_a_closed_stdin_profile_is_refused_as_a_contradiction()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "codexish", "spawn": ["x"], "stdin": "closed",
                          "stop": { "mode": "message", "message": "{disposition}" } },
                        { "name": "default", "spawn": ["y"] } ] }
        """;

        // A message-mode stop IS a write to the worker's stdin. On a closed-stdin profile
        // that write has nowhere to land, so the declaration promises a graceful wind-down
        // the machine can never deliver — and it would degrade silently into the deadline
        // kill, which is precisely the "declared one thing, did another" shape §10's stop
        // honesty forbids. Refused at load, where both halves can be named.
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        var problem = Assert.Single(ex.Errors, e => e.Contains("codexish", StringComparison.Ordinal));
        Assert.Contains("stop.mode 'message'", problem, StringComparison.Ordinal);
        Assert.Contains("stdin 'closed'", problem, StringComparison.Ordinal);
        // Actionable: it must say which way out to take, not merely that this is wrong.
        Assert.Contains("stop.mode 'signal'", problem, StringComparison.Ordinal);
        Assert.Contains("stdin 'deadman'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signal_mode_stop_on_a_closed_stdin_profile_is_the_supported_pairing()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["codex", "exec", "p"], "stdin": "closed",
                          "stop": { "mode": "signal" } } ] }
        """;

        // The Codex shape, and the only one that is honest: no stdin, so no turn, so the
        // stop is the granted TTL and then the tree-kill.
        var config = RunnerConfig.Load(json);
        Assert.Equal(StdinPolicy.Closed, config.Default.Stdin);
        Assert.Equal(StopMode.Signal, config.Default.Stop.Mode);
    }

    [Fact]
    public void Closing_stdin_is_warned_at_startup_with_the_containment_it_gives_up()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [
            { "name": "default", "spawn": ["claude", "-p"] },
            { "name": "codex", "spawn": ["codex", "exec", "p"], "stdin": "closed" }
          ] }
        """;

        var warnings = RunnerConfig.Load(json).StdinPolicyWarnings();

        // Only the profile that opted out is named, and the line states the actual loss —
        // docketd's own death no longer takes the worker down — rather than just repeating
        // the setting back. An operator who reads only this line has to come away knowing
        // that a worker surviving a docketd crash is now expected, not a reaper bug.
        var warning = Assert.Single(warnings);
        Assert.Contains("profile 'codex'", warning, StringComparison.Ordinal);
        Assert.Contains("NO dead-man's switch", warning, StringComparison.Ordinal);
        Assert.Contains("if docketd dies", warning, StringComparison.Ordinal);
        Assert.Contains("next docketd start", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("'default'", warning, StringComparison.Ordinal);
    }
}
