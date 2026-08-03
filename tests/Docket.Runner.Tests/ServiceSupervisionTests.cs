using Docket.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// §10 operator-declared services: config validation, supervision, and the two facts
/// the rest of the system leans on — the heartbeat report, and whether a declared
/// service is currently up (which is what refuse-at-dial consults, §8.2).
/// </summary>
public class ServiceSupervisionTests
{
    // ── Config validation ───────────────────────────────────────────────────────

    private static string Config(string servicesJson) => $$"""
    {
      "machine": { "work_root": "/tmp/docketd-fake" },
      "profiles": [ { "name": "default", "spawn": ["noop"] } ],
      "services": {{servicesJson}}
    }
    """;

    [Fact]
    public void A_declared_service_loads_with_its_defaults()
    {
        var config = RunnerConfig.Load(Config("""
        [ { "name": "web-dev", "spawn": ["/bin/echo", "hi"], "port": 5173,
            "readiness": { "tcp_port": 5173 } } ]
        """));

        var service = Assert.Single(config.DeclaredServices);
        Assert.Equal("web-dev", service.Name);
        Assert.Equal(["/bin/echo", "hi"], service.Spawn);
        Assert.Equal(5173, service.Port);
        Assert.Equal(5173, service.Readiness!.TcpPort);
        Assert.Equal(ServiceDefaults.ReadinessTimeout, service.Readiness.Timeout);
        Assert.Equal(ServiceDefaults.MaxBackoff, service.MaxBackoff);
        Assert.False(service.Logs.Capture); // capture is opt-in, as for tasks
    }

    [Fact]
    public void A_config_with_no_services_section_is_normal()
    {
        var config = RunnerConfig.Load("""
        { "machine": { "work_root": "/w" }, "profiles": [ { "name": "default", "spawn": ["x"] } ] }
        """);

        Assert.Empty(config.DeclaredServices);
    }

