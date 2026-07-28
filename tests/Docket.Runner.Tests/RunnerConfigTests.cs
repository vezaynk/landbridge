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
          "telemetry": { "otel": true, "endpoint": "http://127.0.0.1:4318" },
          "logs": { "path": "~/.claude/logs", "format": "jsonl" },
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
