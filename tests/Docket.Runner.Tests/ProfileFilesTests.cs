using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>#112 G2: <c>profiles[].files</c> writes into the work dir before spawn.</summary>
public class ProfileFilesLoadTests
{
    [Fact]
    public void A_declared_file_loads()
    {
        var json = """
            { "machine": { "work_root": "/w" },
              "profiles": [ { "name": "default", "spawn": ["grok", "-p"],
                "files": [ { "path": "{work_dir}/.grok/config.toml",
                             "contents": "url = \"{mcp_url}\"", "mode": "0600" } ] } ] }
            """;

        var file = Assert.Single(RunnerConfig.Load(json).Default.Files);
        Assert.Equal("{work_dir}/.grok/config.toml", file.Path);
        Assert.Contains("{mcp_url}", file.Contents, StringComparison.Ordinal);
        Assert.Equal("0600", file.Mode);
    }

    [Fact]
    public void Rejects_a_file_with_no_path_or_contents()
    {
        var json = """
            { "machine": { "work_root": "/w" },
              "profiles": [ { "name": "default", "spawn": ["grok", "-p"],
                "files": [ { } ] } ] }
            """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("empty path", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("missing contents", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_non_octal_mode()
    {
        var json = """
            { "machine": { "work_root": "/w" },
              "profiles": [ { "name": "default", "spawn": ["grok", "-p"],
                "files": [ { "path": "x", "contents": "y", "mode": "rwx" } ] } ] }
            """;

        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(json));
        Assert.Contains(ex.Errors, e => e.Contains("octal", StringComparison.Ordinal));
    }

    [Fact]
    public void Work_dir_jail_resolves_dotdot()
    {
        var workDir = TestKit.NewWorkRoot();
        try
        {
            Assert.True(ProcessSupervisor.IsUnderWorkDir(workDir, Path.Combine(workDir, ".grok", "config.toml")));
            Assert.True(ProcessSupervisor.IsUnderWorkDir(workDir, workDir));
            Assert.True(ProcessSupervisor.IsUnderWorkDir(workDir, Path.Combine(workDir, "..", Path.GetFileName(workDir), "x")));
            Assert.False(ProcessSupervisor.IsUnderWorkDir(workDir, Path.Combine(workDir, "..", "secret")));
            Assert.False(ProcessSupervisor.IsUnderWorkDir(workDir, Path.Combine(Path.GetTempPath(), "not-this")));
        }
        finally
        {
            TestKit.TryDeleteRoot(workDir);
        }
    }
}

public sealed class ProfileFilesSpawnTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    [Fact]
    public async Task Declared_file_is_written_with_substitutions_before_the_worker_starts()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(
            new DispatchCommand(
                task, "default",
                SpawnSubstitutions: new Dictionary<string, string> { ["mcp_url"] = "http://plane.test/mcp" }),
            TestKit.Profile("echo-env", files:
            [
                new ProfileFile(
                    "{work_dir}/.grok/config.toml",
                    "url = \"{mcp_url}\"\ntask = \"{task_id}\"\n"),
            ]),
            "machine-42");

        var workDir = Path.Combine(_workRoot, task.ToString());
        var path = Path.Combine(workDir, ".grok", "config.toml");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(5)));
        var body = await File.ReadAllTextAsync(path);
        Assert.Contains("url = \"http://plane.test/mcp\"", body, StringComparison.Ordinal);
        Assert.Contains($"task = \"{task}\"", body, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        var envPath = Path.Combine(workDir, "env");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(envPath), TimeSpan.FromSeconds(10)));
        var env = await File.ReadAllTextAsync(envPath);
        Assert.Contains("DOCKET_MCP_URL=http://plane.test/mcp", env, StringComparison.Ordinal);

        Assert.True(supervisor.Kill(task));
    }

    [Fact]
    public async Task A_claude_shaped_file_can_be_written_from_worker_token_and_mcp_url()
    {
        var task = TaskId.New();
        Supervisor().Spawn(
            new DispatchCommand(
                task, "default",
                WorkerToken: "dkt_w_abc",
                SpawnSubstitutions: new Dictionary<string, string> { ["mcp_url"] = "http://plane.test/mcp" }),
            TestKit.Profile("echo-env", files:
            [
                new ProfileFile(
                    "{work_dir}/mcp-{task_id}.json",
                    """{"mcpServers":{"docket":{"type":"http","url":"{mcp_url}","headers":{"Authorization":"Bearer {worker_token}"}}}}"""),
            ]),
            "m");

        var path = Path.Combine(_workRoot, task.ToString(), $"mcp-{task}.json");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(5)));
        var body = await File.ReadAllTextAsync(path);
        Assert.Contains("\"url\":\"http://plane.test/mcp\"", body, StringComparison.Ordinal);
        Assert.Contains("\"Authorization\":\"Bearer dkt_w_abc\"", body, StringComparison.Ordinal);
        Assert.True(_supervisor!.Kill(task));
    }

    [Fact]
    public async Task Mcp_json_is_not_written_when_the_argv_never_names_it()
    {
        var task = TaskId.New();
        Supervisor().Spawn(
            new DispatchCommand(task, "default", McpConfigJson: """{"mcpServers":{}}"""),
            TestKit.Profile("echo-env"),
            "m");

        var workDir = Path.Combine(_workRoot, task.ToString());
        Assert.True(await TestKit.WaitUntilAsync(
            () => File.Exists(Path.Combine(workDir, "env")), TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(Path.Combine(workDir, "mcp.json")));
        Assert.True(_supervisor!.Kill(task));
    }

    [Fact]
    public void A_path_that_escapes_the_work_dir_fails_the_spawn()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            supervisor.Spawn(
                TestKit.Dispatch(task),
                TestKit.Profile("echo-env", files:
                [
                    new ProfileFile("{work_dir}/../outside.txt", "nope"),
                ]),
                "m"));

        Assert.Contains("outside the work dir", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workRoot, "outside.txt")));
    }

    public void Dispose()
    {
        try { _supervisor?.KillAll(); } catch { /* best effort */ }
        TestKit.TryDeleteRoot(_workRoot);
    }
}
