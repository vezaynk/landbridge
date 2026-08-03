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
}
