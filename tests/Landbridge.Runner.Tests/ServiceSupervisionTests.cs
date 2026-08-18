using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Runner.Tests;

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
      "machine": { "work_root": "/tmp/landbridged-fake" },
      "profiles": [ { "name": "default", "prompt": "go", "spawn": ["noop"] } ],
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
        { "machine": { "work_root": "/w" }, "profiles": [ { "name": "default", "prompt": "go", "spawn": ["x"] } ] }
        """);

        Assert.Empty(config.DeclaredServices);
    }

    /// <summary>
    /// The name validator is a security control, not hygiene: the name becomes a
    /// directory under the state dir, occupying the slot a SessionId Guid fills for
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
            $$"""[ { "name": "{{new string('a', 65)}}", "prompt": "go", "spawn": ["x"] } ]""")));
        Assert.Contains(ex.Errors, e => e.Contains("1-64 characters", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("web-dev")]
    [InlineData("web_dev_2")]
    [InlineData("API")]
    public void An_ordinary_service_name_is_accepted(string name)
    {
        var config = RunnerConfig.Load(Config($$"""[ { "name": "{{name}}", "prompt": "go", "spawn": ["x"] } ]"""));
        Assert.Equal(name, Assert.Single(config.DeclaredServices).Name);
    }

    [Fact]
    public void An_unimplemented_backend_fails_loudly_rather_than_being_ignored()
    {
        // The key exists precisely so a config asking for delegation is refused instead
        // of silently supervised the other way (§10).
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """[ { "name": "api", "prompt": "go", "spawn": ["x"], "backend": "systemd-run" } ]""")));

        Assert.Contains(ex.Errors, e => e.Contains("backend 'systemd-run'", StringComparison.Ordinal)
                                        && e.Contains("not implemented", StringComparison.Ordinal));
    }

    [Fact]
    public void The_direct_backend_is_accepted_explicitly()
    {
        var config = RunnerConfig.Load(Config(
            """[ { "name": "api", "prompt": "go", "spawn": ["x"], "backend": "direct" } ]"""));
        Assert.Single(config.DeclaredServices);
    }

    [Fact]
    public void Duplicate_names_and_empty_spawn_are_refused()
    {
        var dup = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """[ { "name": "api", "prompt": "go", "spawn": ["x"] }, { "name": "api", "prompt": "go", "spawn": ["y"] } ]""")));
        Assert.Contains(dup.Errors, e => e.Contains("duplicate service name", StringComparison.Ordinal));

        var empty = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """[ { "name": "api", "prompt": "go", "spawn": [] } ]""")));
        Assert.Contains(empty.Errors, e => e.Contains("empty spawn argv", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_services_claiming_one_port_are_refused_with_both_names()
    {
        // Refuse-at-dial resolves a dial target to a service BY PORT, so a shared port
        // would make that lookup answer for whichever it found first — and a dial refused
        // on that basis is unexplainable from outside. Both names appear in the message so
        // the operator does not have to diff the config to find the other one.
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """
            [ { "name": "api", "prompt": "go", "spawn": ["x"], "port": 5173 },
              { "name": "web", "prompt": "go", "spawn": ["y"], "port": 5173 } ]
            """)));

        var problem = Assert.Single(ex.Errors);
        Assert.Contains("'api'", problem, StringComparison.Ordinal);
        Assert.Contains("'web'", problem, StringComparison.Ordinal);
        Assert.Contains("5173", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_readiness_port_collides_the_same_way_when_it_is_the_effective_port()
    {
        // With no explicit `port`, readiness.tcp_port IS what IsServiceOnPort resolves, so
        // the collision is real even though neither service declares `port`.
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """
            [ { "name": "api", "spawn": ["x"], "readiness": { "tcp_port": 9001 } },
              { "name": "web", "prompt": "go", "spawn": ["y"], "port": 9001 } ]
            """)));

        Assert.Contains(ex.Errors, e => e.Contains("both claim port 9001", StringComparison.Ordinal));
    }

    [Fact]
    public void One_service_may_name_the_same_port_twice_and_ports_may_be_omitted()
    {
        // `port` and `readiness.tcp_port` agreeing within ONE service is the normal case,
        // not a collision. And several services with no port at all cannot collide —
        // nothing dials them, so they never reach the port lookup.
        var config = RunnerConfig.Load(Config(
            """
            [ { "name": "api", "spawn": ["x"], "port": 5173, "readiness": { "tcp_port": 5173 } },
              { "name": "worker-a", "prompt": "go", "spawn": ["y"] },
              { "name": "worker-b", "prompt": "go", "spawn": ["z"] } ]
            """));

        Assert.Equal(3, config.DeclaredServices.Count);
    }

    [Fact]
    public void An_out_of_range_port_is_refused()
    {
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """[ { "name": "api", "prompt": "go", "spawn": ["x"], "port": 70000 } ]""")));
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
            new LogsConfig());

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
            [Service("api", [TestKit.HarnessPath()], port: 6002)], "m1", TimeProvider.System);

        Assert.Null(sup.IsServiceOnPort(9999));
        Assert.False(sup.IsServiceOnPort(6002)); // declared, not started → down, not unknown
    }

    [Fact]
    public async Task A_service_that_exits_is_reported_failed_and_restarted()
    {
        var clock = new FakeTimeProvider();
        // Exits immediately every time: the supervisor should keep restarting it and the
        // restart count should climb, with the failure visible rather than silent.
        //
        // Spawns the test harness rather than `/bin/sh -c "exit 3"`, which is a POSIX-only
        // command: on Windows that process cannot start at all, so it never runs and there
        // is no exit code to report — the assertion would be measuring a failed spawn, not
        // a failed run. Those are different states and this test is about the second one.
        var service = Service(
            "flaky",
            [TestKit.HarnessPath(), "exit-code", TestHarness.Program.ServiceExitCode.ToString()]);
        await using var sup = new ServiceSupervisor([service], "m1", clock);
        sup.Start();

        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.Report().Single().State == ServiceState.Failed, TimeSpan.FromSeconds(10)));
        Assert.Equal(TestHarness.Program.ServiceExitCode, sup.Report().Single().LastExitCode);
        Assert.NotNull(sup.Report().Single().LastFailureAt);

        // Backoff runs on the injected clock, so the retry is deterministic.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.Report().Single().Restarts >= 1, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_service_that_cannot_start_reports_failed_with_no_exit_code()
    {
        // The counterpart to the test above, and the distinction that matters: a process
        // that never STARTED has no exit code, so LastExitCode is null — not 0, which would
        // render on the dashboard as a clean exit and make a broken command look healthy.
        // "Exited with code N" and "never ran" are different failures and stay distinguishable.
        var clock = new FakeTimeProvider();
        await using var sup = new ServiceSupervisor(
            [Service("missing", ["/definitely/not/a/binary"])], "m1", clock);
        sup.Start();

        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.Report().Single().State == ServiceState.Failed, TimeSpan.FromSeconds(10)));
        Assert.Null(sup.Report().Single().LastExitCode);
        Assert.NotNull(sup.Report().Single().LastFailureAt);
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

            Assert.Contains("LANDBRIDGE_MACHINE_ID=machine-xyz", env);
            Assert.DoesNotContain(env, line => line.StartsWith("LANDBRIDGE_SESSION_ID=", StringComparison.Ordinal));
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
    public void Enabled_defaults_to_true_and_can_be_declared_off()
    {
        var config = RunnerConfig.Load(Config(
            """[ { "name": "on", "prompt": "go", "spawn": ["x"] }, { "name": "off", "prompt": "go", "spawn": ["y"], "enabled": false } ]"""));

        Assert.True(config.DeclaredServices.Single(x => x.Name == "on").Enabled);
        Assert.False(config.DeclaredServices.Single(x => x.Name == "off").Enabled);
    }

    [Fact]
    public async Task A_disabled_service_is_never_started_and_reads_as_disabled()
    {
        // `enabled: false` is the honest stop: desired state stays in config, so there is
        // nothing for a restart to silently undo — unlike a dashboard stop button, which
        // would need landbridged to persist state it deliberately does not keep.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            var service = Service("off", [TestKit.HarnessPath(), "sleeper"], workDir: cwd) with { Enabled = false };
            await using var sup = new ServiceSupervisor([service], "m1", TimeProvider.System);
            sup.Start();

            // Give a would-be spawn ample time to leave the marker `sleeper` writes.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(File.Exists(Path.Combine(cwd, "ready")), "a disabled service was started");
            Assert.Equal(ServiceState.Disabled, sup.Report().Single().State);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_disabled_service_is_still_declared_for_refuse_at_dial()
    {
        // Deliberately off is still "declared and not running", so a dial for its port is
        // refused rather than landing on whatever else has taken it.
        await using var sup = new ServiceSupervisor(
            [Service("off", [TestKit.HarnessPath()], port: 6100) with { Enabled = false }],
            "m1", TimeProvider.System);

        Assert.False(sup.IsServiceOnPort(6100));
    }

    // ── §10 agent-started processes ─────────────────────────────────────────────

    private static ProfileConfig ProfileWith(bool agentInitiated, int cap = 8) =>
        RunnerConfig.Load($$"""
        {
          "machine": { "work_root": "/tmp/landbridged-fake" },
          "profiles": [ { "name": "default", "spawn": ["noop"], "prompt": "go",
            "processes": { "agent_initiated": {{(agentInitiated ? "true" : "false")}},
                          "max": {{cap}} } } ]
        }
        """).Profiles["default"];

    private static StartProcessCommand Ask(
        string name, IReadOnlyList<string>? spawn = null, string? cwd = null, bool openStdin = false) =>
        new(SessionId.New(), "req-1", name, spawn ?? [TestKit.HarnessPath(), "sleeper"],
            WorkingDirectory: cwd, Env: null, OpenStdin: openStdin);

    [Fact]
    public async Task A_profile_that_does_not_permit_processes_refuses()
    {
        await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
        var outcome = await sup.StartProcessAsync(
            Ask("dev"), ProfileWith(agentInitiated: false), CancellationToken.None);

        var refused = Assert.IsType<ProcessOutcome.RefusedOutcome>(outcome);
        Assert.Equal(ProcessRefusals.ProfileNotPermitted, refused.Refusal);
        Assert.Contains("default", refused.Detail, StringComparison.Ordinal);
        Assert.Empty(sup.ReportProcesses());
    }

    [Fact]
    public async Task A_name_off_the_wire_faces_the_same_validator_as_a_config_name()
    {
        await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
        Assert.Equal(
            ProcessRefusals.InvalidName,
            Assert.IsType<ProcessOutcome.RefusedOutcome>(
                await sup.StartProcessAsync(Ask("../escape"), ProfileWith(true), CancellationToken.None)).Refusal);
    }

    [Fact]
    public async Task A_process_runs_once_its_os_process_is_up_and_declares_no_port()
    {
        // Ports are out of scope for a process (§10): this is a process manager, and reachability
        // is §8.2's noun. "Running" therefore means the OS process is up, full stop.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            Assert.IsType<ProcessOutcome.StartedOk>(
                await sup.StartProcessAsync(Ask("watcher", cwd: cwd), ProfileWith(true), CancellationToken.None));

            // Reported as a PROCESS, never mixed into the services list.
            var reported = Assert.Single(sup.ReportProcesses());
            Assert.Equal(ServiceState.Running, reported.State);
            Assert.Empty(sup.Report());
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_process_is_invisible_to_refuse_at_dial_whatever_it_listens_on()
    {
        // If a process happens to bind something, that is the agent's business — Landbridge tracks no
        // port for it, so it must never answer for one. Otherwise stopping a process could start
        // refusing dials for a listener Landbridge never knew about.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            await sup.StartProcessAsync(Ask("server", cwd: cwd), ProfileWith(true), CancellationToken.None);

            Assert.Null(sup.IsServiceOnPort(5173));
            Assert.Null(sup.IsServiceOnPort(0));
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_process_that_exits_is_recorded_and_NOT_restarted()
    {
        // The whole point of a process rather than a service: a crash is information, not
        // something to hide behind a backoff ladder. The exit code rests in the report.
        await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
        Assert.IsType<ProcessOutcome.StartedOk>(await sup.StartProcessAsync(
            Ask("job", spawn: [TestKit.HarnessPath(), "exit-code", "3"]),
            ProfileWith(true), CancellationToken.None));

        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.ReportProcesses().Single().State == ServiceState.Exited, TimeSpan.FromSeconds(10)));
        var reported = sup.ReportProcesses().Single();
        Assert.Equal(TestHarness.Program.ServiceExitCode, reported.ExitCode);
        Assert.NotNull(reported.ExitedAt);

        // Still exited a moment later: nothing revives it.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.Equal(ServiceState.Exited, sup.ReportProcesses().Single().State);
    }

    [Fact]
    public async Task A_name_already_held_is_refused_and_says_which_kind_holds_it()
    {
        // One namespace across processes AND services, so a clash is always reported.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor(
                [Service("fixture", [TestKit.HarnessPath(), "sleeper"], workDir: cwd)],
                "m1", TimeProvider.System);

            var clash = Assert.IsType<ProcessOutcome.RefusedOutcome>(
                await sup.StartProcessAsync(Ask("fixture", cwd: cwd), ProfileWith(true), CancellationToken.None));
            Assert.Equal(ProcessRefusals.NameTaken, clash.Refusal);
            Assert.Contains("service", clash.Detail, StringComparison.Ordinal);

            await sup.StartProcessAsync(Ask("mine", cwd: cwd), ProfileWith(true), CancellationToken.None);
            var again = Assert.IsType<ProcessOutcome.RefusedOutcome>(
                await sup.StartProcessAsync(Ask("mine", cwd: cwd), ProfileWith(true), CancellationToken.None));
            Assert.Equal(ProcessRefusals.NameTaken, again.Refusal);
            Assert.Contains("process", again.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task Several_processes_coexist_and_the_cap_bounds_them()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            var profile = ProfileWith(true, cap: 2);
            Assert.IsType<ProcessOutcome.StartedOk>(
                await sup.StartProcessAsync(Ask("one", cwd: cwd), profile, CancellationToken.None));
            Assert.IsType<ProcessOutcome.StartedOk>(
                await sup.StartProcessAsync(Ask("two", cwd: cwd), profile, CancellationToken.None));

            var refused = Assert.IsType<ProcessOutcome.RefusedOutcome>(
                await sup.StartProcessAsync(Ask("three", cwd: cwd), profile, CancellationToken.None));
            Assert.Equal(ProcessRefusals.CapReached, refused.Refusal);
            Assert.Contains("max 2", refused.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_process_that_cannot_start_is_refused_and_leaves_nothing_behind()
    {
        await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
        Assert.Equal(
            ProcessRefusals.SpawnFailed,
            Assert.IsType<ProcessOutcome.RefusedOutcome>(await sup.StartProcessAsync(
                Ask("broken", spawn: ["/definitely/not/a/binary"]),
                ProfileWith(true), CancellationToken.None)).Refusal);
        // Otherwise the cap and the name/port tables would drift against reality.
        Assert.Empty(sup.ReportProcesses());
    }

    [Fact]
    public async Task Any_task_may_stop_a_process_including_one_another_task_started()
    {
        // Machine-scoped on purpose: the agent sent to clean up is a CONTINUATION with a new
        // task id, so a task-scoped stop would be unusable by the very worker dispatched to tidy.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            await sup.StartProcessAsync(Ask("dev", cwd: cwd), ProfileWith(true), CancellationToken.None);
            Assert.Single(sup.ReportProcesses());

            // A different task id — exactly what a Lead's cleanup continuation carries.
            Assert.IsType<ProcessOutcome.StoppedOk>(await sup.StopProcessAsync("dev", CancellationToken.None));
            Assert.Empty(sup.ReportProcesses());

            Assert.Equal(
                ProcessRefusals.NoSuchProcess,
                Assert.IsType<ProcessOutcome.RefusedOutcome>(await sup.StopProcessAsync("dev", CancellationToken.None)).Refusal);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task An_exited_process_releases_its_name()
    {
        // Uniqueness is among LIVE entries. A corpse must not block a retry — otherwise a
        // resumed agent re-running the same job would be stuck on a name it already owns.
        await using var sup = new ServiceSupervisor(
            [], "m1", TimeProvider.System, probe: (_, _) => Task.FromResult(true));
        var profile = ProfileWith(true, cap: 1);

        Assert.IsType<ProcessOutcome.StartedOk>(await sup.StartProcessAsync(
            Ask("job", spawn: [TestKit.HarnessPath(), "exit-code", "3"]), profile, CancellationToken.None));
        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.ReportProcesses().Single().State == ServiceState.Exited, TimeSpan.FromSeconds(10)));

        // Same name, and a cap of 1 that the corpse must not be counted against.
        Assert.IsType<ProcessOutcome.StartedOk>(await sup.StartProcessAsync(
            Ask("job", spawn: [TestKit.HarnessPath(), "exit-code", "3"]), profile, CancellationToken.None));
    }

    [Fact]
    public async Task Stop_refuses_to_touch_a_config_declared_service()
    {
        // An operator fixture is not an agent's to stop; only processes are.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor(
                [Service("fixture", [TestKit.HarnessPath(), "sleeper"], workDir: cwd)],
                "m1", TimeProvider.System);
            sup.Start();

            Assert.Equal(
                ProcessRefusals.NoSuchProcess,
                Assert.IsType<ProcessOutcome.RefusedOutcome>(await sup.StopProcessAsync("fixture", CancellationToken.None)).Refusal);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_write_reaches_the_process_stdin_and_shows_up_in_its_log()
    {
        // The pipe carries no reply channel, so the answer arrives in the log — which is the
        // interaction loop the tool documents: write, read the log, decide.
        var cwd = TestKit.NewWorkRoot();
        var state = TestKit.NewWorkRoot();
        try
        {
            var logs = new ServiceLogStore(Path.Combine(state, ServiceLogStore.DirName));
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System, logs: logs);
            // Opt in explicitly: stdin is closed by default, so write_process only works for a
            // process whose starter asked for a pipe.
            Assert.IsType<ProcessOutcome.StartedOk>(await sup.StartProcessAsync(
                Ask("repl", spawn: [TestKit.HarnessPath(), "echo-stdin"], cwd: cwd, openStdin: true),
                ProfileWith(true), CancellationToken.None));

            var written = Assert.IsType<ProcessOutcome.WrittenOk>(
                await sup.WriteProcessAsync("repl", "hello", appendNewline: true, CancellationToken.None));
            Assert.Equal(6, written.Bytes); // "hello" + the newline it appended

            var logDir = Path.Combine(logs.Root, "repl");
            Assert.True(await TestKit.WaitUntilAsync(
                () => Directory.Exists(logDir)
                      && Directory.GetFiles(logDir, "*.ndjson")
                          .Any(f => TestKit.ReadLinesShared(f).Any(l => l.Contains("got: hello", StringComparison.Ordinal))),
                TimeSpan.FromSeconds(15)));
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
            TestKit.TryDeleteRoot(state);
        }
    }

    [Fact]
    public async Task A_process_started_without_stdin_refuses_writes_with_its_own_cause()
    {
        // The DEFAULT path, so this is the refusal an agent is most likely to meet. It must say
        // "no pipe was opened", not "no such process" and not "broken pipe": the caller needs to
        // learn writing required asking for stdin at start, so it restarts the process differently
        // rather than hunting for a typo.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            // No flag at all — the default is closed.
            Assert.IsType<ProcessOutcome.StartedOk>(await sup.StartProcessAsync(
                Ask("quiet", cwd: cwd), ProfileWith(true), CancellationToken.None));

            var refused = Assert.IsType<ProcessOutcome.RefusedOutcome>(
                await sup.WriteProcessAsync("quiet", "hello", true, CancellationToken.None));
            Assert.Equal(ProcessRefusals.StdinNotOpened, refused.Refusal);
            Assert.Contains("without a stdin pipe", refused.Detail, StringComparison.Ordinal);

            // And the mode is reported, so a cleanup agent knows there is no graceful stop.
            Assert.False(sup.ReportProcesses().Single().StdinOpen);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_process_without_stdin_still_stops_and_asking_for_stdin_is_opt_in()
    {
        // Without stdin there is no EOF lever, so stop is the bounded wait and then the tree. That
        // is the default path and it must still work.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            await sup.StartProcessAsync(Ask("hard", cwd: cwd), ProfileWith(true), CancellationToken.None);
            Assert.False(sup.ReportProcesses().Single().StdinOpen); // the default
            Assert.IsType<ProcessOutcome.StoppedOk>(await sup.StopProcessAsync("hard", CancellationToken.None));
            Assert.Empty(sup.ReportProcesses());

            // Asking for stdin is what makes write_process and a graceful stop available.
            await sup.StartProcessAsync(Ask("chatty", cwd: cwd, openStdin: true), ProfileWith(true), CancellationToken.None);
            Assert.True(sup.ReportProcesses().Single().StdinOpen);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_write_is_refused_for_an_unknown_process_and_for_an_oversized_payload()
    {
        await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);

        Assert.Equal(
            ProcessRefusals.NoSuchProcess,
            Assert.IsType<ProcessOutcome.RefusedOutcome>(
                await sup.WriteProcessAsync("ghost", "x", true, CancellationToken.None)).Refusal);

        // Checked before the lookup: a caller that is over the cap learns that, rather than
        // learning about the name first and the size on a later attempt.
        var refused = Assert.IsType<ProcessOutcome.RefusedOutcome>(await sup.WriteProcessAsync(
            "ghost", new string('x', ProcessStdin.MaxBytes + 1), true, CancellationToken.None));
        Assert.Equal(ProcessRefusals.PayloadTooLarge, refused.Refusal);
    }

    [Fact]
    public async Task A_process_carries_the_machine_id_and_never_a_task_id()
    {
        // The tagging is what binds lifetime, and a task id here would be fatal: the per-task
        // exit sweep would kill the process the moment its declaring worker's turn ended —
        // exactly the loss this feature exists to prevent.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "machine-xyz", TimeProvider.System);
            await sup.StartProcessAsync(
                Ask("tagged", spawn: [TestKit.HarnessPath(), "echo-env"], cwd: cwd),
                ProfileWith(true), CancellationToken.None);

            var envFile = Path.Combine(cwd, "env");
            Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(envFile), TimeSpan.FromSeconds(15)));
            var env = TestKit.ReadLinesShared(envFile);
            Assert.Contains("LANDBRIDGE_MACHINE_ID=machine-xyz", env);
            Assert.DoesNotContain(env, l => l.StartsWith("LANDBRIDGE_SESSION_ID=", StringComparison.Ordinal));
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
