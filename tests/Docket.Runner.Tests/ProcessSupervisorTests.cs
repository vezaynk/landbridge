using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// Process supervision against real spawned children, spec §10. No shell,
/// argv only; timers driven by <see cref="FakeTimeProvider"/> for determinism.
/// </summary>
public sealed class ProcessSupervisorTests : IDisposable
{
    private readonly string _workRoot = TestKit.NewWorkRoot();
    private readonly FakeTimeProvider _clock = new();
    private readonly OutboundEventRing _ring = new(capacity: 256);
    private ProcessSupervisor? _supervisor;

    private ProcessSupervisor Supervisor() =>
        _supervisor ??= new ProcessSupervisor(TestKit.Machine(_workRoot), _ring, _clock);

    private async Task<List<RunnerEvent>> DrainedEventsAsync()
    {
        _ring.Complete();
        var events = new List<RunnerEvent>();
        await foreach (var item in _ring.ReadAllAsync(CancellationToken.None))
            events.Add(item.Event);
        return events;
    }

    [Fact]
    public async Task Spawn_starts_the_harness_injects_env_and_uses_the_task_work_dir()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "machine-42");

        Assert.Equal(1, supervisor.RunningTotal);
        Assert.Equal(1, supervisor.RunningFor("default"));

        // §10: spawned into {work_root}/{task_id} with DOCKET_* injected. The
        // harness writes those into a marker in its own working directory.
        var marker = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(15)),
            "harness never wrote its started marker");

        var lines = await File.ReadAllLinesAsync(marker);
        Assert.Equal(task.ToString(), lines[0]);
        Assert.Equal("machine-42", lines[1]);

        Assert.True(supervisor.Kill(task));
    }

    [Fact]
    public async Task Spawn_emits_a_started_event()
    {
        var task = TaskId.New();
        Supervisor().Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "m");
        Supervisor().Kill(task);

        var events = await DrainedEventsAsync();
        Assert.Contains(events, e => e is StartedEvent s && s.Task == task);
    }

    [Fact]
    public async Task Stop_message_mode_reaches_the_agent_as_a_turn_and_it_winds_down_without_being_killed()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("stdin-stop", StopMode.Message), "m");
        // Hold the supervised handle so its exit code is still readable after OnExited
        // removes it from the running set (below).
        supervisor.TryGet(task, out var supervised);

        // Wait for the harness to be reading stdin.
        var marker = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(15)));

        var ack = await supervisor.StopAsync(task, TimeSpan.FromSeconds(30), StopDisposition.Preserve, "wind down", CancellationToken.None);

        // §10: stop was delivered as an injected message turn, not a signal.
        Assert.True(ack.Delivered);
        Assert.Equal(StopDelivery.Message, ack.Delivery);

        var stopped = Path.Combine(_workRoot, task.ToString(), "stopped");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(stopped), TimeSpan.FromSeconds(15)),
            "harness did not wind down from the injected stop turn");
        Assert.True(await TestKit.WaitUntilAsync(() => !supervised.ProcessAlive, TimeSpan.FromSeconds(15)));

        // §11: it exited on its own from the injected turn (exit 0), never hard-killed
        // — the FakeTimeProvider wind-down timer was never advanced, so a voluntary
        // graceful exit is the only way it is gone. A tree-kill would surface a
        // non-zero (signalled) exit code.
        Assert.Equal(0, supervised.Process.ExitCode);
        Assert.Equal(0, supervisor.RunningTotal);
    }

    [Fact]
    public async Task Stop_message_mode_hard_kills_at_the_wind_down_deadline_when_the_agent_ignores_it()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();
        // "run" reads stdin but ignores injected lines, so the wind-down turn is not
        // honoured and the min(ttl, wind_down) backstop must fire. wind_down (30s) is
        // deliberately shorter than the TTL (120s): the kill keying off wind_down —
        // not the TTL — is exactly what this asserts (§10, §11).
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("run", StopMode.Message, windDown: TimeSpan.FromSeconds(30)), "m");
        supervisor.TryGet(task, out var supervised);
        Assert.True(await TestKit.WaitUntilAsync(() => supervised.ProcessAlive, TimeSpan.FromSeconds(5)));

        var ack = await supervisor.StopAsync(task, TimeSpan.FromSeconds(120), StopDisposition.Preserve, null, CancellationToken.None);
        Assert.Equal(StopDelivery.Message, ack.Delivery);
        Assert.True(supervised.ProcessAlive); // inside the wind-down window, not yet killed

        _clock.Advance(TimeSpan.FromSeconds(30)); // wind-down deadline (< TTL) → hard kill
        Assert.True(await TestKit.WaitUntilAsync(() => !supervised.ProcessAlive, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Stop_with_ttl_zero_kills_immediately_without_injecting_even_in_message_mode()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();
        // A message-mode profile whose harness WOULD wind down on a "stop" line — but
        // TTL=0 must kill outright without injecting anything, so it never gets one.
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("stdin-stop", StopMode.Message), "m");
        supervisor.TryGet(task, out var supervised);
        var marker = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(15)));

        var ack = await supervisor.StopAsync(task, TimeSpan.Zero, StopDisposition.Discard, null, CancellationToken.None);

        Assert.Equal(StopDelivery.ImmediateKill, ack.Delivery);
        Assert.True(await TestKit.WaitUntilAsync(() => !supervised.ProcessAlive, TimeSpan.FromSeconds(10)));

        // No stop turn was injected, so the harness never wrote its graceful-stop
        // marker. Safe to assert absence: the process is dead, so no writer remains.
        var stopped = Path.Combine(_workRoot, task.ToString(), "stopped");
        Assert.False(File.Exists(stopped), "TTL=0 must not inject a wind-down turn");
    }

    [Fact]
    public async Task Stop_signal_mode_hard_kills_at_the_ttl_deadline_with_no_injection()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();
        // No message seam (signal mode): nothing is injected, but the plane granted a
        // TTL>0 grace the Lead chose on the wire, so the worker gets the FULL ttl to
        // finish and exit on its own before the hard kill — the runner does not
        // second-guess the plane's grace (§9 check 12: only ttl=0 is immediate).
        // wind_down (10s) is deliberately shorter than the ttl (30s): the kill must
        // key off the ttl, NOT wind_down, which is the message-path budget alone.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("run", StopMode.Signal, windDown: TimeSpan.FromSeconds(10)), "m");
        supervisor.TryGet(task, out var supervised);
        Assert.True(await TestKit.WaitUntilAsync(() => supervised.ProcessAlive, TimeSpan.FromSeconds(5)));

        var ack = await supervisor.StopAsync(task, TimeSpan.FromSeconds(30), StopDisposition.Preserve, null, CancellationToken.None);
        Assert.Equal(StopDelivery.Signal, ack.Delivery);
        Assert.True(supervised.ProcessAlive); // not killed immediately — the ttl grace holds

        _clock.Advance(TimeSpan.FromSeconds(10)); // wind_down would fire here, but must NOT apply
        Assert.True(supervised.ProcessAlive, "a signal-mode stop must wait the full ttl, not wind_down");

        _clock.Advance(TimeSpan.FromSeconds(20)); // reaches the ttl deadline → hard kill
        Assert.True(await TestKit.WaitUntilAsync(() => !supervised.ProcessAlive, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Kill_takes_the_whole_tree_down_and_leaves_siblings_alive()
    {
        var taskA = TaskId.New();
        var taskB = TaskId.New();
        var supervisor = Supervisor();
        supervisor.Spawn(TestKit.Dispatch(taskA), TestKit.Profile("spawn-child"), "m");
        supervisor.Spawn(TestKit.Dispatch(taskB), TestKit.Profile("spawn-child"), "m");

        var childA = await ReadChildPid(taskA);
        var childB = await ReadChildPid(taskB);
        Assert.True(await TestKit.WaitUntilAsync(() => TestKit.PidAlive(childA) && TestKit.PidAlive(childB), TimeSpan.FromSeconds(10)));

        supervisor.TryGet(taskA, out var supervisedA);
        supervisor.TryGet(taskB, out var supervisedB);

        Assert.True(supervisor.Kill(taskA));

        // §10: group/tree kill takes A and its grandchild down; B's tree is untouched.
        Assert.True(await TestKit.WaitUntilAsync(() => !supervisedA.ProcessAlive, TimeSpan.FromSeconds(10)));
        Assert.True(await TestKit.WaitUntilAsync(() => !TestKit.PidAlive(childA), TimeSpan.FromSeconds(10)));
        Assert.True(supervisedB.ProcessAlive);
        Assert.True(TestKit.PidAlive(childB));

        supervisor.Kill(taskB);
    }

    [Fact]
    public async Task Per_task_liveness_derives_from_activity_and_process_alive()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "m");
        supervisor.TryGet(task, out var supervised);
        Assert.True(await TestKit.WaitUntilAsync(() => supervised.ProcessAlive, TimeSpan.FromSeconds(5)));

        var timeout = TimeSpan.FromSeconds(10);
        Assert.True(supervisor.IsTaskLive(task, timeout)); // fresh

        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.False(supervisor.IsTaskLive(task, timeout)); // stale: no signal within the window

        supervisor.RecordActivity(task); // a tool-call arrives
        Assert.True(supervisor.IsTaskLive(task, timeout));

        supervisor.Kill(task);
        Assert.True(await TestKit.WaitUntilAsync(() => !supervisor.IsTaskLive(task, timeout), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Kill_all_stops_everything_the_supervisor_started()
    {
        var supervisor = Supervisor();
        var tasks = new List<TaskId>();
        for (var i = 0; i < 3; i++)
        {
            var t = TaskId.New();
            tasks.Add(t);
            supervisor.Spawn(TestKit.Dispatch(t), TestKit.Profile("run"), "m");
        }
        Assert.Equal(3, supervisor.RunningTotal);

        supervisor.KillAll();

        Assert.True(await TestKit.WaitUntilAsync(() => supervisor.RunningTotal == 0, TimeSpan.FromSeconds(10)));
    }

    // ── §11 resume: spawn argv selection ────────────────────────────────────────

    [Fact]
    public async Task Resume_spawns_from_resume_args_substituting_session_id_and_mcp_config()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // A dispatch that carries a resume ref against a profile that declares
        // resume.args → the supervisor builds the argv from resume.args, filling
        // {session_id} with the ref and {mcp_config} with the written config path.
        var dispatch = new DispatchCommand(
            task, "default", McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: "sess-abc");
        supervisor.Spawn(dispatch, TestKit.ResumeProfile(), "m");

        var argv = await ReadArgvMarker(task);
        Assert.Equal("echo-argv", argv[0]); // resume.args re-runs the harness in echo mode
        var resumeIdx = Array.IndexOf(argv, "--resume");
        Assert.True(resumeIdx >= 0, "resume argv did not carry --resume");
        Assert.Equal("sess-abc", argv[resumeIdx + 1]); // {session_id} substituted
        var mcpIdx = Array.IndexOf(argv, "--mcp-config");
        Assert.True(mcpIdx >= 0, "resume argv did not carry --mcp-config");
        Assert.Equal(Path.Combine(_workRoot, task.ToString(), "mcp.json"), argv[mcpIdx + 1]); // {mcp_config} substituted

        supervisor.Kill(task);
    }

    [Fact]
    public async Task A_resume_ref_with_no_resume_config_cold_starts()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // The profile has NO resume config, so even a dispatch carrying a resume ref
        // spawns the normal (cold) argv — the documented fallback (§11).
        supervisor.Spawn(
            new DispatchCommand(task, "default", ResumeSessionRef: "sess-abc"),
            TestKit.Profile("echo-argv"), "m");

        var argv = await ReadArgvMarker(task);
        Assert.Equal(["echo-argv"], argv);          // just the cold spawn argv…
        Assert.DoesNotContain("--resume", argv);     // …no resume flag, no {session_id}

        supervisor.Kill(task);
    }

    [Fact]
    public async Task A_resume_config_with_no_ref_cold_starts()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        // The profile declares resume.args, but this dispatch carries no ref (a first
        // dispatch), so the supervisor spawns the cold argv, not resume.args (§11).
        supervisor.Spawn(new DispatchCommand(task, "default"), TestKit.ResumeProfile(), "m");

        var argv = await ReadArgvMarker(task);
        Assert.Equal(["echo-argv"], argv);
        Assert.DoesNotContain("--resume", argv);

        supervisor.Kill(task);
    }

    private async Task<string[]> ReadArgvMarker(TaskId task)
    {
        var path = Path.Combine(_workRoot, task.ToString(), "argv");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(15)),
            "harness never recorded its argv");
        return await File.ReadAllLinesAsync(path);
    }

    private async Task<int> ReadChildPid(TaskId task)
    {
        var path = Path.Combine(_workRoot, task.ToString(), "child.pid");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(15)),
            "harness never recorded its grandchild pid");
        return int.Parse(await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        try { _supervisor?.KillAll(); } catch { /* best effort */ }
        TestKit.TryDeleteRoot(_workRoot);
    }
}
