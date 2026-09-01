using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.Runner.Tests;

/// <summary>
/// §10 agent-started processes, plus leftover <c>services[]</c> refusal.
/// Operator-declared fixtures are gone; long work is <c>start_process</c>.
/// </summary>
public class AgentProcessSupervisionTests
{
    private static string Config(string servicesJson) => $$"""
    {
      "machine": { "work_root": "/tmp/landbridged-fake" },
      "profiles": [ { "name": "default", "prompt": "go", "spawn": ["noop"] } ],
      "services": {{servicesJson}}
    }
    """;

    [Fact]
    public void A_config_with_no_services_section_is_normal()
    {
        var config = RunnerConfig.Load("""
        { "machine": { "work_root": "/w" }, "profiles": [ { "name": "default", "prompt": "go", "spawn": ["x"] } ] }
        """);
        Assert.NotNull(config);
    }

    [Fact]
    public void An_empty_services_array_is_normal()
    {
        var config = RunnerConfig.Load(Config("[]"));
        Assert.NotNull(config);
    }

    [Fact]
    public void A_leftover_services_block_is_refused()
    {
        var ex = Assert.Throws<RunnerConfigException>(() => RunnerConfig.Load(Config("""
        [ { "name": "web-dev", "spawn": ["/bin/echo", "hi"], "port": 5173 } ]
        """)));
        Assert.Contains(ex.Errors, e => e.Contains("services[] is gone", StringComparison.Ordinal));
    }

    [Fact]
    public void The_log_store_refuses_a_name_that_would_escape_its_root()
    {
        var root = TestKit.NewWorkRoot();
        try
        {
            var store = new ProcessLogStore(Path.Combine(root, ProcessLogStore.DirName));
            Assert.Throws<ArgumentException>(() => store.CreateWriter("../escape", 1024));
        }
        finally
        {
            TestKit.TryDeleteRoot(root);
        }
    }

    [Fact]
    public void Process_logs_live_outside_the_pruned_transcript_root()
    {
        var state = TestKit.NewWorkRoot();
        try
        {
            var transcripts = Path.Combine(state, TranscriptDefaults.DirName);
            var processes = Path.Combine(state, ProcessLogStore.DirName);
            Assert.NotEqual(transcripts, processes);

            using var writer = new ProcessLogStore(processes).CreateWriter("web-dev", 4096);
            writer.WriteStdoutLine("listening on 5173");
            writer.Dispose();

            new TranscriptStore(transcripts, TimeSpan.FromTicks(1), TimeProvider.System).Prune();
            Assert.True(Directory.Exists(Path.Combine(processes, "web-dev")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(processes, "web-dev")));
        }
        finally
        {
            TestKit.TryDeleteRoot(state);
        }
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
        await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
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
        await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
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
            await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
            Assert.IsType<ProcessOutcome.StartedOk>(
                await sup.StartProcessAsync(Ask("watcher", cwd: cwd), ProfileWith(true), CancellationToken.None));

            var reported = Assert.Single(sup.ReportProcesses());
            Assert.Equal(ProcessState.Running, reported.State);
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
        await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
        Assert.IsType<ProcessOutcome.StartedOk>(await sup.StartProcessAsync(
            Ask("job", spawn: [TestKit.HarnessPath(), "exit-code", "3"]),
            ProfileWith(true), CancellationToken.None));

        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.ReportProcesses().Single().State == ProcessState.Exited, TimeSpan.FromSeconds(10)));
        var reported = sup.ReportProcesses().Single();
        Assert.Equal(TestHarness.Program.ServiceExitCode, reported.ExitCode);
        Assert.NotNull(reported.ExitedAt);

        // Still exited a moment later: nothing revives it.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.Equal(ProcessState.Exited, sup.ReportProcesses().Single().State);
    }

    [Fact]
    public async Task A_name_already_held_is_refused()
    {
        var cwd = TestKit.NewWorkRoot();
        try
        {
            await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
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
            await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
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
        await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
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
            await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
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
        await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
        var profile = ProfileWith(true, cap: 1);

        Assert.IsType<ProcessOutcome.StartedOk>(await sup.StartProcessAsync(
            Ask("job", spawn: [TestKit.HarnessPath(), "exit-code", "3"]), profile, CancellationToken.None));
        Assert.True(await TestKit.WaitUntilAsync(
            () => sup.ReportProcesses().Single().State == ProcessState.Exited, TimeSpan.FromSeconds(10)));

        // Same name, and a cap of 1 that the corpse must not be counted against.
        Assert.IsType<ProcessOutcome.StartedOk>(await sup.StartProcessAsync(
            Ask("job", spawn: [TestKit.HarnessPath(), "exit-code", "3"]), profile, CancellationToken.None));
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
            var logs = new ProcessLogStore(Path.Combine(state, ProcessLogStore.DirName));
            await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System, logs: logs);
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
            await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
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
            await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);
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
        await using var sup = new AgentProcessSupervisor("m1", TimeProvider.System);

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
            await using var sup = new AgentProcessSupervisor("machine-xyz", TimeProvider.System);
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

}
