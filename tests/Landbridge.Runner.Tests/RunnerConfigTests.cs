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
          "spawn": ["claude-agent-acp"], "prompt": "go",
          "prompt": "Do the task.",
          "stop": { "wind_down_seconds": 20 },
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
          "spawn": ["claude-agent-acp"],
          "prompt": "Do the task, conservatively."
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
        Assert.Equal(TimeSpan.FromSeconds(20), config.Default.Stop.WindDown);
        Assert.Equal(3, config.Default.MaxConcurrent);
        Assert.Equal("Do the task.", config.Default.Prompt);

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

    /// <summary>
    /// Keys that no longer exist must be <em>skipped</em>, not fatal. Two generations of
    /// them are pinned here: the three that were always inert (<c>stop.signal</c>,
    /// <c>logs.path</c>, <c>logs.format</c>), and the whole stream protocol that has since
    /// been removed (<c>stop.mode</c>, <c>stop.message</c>, <c>events</c>, <c>resume</c>,
    /// <c>stdin</c>, <c>protocol</c>). A machine must not refuse to start on a config file
    /// that worked yesterday — the operator's fix is to delete dead keys at leisure, not
    /// under an outage.
    ///
    /// <para>This pins a compatibility promise the runner-config reference now makes in
    /// three places ("a config still declaring either is accepted unchanged"). It holds
    /// because <see cref="RunnerJsonContext"/> sets no <c>UnmappedMemberHandling</c>, so
    /// System.Text.Json skips members with no matching property — but that is a default
    /// somebody could tighten in one line while believing it only affected typos, which is
    /// exactly the kind of change this test exists to stop. The keys are inert here, as
    /// they were inert before removal; what must not happen is a machine refusing to start
    /// on a config file that worked yesterday.</para>
    /// </summary>
    [Fact]
    public void A_config_declaring_the_removed_stop_signal_and_logs_path_format_keys_still_loads()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["opencode", "acp"], "prompt": "go", "prompt": "go",
            "protocol": "stream",
            "stdin": "closed",
            "stop": { "mode": "signal", "signal": "SIGTERM", "message": "{disposition}",
                      "wind_down_seconds": 12 },
            "resume": { "args": ["opencode", "run", "--session", "{session_id}"] },
            "events": { "source": "terminal", "mapping": { "tool_event_type": "tool_use" } },
            "logs": { "path": "/var/log/docket/worker.ndjson", "format": "stream-json",
                      "capture": true, "max_bytes": 4096 } } ] }
        """;

        var config = RunnerConfig.Load(json);

        // The surviving neighbours in each section still parse, so the removed keys were
        // skipped rather than derailing the object they sat in. Note `protocol: stream`
        // among them: it does not resurrect the mode, it is simply a word nothing reads.
        Assert.Equal(TimeSpan.FromSeconds(12), config.Default.Stop.WindDown);
        Assert.True(config.Default.Logs.Capture);
        Assert.Equal(4096, config.Default.Logs.MaxBytes);
    }

    [Fact]
    public void Capture_defaults_to_off_when_logs_is_omitted()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "prompt": "go", "spawn": ["claude", "-p"] } ] }
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
          "profiles": [ { "name": "default", "spawn": ["claude-agent-acp"], "prompt": "go", "prompt": "go",
            "logs": { "capture": true, "max_bytes": 0, "prune_after_days": -1 } } ] }
        """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("max_bytes"));
        Assert.Contains(ex.Errors, e => e.Contains("prune_after_days"));
    }

    [Fact]
    public void Rejects_a_config_with_no_default_profile()
    {
        var json = """
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "primary", "prompt": "go", "spawn": ["claude"] } ] }
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
            { "name": "default", "prompt": "go", "spawn": ["claude"] },
            { "name": "default", "prompt": "go", "spawn": ["codex"] }
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
          "profiles": [ { "name": "default", "prompt": "go", "spawn": [] } ] }
        """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("empty spawn argv"));
    }

    [Fact]
    public void Rejects_a_config_without_a_work_root()
    {
        var json = """
        { "machine": { },
          "profiles": [ { "name": "default", "prompt": "go", "spawn": ["claude"] } ] }
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
          "profiles": [ { "name": "default", "spawn": ["claude-agent-acp"], "prompt": "go", "prompt": "go" } ] }
        """;

        var config = RunnerConfig.Load(json);
        Assert.Equal(TimeSpan.FromSeconds(15), config.Machine.HeartbeatInterval);
        Assert.Equal(BackPressureThresholds.Default, config.Machine.BackPressure);
        Assert.Equal(TimeSpan.FromSeconds(30), config.Default.Stop.WindDown);
        Assert.Null(config.Default.MaxConcurrent);
        Assert.Empty(config.Default.Env);
        Assert.Empty(config.Default.Files);
        Assert.Empty(config.Default.ConfigOptions);
        Assert.Null(config.Default.SessionMode);
    }

    [Fact]
    public void Session_mode_is_the_set_mode_pin()
    {
        var config = RunnerConfig.Load(AcpProfile(extra: """
            "session_mode": "approve",
            """));

        Assert.Equal("approve", config.Default.SessionMode);
    }

    [Fact]
    public void Session_mode_refuses_an_empty_string()
    {
        Assert.False(RunnerConfig.TryLoad(
            AcpProfile(extra: """
            "session_mode": "",
            """),
            out _, out var errors));
        Assert.Contains(errors, e => e.Contains("session_mode") && e.Contains("empty"));
    }

    [Fact]
    public void Config_options_are_the_set_config_option_pins()
    {
        var config = RunnerConfig.Load(AcpProfile(extra: """
            "config_options": { "model": "anthropic/claude-haiku-4-5-20251001", "mode": "code" },
            """));

        Assert.Equal("anthropic/claude-haiku-4-5-20251001", config.Default.ConfigOptions["model"]);
        Assert.Equal("code", config.Default.ConfigOptions["mode"]);
    }

    [Fact]
    public void Config_options_refuse_an_empty_key_or_value()
    {
        Assert.False(RunnerConfig.TryLoad(
            AcpProfile(extra: """
            "config_options": { "": "x" },
            """),
            out _, out var emptyKey));
        Assert.Contains(emptyKey, e => e.Contains("config_options") && e.Contains("empty key"));

        Assert.False(RunnerConfig.TryLoad(
            AcpProfile(extra: """
            "config_options": { "model": "" },
            """),
            out _, out var emptyValue));
        Assert.Contains(emptyValue, e => e.Contains("config_options") && e.Contains("empty value"));
    }


    // §10 event relay: terminal is the only implemented source. hooks/otel parse but
    // are consumed nowhere, so they behave as none — one started event at spawn and
    // nothing after, which the plane's per-task liveness window turns into an endless
    // requeue. The warning is the only thing standing between an operator and that
    // trap, so these tests pin both who gets named and that the consequence is stated.





    // §10 stdin policy (#110). `deadman` holds the pipe open for the worker's life — the
    // dead-man's switch, and the only behaviour there used to be. `closed` sends EOF right
    // after spawn, which is the difference between a working Codex worker and one that
    // blocks forever inside prompt resolution. These pin the parse, the strictness, and the
    // one pairing that cannot mean anything.








    // ── §10 protocol: acp ───────────────────────────────────────────────────────




    /// <summary>An ACP agent takes no prompt on argv, so a profile without one starts a
    /// worker that connects and waits forever.</summary>
    [Fact]
    public void A_profile_without_a_prompt_is_refused()
    {
        Assert.False(RunnerConfig.TryLoad(AcpProfile(prompt: null), out _, out var errors));

        Assert.Contains(errors, e => e.Contains("has no `prompt`"));
    }







    /// <summary>
    /// A worked ACP profile, shaped like the OpenCode one in the enroll reference:
    /// <c>opencode acp</c>, a prompt on the wire, and none of the stream-mode scaffolding.
    /// </summary>
    private static string AcpProfile(string? prompt = "Do the task.", string extra = "") =>
        $$"""
        {
          "machine": { "work_root": "/var/lib/docketd/work" },
          "profiles": [
            {
              "name": "default",
              {{(prompt is null ? "" : $"\"prompt\": \"{prompt}\",")}}
              {{extra}}
              "spawn": ["opencode", "acp"]
            }
          ]
        }
        """;
}
