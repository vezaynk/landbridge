using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// #112 G3: <c>profiles[].env</c> is a per-spawn environment map with the same
/// <c>{…}</c> substitutions <c>spawn</c> already gets. Load validation refuses the
/// reserved <c>DOCKET_*</c> names; the supervisor substitutes and stamps the rest
/// onto a real child.
/// </summary>
public class ProfileEnvTests
{
    private static string WithEnv(string envJson) => $$"""
        { "machine": { "work_root": "/w" },
          "profiles": [ { "name": "default", "spawn": ["grok", "-p"], "prompt": "go",
            "env": {{envJson}} } ] }
        """;

    [Fact]
    public void An_absent_env_block_is_an_empty_map()
    {
        var json = """
            { "machine": { "work_root": "/w" },
              "profiles": [ { "name": "default", "prompt": "go", "spawn": ["grok", "-p"] } ] }
            """;

        Assert.Empty(RunnerConfig.Load(json).Default.Env);
    }

    [Fact]
    public void A_declared_env_map_loads_verbatim()
    {
        var config = RunnerConfig.Load(WithEnv("""{ "GROK_HOME": "{work_dir}/.grok", "XAI_API_KEY": "from-profile" }"""));

        Assert.Equal("{work_dir}/.grok", config.Default.Env["GROK_HOME"]);
        Assert.Equal("from-profile", config.Default.Env["XAI_API_KEY"]);
    }

    [Theory]
    [InlineData("DOCKET_TASK_ID")]
    [InlineData("DOCKET_MACHINE_ID")]
    [InlineData("DOCKET_WORKER_TOKEN")]
    [InlineData("DOCKET_TRACEPARENT")]
    public void Refuses_the_variables_docketd_stamps_itself(string key)
    {
        var ex = Assert.Throws<RunnerConfigException>(() =>
            RunnerConfig.Load(WithEnv($$"""{ "{{key}}": "forged" }""")));

        Assert.Contains(ex.Errors, e =>
            e.Contains(key, StringComparison.Ordinal)
            && e.Contains("not configurably", StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_an_empty_env_key()
    {
        var ex = Assert.Throws<RunnerConfigException>(() =>
            RunnerConfig.Load(WithEnv("""{ "": "nope" }""")));

        Assert.Contains(ex.Errors, e => e.Contains("empty key", StringComparison.Ordinal));
    }
}

/// <summary>
/// The same G3 rules, observed on a real spawned child via the harness's
/// <c>echo-env</c> mode — substitution has to survive <see cref="System.Diagnostics.ProcessStartInfo"/>.
/// </summary>
public sealed class ProfileEnvSpawnTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    [Fact]
    public async Task Profile_env_reaches_the_worker_with_work_dir_substituted()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("echo-env", env: new Dictionary<string, string>
            {
                ["GROK_HOME"] = "{work_dir}/.grok",
                ["DOCKET_PROFILE_ENV_MARKER"] = "from-profile",
            }),
            "machine-42");

        var env = await ReadEnvMarker(task);
        var workDir = Path.Combine(_workRoot, task.ToString());

        Assert.Equal("from-profile", env["DOCKET_PROFILE_ENV_MARKER"]);
        // Verbatim substitution, so the expectation is verbatim too: the declared value is
        // "{work_dir}/.grok" and `Substitute` only replaces the token, leaving the operator's
        // own separator alone. NOT Path.Combine — on Windows that builds an all-backslash
        // string while the actual keeps the config's forward slash before `.grok`, which is
        // exactly how this failed the Windows leg of #164. docketd must not silently rewrite
        // a value the operator wrote; spawn argv and files[] paths substitute the same way.
        Assert.Equal($"{workDir}/.grok", env["GROK_HOME"]);
        Assert.Equal(task.ToString(), env["DOCKET_TASK_ID"]);
        Assert.Equal("machine-42", env["DOCKET_MACHINE_ID"]);

        Assert.True(supervisor.Kill(task));
    }

    [Fact]
    public async Task Telemetry_env_still_overlays_profile_env_when_otel_is_on()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile(
                "echo-env",
                env: new Dictionary<string, string>
                {
                    ["GROK_HOME"] = "/isolated",
                    ["HARNESS_TELEMETRY_TEST_MARKER"] = "from-profile-env",
                },
                telemetry: new TelemetryConfig(
                    Otel: true,
                    Endpoint: "http://127.0.0.1:4318",
                    Env: new Dictionary<string, string>
                    {
                        ["HARNESS_TELEMETRY_TEST_MARKER"] = "from-telemetry-env",
                    })),
            "machine-42");

        var env = await ReadEnvMarker(task);

        Assert.Equal("/isolated", env["GROK_HOME"]);
        Assert.Equal("from-telemetry-env", env["HARNESS_TELEMETRY_TEST_MARKER"]);
        Assert.Equal("http://127.0.0.1:4318", env["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        Assert.True(supervisor.Kill(task));
    }

    private async Task<Dictionary<string, string>> ReadEnvMarker(TaskId task)
    {
        var path = Path.Combine(_workRoot, task.ToString(), "env");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(15)),
            "harness never recorded its environment");

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            var split = line.IndexOf('=', StringComparison.Ordinal);
            if (split > 0)
                env[line[..split]] = line[(split + 1)..];
        }
        return env;
    }

    public void Dispose()
    {
        try { _supervisor?.KillAll(); } catch { /* best effort */ }
        TestKit.TryDeleteRoot(_workRoot);
    }
}