    /// <summary>
    /// The name validator is a security control, not hygiene: the name becomes a
    /// directory under the state dir, occupying the slot a TaskId Guid fills for
    /// transcripts — and the Guid is the whole reason that path builder is closed.
    /// </summary>
    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/name")]
    [InlineData("back\\slash")]
    [InlineData("/absolute")]
    [InlineData("..")]
    [InlineData("has space")]
    [InlineData("dot.dot")]
    [InlineData("")]
    public void A_service_name_that_could_steer_a_path_is_refused(string name)
    {
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            $$"""[ { "name": {{System.Text.Json.JsonSerializer.Serialize(name)}}, "spawn": ["x"] } ]""")));

        Assert.Contains(ex.Errors, e => e.Contains("service name", StringComparison.Ordinal)
                                        || e.Contains("missing `name`", StringComparison.Ordinal));
    }

    [Fact]
    public void A_service_name_longer_than_the_cap_is_refused()
    {
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            $$"""[ { "name": "{{new string('a', 65)}}", "spawn": ["x"] } ]""")));
        Assert.Contains(ex.Errors, e => e.Contains("1-64 characters", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("web-dev")]
    [InlineData("web_dev_2")]
    [InlineData("API")]
    public void An_ordinary_service_name_is_accepted(string name)
    {
        var config = RunnerConfig.Load(Config($$"""[ { "name": "{{name}}", "spawn": ["x"] } ]"""));
        Assert.Equal(name, Assert.Single(config.DeclaredServices).Name);
    }

    [Fact]
    public void An_unimplemented_backend_fails_loudly_rather_than_being_ignored()
    {
        // The key exists precisely so a config asking for delegation is refused instead
        // of silently supervised the other way (§10).
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """[ { "name": "api", "spawn": ["x"], "backend": "systemd-run" } ]""")));

        Assert.Contains(ex.Errors, e => e.Contains("backend 'systemd-run'", StringComparison.Ordinal)
                                        && e.Contains("not implemented", StringComparison.Ordinal));
    }

    [Fact]
    public void The_direct_backend_is_accepted_explicitly()
    {
        var config = RunnerConfig.Load(Config(
            """[ { "name": "api", "spawn": ["x"], "backend": "direct" } ]"""));
        Assert.Single(config.DeclaredServices);
    }

    [Fact]
    public void Duplicate_names_and_empty_spawn_are_refused()
    {
        var dup = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """[ { "name": "api", "spawn": ["x"] }, { "name": "api", "spawn": ["y"] } ]""")));
        Assert.Contains(dup.Errors, e => e.Contains("duplicate service name", StringComparison.Ordinal));

        var empty = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """[ { "name": "api", "spawn": [] } ]""")));
        Assert.Contains(empty.Errors, e => e.Contains("empty spawn argv", StringComparison.Ordinal));
    }

    [Fact]
    public void An_out_of_range_port_is_refused()
    {
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """[ { "name": "api", "spawn": ["x"], "port": 70000 } ]""")));
        Assert.Contains(ex.Errors, e => e.Contains("port must be in 1..65535", StringComparison.Ordinal));
    }

    // ── Log store ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_log_store_refuses_a_name_that_would_escape_its_root()
    {
        // Defence in depth: config validation already rejects these, but the store is
        // the thing holding the path invariant, so it re-checks rather than trusting
        // that every future caller came through validation.
        var root = TestKit.NewWorkRoot();
        try
        {
            var store = new ServiceLogStore(Path.Combine(root, "services"));
            Assert.Throws<ArgumentException>(() => store.CreateWriter("../escape", 1024));
        }
        finally
        {
            TestKit.TryDeleteRoot(root);
        }
    }

    [Fact]
    public void Service_logs_live_outside_the_pruned_transcript_root()
    {
        // The separation is load-bearing: TranscriptStore.Prune deletes any top-level
        // dir under the transcripts root whose newest write is older than the window,
        // which for a long-idle service would unlink a LIVE log directory.
        var state = TestKit.NewWorkRoot();
        try
        {
            var transcripts = Path.Combine(state, TranscriptDefaults.DirName);
            var services = Path.Combine(state, ServiceLogStore.DirName);
            Assert.NotEqual(transcripts, services);

            using var writer = new ServiceLogStore(services).CreateWriter("web-dev", 4096);
            writer.WriteStdoutLine("listening on 5173");
            writer.Dispose();

            // A prune sweep over the transcripts root cannot see it.
            new TranscriptStore(transcripts, TimeSpan.FromTicks(1), TimeProvider.System).Prune();
            Assert.True(Directory.Exists(Path.Combine(services, "web-dev")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(services, "web-dev")));
        }
        finally
        {
            TestKit.TryDeleteRoot(state);
        }
    }

    // ── Supervision ─────────────────────────────────────────────────────────────

    private static ServiceConfig Service(
        string name, IReadOnlyList<string> spawn, int? port = null,
        ReadinessConfig? readiness = null, string? workDir = null,
        IReadOnlyDictionary<string, string>? env = null) =>
        new(name, spawn, workDir, env ?? new Dictionary<string, string>(StringComparer.Ordinal),
            port, readiness, ServiceDefaults.MaxBackoff,
            new LogsConfig(null, null));

    [Fact]
    public async Task A_service_is_reported_running_once_its_readiness_port_answers()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            var ready = false;
            var service = Service("api", [TestKit.HarnessPath(), "sleeper"], port: 6001,
                readiness: new ReadinessConfig(6001, TimeSpan.FromSeconds(10)), workDir: cwd);
            await using var sup = new ServiceSupervisor(
                [service], "m1", TimeProvider.System, probe: (_, _) => Task.FromResult(ready));
            sup.Start();

            // Until the port answers it is starting, not running — which is exactly the
            // distinction a holder task needs before it calls register_service (§8.2).
            Assert.True(await TestKit.WaitUntilAsync(
                () => sup.Report().Single().State == ServiceState.Starting, TimeSpan.FromSeconds(10)));
            Assert.False(sup.IsServiceOnPort(6001));

            ready = true;
            Assert.True(await TestKit.WaitUntilAsync(
                () => sup.Report().Single().State == ServiceState.Running, TimeSpan.FromSeconds(10)));
            Assert.True(sup.IsServiceOnPort(6001));

            var status = sup.Report().Single();
            Assert.Equal("api", status.Name);
            Assert.Equal(6001, status.Port);
            Assert.NotNull(status.StartedAt);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task IsServiceOnPort_says_nothing_about_a_port_it_does_not_declare()
    {
        // The null answer is load-bearing for refuse-at-dial: an undeclared port may be
        // a worker-started listener, and refusing that dial would break §8.2 forwards
        // that work today.
        await using var sup = new ServiceSupervisor(
            [Service("api", ["/bin/echo"], port: 6002)], "m1", TimeProvider.System);

        Assert.Null(sup.IsServiceOnPort(9999));
        Assert.False(sup.IsServiceOnPort(6002)); // declared, not started → down, not unknown
    }

    [Fact]
    public async Task A_service_that_exits_is_reported_failed_and_restarted()
    {
        var clock = new FakeTimeProvider();
        // Exits immediately every time: the supervisor should keep restarting it and the
        // restart count should climb, with the failure visible rather than silent.
        var service = Service("flaky", ["/bin/sh", "-c", "exit 3"]);
        await using var sup = new ServiceSupervisor([service], "m1", clock);
        sup.Start();

        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.Report().Single().State == ServiceState.Failed, TimeSpan.FromSeconds(10)));
        Assert.Equal(3, sup.Report().Single().LastExitCode);
        Assert.NotNull(sup.Report().Single().LastFailureAt);

        // Backoff runs on the injected clock, so the retry is deterministic.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.Report().Single().Restarts >= 1, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_service_that_cannot_start_is_reported_failed_not_crashed()
    {
        var clock = new FakeTimeProvider();
        await using var sup = new ServiceSupervisor(
            [Service("missing", ["/definitely/not/a/binary"])], "m1", clock);
        sup.Start();

        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.Report().Single().State == ServiceState.Failed, TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// The tagging that makes restart-equals-reboot cover services, proven from the
    /// child's own environment: machine id present so the restart sweep reaps the
    /// previous generation, task id ABSENT so per-task exit cleanup steps over it.
    /// </summary>
    [Fact]
    public async Task A_service_carries_the_machine_id_and_no_task_id()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            var service = Service("tagged", [TestKit.HarnessPath(), "echo-env"], workDir: cwd);
            await using var sup = new ServiceSupervisor([service], "machine-xyz", TimeProvider.System);
            sup.Start();

            var envFile = Path.Combine(cwd, "env");
            Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(envFile), TimeSpan.FromSeconds(15)));
            var env = TestKit.ReadLinesShared(envFile);

            Assert.Contains("DOCKET_MACHINE_ID=machine-xyz", env);
            Assert.DoesNotContain(env, line => line.StartsWith("DOCKET_TASK_ID=", StringComparison.Ordinal));
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task Declared_env_reaches_the_service()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            var service = Service(
                "env-svc", [TestKit.HarnessPath(), "echo-env"], workDir: cwd,
                env: new Dictionary<string, string>(StringComparer.Ordinal) { ["PORT"] = "5173" });
            await using var sup = new ServiceSupervisor([service], "m1", TimeProvider.System);
            sup.Start();

            var envFile = Path.Combine(cwd, "env");
            Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(envFile), TimeSpan.FromSeconds(15)));
            Assert.Contains("PORT=5173", TestKit.ReadLinesShared(envFile));
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public void An_empty_report_and_a_null_report_are_different_answers()
    {
        // Null on the heartbeat means "this machine says nothing about services" (an
        // older runner); an empty list means "declares none". The plane must not have to
        // guess which, so a supervisor that exists always reports a list.
        var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
        Assert.Empty(sup.Report());
    }

    [Fact]
    public async Task KillAll_stops_every_supervised_service()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            var service = Service("api", [TestKit.HarnessPath(), "sleeper"], port: 6003,
                readiness: new ReadinessConfig(6003, TimeSpan.FromSeconds(10)), workDir: cwd);
            var sup = new ServiceSupervisor(
                [service], "m1", TimeProvider.System, probe: (_, _) => Task.FromResult(true));
            sup.Start();
            Assert.True(await TestKit.WaitUntilAsync(
                () => sup.Report().Single().State == ServiceState.Running, TimeSpan.FromSeconds(15)));

            await sup.DisposeAsync();
            Assert.Equal(ServiceState.Stopped, sup.Report().Single().State);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }
}
