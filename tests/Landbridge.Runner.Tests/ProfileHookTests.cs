using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Runner.Tests;

/// <summary>Argv profile hooks: fail-closed <c>before_spawn</c>, best-effort <c>after_exit</c>.</summary>
public class ProfileHookLoadTests
{
    [Fact]
    public void Hooks_load_as_argv()
    {
        var json = """
            { "machine": { "work_root": "/w" },
              "profiles": [ { "name": "default", "spawn": ["codex", "exec"], "prompt": "go",
                "hooks": { "before_spawn": ["/bin/ensure-mcp", "{mcp_url}"],
                           "after_exit": ["/bin/idle"] } } ] }
            """;

        var hooks = RunnerConfig.Load(json).Profiles["default"].Hooks;
        Assert.Equal(["/bin/ensure-mcp", "{mcp_url}"], hooks.BeforeSpawn);
        Assert.Equal(["/bin/idle"], hooks.AfterExit);
    }

    [Fact]
    public void Rejects_an_empty_before_spawn_argv0()
    {
        var json = """
            { "machine": { "work_root": "/w" },
              "profiles": [ { "name": "default", "spawn": ["codex", "exec"], "prompt": "go",
                "hooks": { "before_spawn": [""] } } ] }
            """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("before_spawn", StringComparison.Ordinal));
    }
}

public sealed class ProfileHookSpawnTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    [Fact]
    public async Task Before_spawn_runs_and_sees_mcp_url()
    {
        var task = SessionId.New();
        Supervisor().Spawn(
            new DispatchCommand(
                task, "default",
                SpawnSubstitutions: new Dictionary<string, string> { ["mcp_url"] = "http://plane.test/mcp" }),
            TestKit.Profile("echo-env", hooks: new ProfileHooks(
                BeforeSpawn: [TestKit.HarnessPath(), "hook-ok", "{mcp_url}"])),
            "machine-42");

        var hookPath = Path.Combine(_workRoot, task.ToString(), "hook");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(hookPath), TimeSpan.FromSeconds(10)));
        var argv = await File.ReadAllTextAsync(hookPath);
        Assert.Contains("hook-ok", argv, StringComparison.Ordinal);
        Assert.Contains("http://plane.test/mcp", argv, StringComparison.Ordinal);

        Assert.True(_supervisor!.Kill(task));
    }

    [Fact]
    public void A_failing_before_spawn_does_not_start_the_harness()
    {
        var task = SessionId.New();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Supervisor().Spawn(
                TestKit.Dispatch(task),
                TestKit.Profile("echo-env", hooks: new ProfileHooks(
                    BeforeSpawn: [TestKit.HarnessPath(), "hook-fail"])),
                "m"));

        Assert.Contains("before_spawn", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workRoot, task.ToString(), "env")));
    }

    [Fact]
    public async Task Before_spawn_does_not_carry_task_id_or_worker_token()
    {
        var task = SessionId.New();
        Supervisor().Spawn(
            new DispatchCommand(task, "default", WorkerToken: "lbr_w_secret"),
            TestKit.Profile("echo-env", hooks: new ProfileHooks(
                BeforeSpawn: [TestKit.HarnessPath(), "hook-env"])),
            "machine-42");

        var path = Path.Combine(_workRoot, task.ToString(), "hook-env");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(10)));
        var env = await File.ReadAllTextAsync(path);
        Assert.Contains("LANDBRIDGE_HOOK=before_spawn", env, StringComparison.Ordinal);
        Assert.Contains("LANDBRIDGE_MACHINE_ID=machine-42", env, StringComparison.Ordinal);
        Assert.DoesNotContain("LANDBRIDGE_SESSION_ID=", env, StringComparison.Ordinal);
        Assert.DoesNotContain("LANDBRIDGE_WORKER_TOKEN=", env, StringComparison.Ordinal);

        Assert.True(_supervisor!.Kill(task));
    }

    [Fact]
    public async Task Before_spawn_receives_the_profile_env_map()
    {
        var task = SessionId.New();
        Supervisor().Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile(
                "echo-env",
                env: new Dictionary<string, string>
                {
                    ["CODEX_HOME"] = "{work_dir}/.codex",
                    ["LANDBRIDGE_PROFILE_ENV_MARKER"] = "from-profile",
                },
                hooks: new ProfileHooks(BeforeSpawn: [TestKit.HarnessPath(), "hook-env"])),
            "machine-42");

        var path = Path.Combine(_workRoot, task.ToString(), "hook-env");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(10)));
        var env = await File.ReadAllTextAsync(path);
        var workDir = Path.Combine(_workRoot, task.ToString());
        // Verbatim substitution — same rule as ProfileEnvSpawnTests. Path.Combine
        // on Windows would look for a backslash the config never wrote.
        Assert.Contains($"CODEX_HOME={workDir}/.codex", env, StringComparison.Ordinal);
        Assert.Contains("LANDBRIDGE_PROFILE_ENV_MARKER=from-profile", env, StringComparison.Ordinal);
        Assert.DoesNotContain("LANDBRIDGE_SESSION_ID=", env, StringComparison.Ordinal);
        Assert.DoesNotContain("LANDBRIDGE_WORKER_TOKEN=", env, StringComparison.Ordinal);

        Assert.True(_supervisor!.Kill(task));
    }

    [Fact]
    public async Task After_exit_runs_on_a_kill()
    {
        var task = SessionId.New();
        var supervisor = Supervisor();
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("run", hooks: new ProfileHooks(
                AfterExit: [TestKit.HarnessPath(), "hook-ok", "after"])),
            "m");

        Assert.True(supervisor.Kill(task));
        var hookPath = Path.Combine(_workRoot, task.ToString(), "hook");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(hookPath), TimeSpan.FromSeconds(10)));
        Assert.Contains("after", await File.ReadAllTextAsync(hookPath), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { _supervisor?.KillAll(); } catch { /* best effort */ }
        TestKit.TryDeleteRoot(_workRoot);
    }
}
