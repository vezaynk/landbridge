using Docket.Contracts;
using Docket.Core;
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
    public void Two_services_claiming_one_port_are_refused_with_both_names()
    {
        // Refuse-at-dial resolves a dial target to a service BY PORT, so a shared port
        // would make that lookup answer for whichever it found first — and a dial refused
        // on that basis is unexplainable from outside. Both names appear in the message so
        // the operator does not have to diff the config to find the other one.
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config(
            """
            [ { "name": "api", "spawn": ["x"], "port": 5173 },
              { "name": "web", "spawn": ["y"], "port": 5173 } ]
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
              { "name": "web", "spawn": ["y"], "port": 9001 } ]
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
              { "name": "worker-a", "spawn": ["y"] },
              { "name": "worker-b", "spawn": ["z"] } ]
            """));

        Assert.Equal(3, config.DeclaredServices.Count);
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
    public void Enabled_defaults_to_true_and_can_be_declared_off()
    {
        var config = RunnerConfig.Load(Config(
            """[ { "name": "on", "spawn": ["x"] }, { "name": "off", "spawn": ["y"], "enabled": false } ]"""));

        Assert.True(config.DeclaredServices.Single(x => x.Name == "on").Enabled);
        Assert.False(config.DeclaredServices.Single(x => x.Name == "off").Enabled);
    }

    [Fact]
    public async Task A_disabled_service_is_never_started_and_reads_as_disabled()
    {
        // `enabled: false` is the honest stop: desired state stays in config, so there is
        // nothing for a restart to silently undo — unlike a dashboard stop button, which
        // would need docketd to persist state it deliberately does not keep.
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

    // ── §10 agent-initiated services ────────────────────────────────────────────

    private static ProfileConfig ProfileWith(bool agentInitiated, int cap = 8) =>
        RunnerConfig.Load($$"""
        {
          "machine": { "work_root": "/tmp/docketd-fake" },
          "profiles": [ { "name": "default", "spawn": ["noop"],
            "services": { "agent_initiated": {{(agentInitiated ? "true" : "false")}},
                          "max_agent_initiated": {{cap}} } } ]
        }
        """).Default;

    private static StartServiceCommand Ask(
        TaskId task, string name, IReadOnlyList<string>? spawn = null,
        int? port = null, int? readiness = null) =>
        new(task, "req-1", name, spawn ?? [TestKit.HarnessPath(), "sleeper"],
            WorkingDirectory: null, Env: null, Port: port, ReadinessTcpPort: readiness);

    [Fact]
    public async Task A_profile_that_does_not_permit_agent_services_refuses()
    {
        // Off by default, and the refusal names the profile — an operator reading it knows
        // exactly which declaration to change.
        await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
        var outcome = await sup.StartForTaskAsync(
            Ask(TaskId.New(), "dev"), ProfileWith(agentInitiated: false), CancellationToken.None);

        var refused = Assert.IsType<ServiceStartOutcome.RefusedOutcome>(outcome);
        Assert.Equal(ServiceRefusals.ProfileNotPermitted, refused.Refusal);
        Assert.Contains("default", refused.Detail, StringComparison.Ordinal);
        Assert.Empty(sup.Report());
    }

    [Fact]
    public async Task A_name_off_the_wire_faces_the_same_validator_as_a_config_name()
    {
        // The validator was written as a security control for exactly this case: here the
        // name is agent-supplied, and it becomes a directory under the state dir.
        await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
        var outcome = await sup.StartForTaskAsync(
            Ask(TaskId.New(), "../escape"), ProfileWith(true), CancellationToken.None);

        Assert.Equal(
            ServiceRefusals.InvalidName,
            Assert.IsType<ServiceStartOutcome.RefusedOutcome>(outcome).Refusal);
    }

    [Fact]
    public async Task A_port_less_service_starts_and_is_running_once_its_process_is_up()
    {
        // The shape the user actually asked for: a watcher or indexer with no listener.
        // "Running" means the process is up — no timer pretending to be a readiness check.
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            var cmd = Ask(TaskId.New(), "watcher") with { WorkingDirectory = cwd };
            var outcome = await sup.StartForTaskAsync(cmd, ProfileWith(true), CancellationToken.None);

            var ok = Assert.IsType<ServiceStartOutcome.StartedOk>(outcome);
            Assert.Null(ok.Port);
            Assert.Equal(ServiceState.Running, sup.Report().Single().State);
            // Port-less means invisible to refuse-at-dial: it declares nothing dialable.
            Assert.Null(sup.IsServiceOnPort(0));
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task An_agent_service_dies_with_its_declaring_task()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            var task = TaskId.New();
            var cmd = Ask(task, "dev") with { WorkingDirectory = cwd };
            Assert.IsType<ServiceStartOutcome.StartedOk>(
                await sup.StartForTaskAsync(cmd, ProfileWith(true), CancellationToken.None));
            Assert.Single(sup.Report());

            // The lifetime binding: no plane command, no config to re-read — the task ending
            // is the whole signal, and this is the teardown docketd owns rather than leaving
            // to the reaper's env scan (which finds nothing on Windows by design).
            Assert.Equal(1, sup.StopForTask(task));
            Assert.Empty(sup.Report());

            // And it does not come back: a service whose owner is gone must not be restarted.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.Empty(sup.Report());
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task Stopping_one_task_leaves_another_tasks_service_alone()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            var mine = TaskId.New();
            var theirs = TaskId.New();
            // Same NAME on both: identity is {task, name}, so this is not a collision.
            await sup.StartForTaskAsync(
                Ask(mine, "dev") with { WorkingDirectory = cwd }, ProfileWith(true), CancellationToken.None);
            await sup.StartForTaskAsync(
                Ask(theirs, "dev") with { WorkingDirectory = cwd }, ProfileWith(true), CancellationToken.None);
            Assert.Equal(2, sup.Report().Count);

            Assert.Equal(1, sup.StopForTask(mine));
            Assert.Single(sup.Report());
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task The_same_task_redeclaring_a_name_reclaims_rather_than_being_refused()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            var task = TaskId.New();
            var cmd = Ask(task, "dev") with { WorkingDirectory = cwd };
            Assert.IsType<ServiceStartOutcome.StartedOk>(
                await sup.StartForTaskAsync(cmd, ProfileWith(true), CancellationToken.None));

            // Idempotent: a retrying worker gets its service back instead of being stuck.
            Assert.IsType<ServiceStartOutcome.ReclaimedOk>(
                await sup.StartForTaskAsync(cmd, ProfileWith(true), CancellationToken.None));
            Assert.Single(sup.Report());
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_different_task_claiming_a_held_port_is_refused_and_told_who_holds_it()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor(
                [], "m1", TimeProvider.System, probe: (_, _) => Task.FromResult(true));
            await sup.StartForTaskAsync(
                Ask(TaskId.New(), "api", port: 7301, readiness: 7301) with { WorkingDirectory = cwd },
                ProfileWith(true), CancellationToken.None);

            var outcome = await sup.StartForTaskAsync(
                Ask(TaskId.New(), "other", port: 7301) with { WorkingDirectory = cwd },
                ProfileWith(true), CancellationToken.None);

            var refused = Assert.IsType<ServiceStartOutcome.RefusedOutcome>(outcome);
            Assert.Equal(ServiceRefusals.PortTaken, refused.Refusal);
            Assert.Contains("'api'", refused.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task Port_less_services_never_collide()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            var task = TaskId.New();
            foreach (var name in new[] { "watch-a", "watch-b", "watch-c" })
            {
                Assert.IsType<ServiceStartOutcome.StartedOk>(await sup.StartForTaskAsync(
                    Ask(task, name) with { WorkingDirectory = cwd }, ProfileWith(true), CancellationToken.None));
            }

            Assert.Equal(3, sup.Report().Count);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task The_machine_cap_bounds_how_many_a_task_may_hold()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
            var task = TaskId.New();
            var profile = ProfileWith(true, cap: 2);
            await sup.StartForTaskAsync(Ask(task, "one") with { WorkingDirectory = cwd }, profile, CancellationToken.None);
            await sup.StartForTaskAsync(Ask(task, "two") with { WorkingDirectory = cwd }, profile, CancellationToken.None);

            var outcome = await sup.StartForTaskAsync(
                Ask(task, "three") with { WorkingDirectory = cwd }, profile, CancellationToken.None);

            var refused = Assert.IsType<ServiceStartOutcome.RefusedOutcome>(outcome);
            Assert.Equal(ServiceRefusals.CapReached, refused.Refusal);
            Assert.Contains("max_agent_initiated 2", refused.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
        }
    }

    [Fact]
    public async Task A_service_that_cannot_start_is_refused_not_left_half_registered()
    {
        await using var sup = new ServiceSupervisor([], "m1", TimeProvider.System);
        var outcome = await sup.StartForTaskAsync(
            Ask(TaskId.New(), "broken", spawn: ["/definitely/not/a/binary"]),
            ProfileWith(true), CancellationToken.None);

        Assert.Equal(
            ServiceRefusals.SpawnFailed,
            Assert.IsType<ServiceStartOutcome.RefusedOutcome>(outcome).Refusal);
        // A failed admission leaves nothing behind — otherwise the cap and the port table
        // would drift against reality.
        Assert.Empty(sup.Report());
    }

    [Fact]
    public async Task An_agent_service_carries_its_task_id_and_a_config_one_does_not()
    {
        // The tagging IS the lifetime policy: the task id is what makes the existing per-task
        // exit sweep reap this, and its absence is what makes a config service an operator fixture.
        var agentCwd = TestKit.NewWorkRoot();
        var configCwd = TestKit.NewWorkRoot();
        try
        {
            var task = TaskId.New();
            await using var sup = new ServiceSupervisor(
                [Service("fixture", [TestKit.HarnessPath(), "echo-env"], workDir: configCwd)],
                "machine-xyz", TimeProvider.System);
            sup.Start();

            await sup.StartForTaskAsync(
                Ask(task, "agentsvc", spawn: [TestKit.HarnessPath(), "echo-env"]) with { WorkingDirectory = agentCwd },
                ProfileWith(true), CancellationToken.None);

            var agentEnv = Path.Combine(agentCwd, "env");
            var configEnv = Path.Combine(configCwd, "env");
            Assert.True(await TestKit.WaitUntilAsync(
                () => File.Exists(agentEnv) && File.Exists(configEnv), TimeSpan.FromSeconds(15)));

            Assert.Contains($"DOCKET_TASK_ID={task}", TestKit.ReadLinesShared(agentEnv));
            Assert.DoesNotContain(
                TestKit.ReadLinesShared(configEnv),
                line => line.StartsWith("DOCKET_TASK_ID=", StringComparison.Ordinal));
        }
        finally
        {
            TestKit.TryDeleteRoot(agentCwd);
            TestKit.TryDeleteRoot(configCwd);
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
