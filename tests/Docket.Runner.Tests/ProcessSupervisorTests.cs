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

    /// <summary>
    /// The message seam itself still works, end to end, against a harness that genuinely reads
    /// stdin turns: the written turn is consumed, the agent winds down, and it exits on its own
    /// before any deadline. This is the fact that keeps #103's honesty work from being mistaken
    /// for "message delivery is broken" — it is not; what was broken was docketd claiming the
    /// agent read the turn when only the write is observable. Here consumption <em>is</em>
    /// observable, but note where from: the harness's own marker file and its clean exit code,
    /// never the ack.
    /// </summary>
    [Fact]
    public async Task Stop_cancels_the_acp_session_and_the_agent_exits()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("stdin-stop"), "m");
        supervisor.TryGet(task, out var supervised);

        var marker = Path.Combine(_workRoot, task.ToString(), "started");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(marker), TimeSpan.FromSeconds(15)));

        var ack = await supervisor.StopAsync(task, TimeSpan.FromSeconds(30), StopDisposition.Preserve, "wind down", CancellationToken.None);

        Assert.True(ack.Actioned);
        Assert.Equal(StopDelivery.MessageWritten, ack.Delivery);

        var stopped = Path.Combine(_workRoot, task.ToString(), "stopped");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(stopped), TimeSpan.FromSeconds(15)),
            "harness did not record session/cancel");
        Assert.True(await TestKit.WaitUntilAsync(() => !supervised.ProcessAlive, TimeSpan.FromSeconds(15)));
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
            TestKit.Profile("ignore-cancel", windDown: TimeSpan.FromSeconds(30)), "m");
        supervisor.TryGet(task, out var supervised);
        Assert.True(await TestKit.WaitUntilAsync(() => supervised.ProcessAlive, TimeSpan.FromSeconds(5)));

        var ack = await supervisor.StopAsync(task, TimeSpan.FromSeconds(120), StopDisposition.Preserve, null, CancellationToken.None);
        Assert.Equal(StopDelivery.MessageWritten, ack.Delivery);
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
        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("stdin-stop"), "m");
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
            TestKit.Profile("ignore-cancel", windDown: TimeSpan.FromSeconds(10)), "m");
        supervisor.TryGet(task, out var supervised);
        Assert.True(await TestKit.WaitUntilAsync(() => supervised.ProcessAlive, TimeSpan.FromSeconds(5)));

        var ack = await supervisor.StopAsync(task, TimeSpan.FromSeconds(30), StopDisposition.Preserve, null, CancellationToken.None);
        Assert.Equal(StopDelivery.MessageWritten, ack.Delivery);
        Assert.True(supervised.ProcessAlive);

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await TestKit.WaitUntilAsync(() => !supervised.ProcessAlive, TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// The honesty property behind #103, stated as an equality: two message-mode profiles that
    /// differ only in whether their harness reads stdin produce the <b>same</b> ack, while their
    /// real outcomes diverge completely — one winds down and exits 0, the other has to be killed
    /// at the deadline. So the ack cannot mean "the agent was told to wind down"; it can only
    /// mean what it now says, that a turn was written. An ack that claimed consumption would
    /// have to tell these two apart, and nothing available to docketd can.
    /// <para>This is the unit-scale twin of the real-<c>claude</c> fact in
    /// <c>Docket.MultiMachine.Tests</c>, which is the same shape with the ignoring harness being
    /// the actual CLI.</para>
    /// </summary>
    [Fact]
    public async Task Session_cancel_is_the_same_ack_whether_the_agent_exits_or_ignores_it()
    {
        var reader = TaskId.New();
        var deaf = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(TestKit.Dispatch(reader), TestKit.Profile("stdin-stop", name: "reads"), "m");
        supervisor.Spawn(TestKit.Dispatch(deaf), TestKit.Profile("ignore-cancel", name: "deaf"), "m");
        supervisor.TryGet(reader, out var readerTask);
        supervisor.TryGet(deaf, out var deafTask);

        Assert.True(await TestKit.WaitUntilAsync(
            () => File.Exists(Path.Combine(_workRoot, reader.ToString(), "started"))
                && File.Exists(Path.Combine(_workRoot, deaf.ToString(), "started")),
            TimeSpan.FromSeconds(15)));

        var ttl = TimeSpan.FromSeconds(120);
        var readerAck = await supervisor.StopAsync(reader, ttl, StopDisposition.Preserve, "wind down", CancellationToken.None);
        var deafAck = await supervisor.StopAsync(deaf, ttl, StopDisposition.Preserve, "wind down", CancellationToken.None);

        Assert.Equal(StopDelivery.MessageWritten, readerAck.Delivery);
        Assert.Equal(readerAck, deafAck);

        Assert.True(await TestKit.WaitUntilAsync(
            () => File.Exists(Path.Combine(_workRoot, reader.ToString(), "stopped")), TimeSpan.FromSeconds(15)));
        Assert.True(await TestKit.WaitUntilAsync(() => !readerTask.ProcessAlive, TimeSpan.FromSeconds(15)));
        Assert.Equal(0, readerTask.Process.ExitCode);

        Assert.True(deafTask.ProcessAlive);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await TestKit.WaitUntilAsync(() => !deafTask.ProcessAlive, TimeSpan.FromSeconds(10)));
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

    /// <summary>
    /// §10 per-task liveness, process-alive half: <see cref="ProcessSupervisor.LiveTasks"/> is
    /// the set <c>RunnerDaemon.EmitAliveEvents</c> turns into one <c>alive</c> per task on every
    /// heartbeat, and it is the only channel by which a fact only the runner can observe reaches
    /// the plane. So this pins the property the wire actually depends on.
    ///
    /// <para>The subtlety worth a test is that it must be <em>narrower</em> than
    /// <see cref="ProcessSupervisor.RunningTasks"/>: a task whose process has died but whose
    /// bookkeeping has not yet been torn down still appears in <c>RunningTasks</c>, and
    /// reporting that one as alive would refresh the aliveness clock for a worker that is gone
    /// and hold off a requeue that should happen. Killing one of two tasks is what separates
    /// the two collections.</para>
    ///
    /// <para>This replaces a test of <c>IsTaskLive</c>, a runner-local activity clock that no
    /// production code ever read — the plane decides per-task liveness from the events it
    /// receives, not from a supervisor query with no wire representation.</para>
    /// </summary>
    [Fact]
    public async Task Live_tasks_carries_only_processes_still_alive_and_is_narrower_than_running()
    {
        var supervisor = Supervisor();
        var alive = TaskId.New();
        var doomed = TaskId.New();
        supervisor.Spawn(TestKit.Dispatch(alive), TestKit.Profile("run"), "m");
        supervisor.Spawn(TestKit.Dispatch(doomed), TestKit.Profile("run"), "m");

        Assert.True(
            await TestKit.WaitUntilAsync(() => supervisor.LiveTasks.Count == 2, TimeSpan.FromSeconds(10)),
            "both spawned workers should report process-alive");
        Assert.Contains(alive, supervisor.LiveTasks);
        Assert.Contains(doomed, supervisor.LiveTasks);

        supervisor.Kill(doomed);

        // The killed task drops out of LiveTasks — so no further `alive` is emitted for it and
        // the plane's aliveness clock is free to expire — while its sibling keeps reporting.
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => !supervisor.LiveTasks.Contains(doomed), TimeSpan.FromSeconds(10)),
            "a killed task must stop reporting process-alive");
        Assert.Contains(alive, supervisor.LiveTasks);

        supervisor.Kill(alive);
        Assert.True(
            await TestKit.WaitUntilAsync(() => supervisor.LiveTasks.Count == 0, TimeSpan.FromSeconds(10)));
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
    public async Task Resume_loads_the_acp_session_on_the_same_spawn()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        var dispatch = new DispatchCommand(
            task, "default", McpConfigJson: """{"mcpServers":{}}""", ResumeSessionRef: "sess-abc");
        supervisor.Spawn(dispatch, TestKit.Profile("echo-argv"), "m");

        var argv = await ReadArgvMarker(task);
        Assert.Equal(["echo-argv"], argv);
        var loaded = await ReadMarker(task, "acp-loaded");
        Assert.Equal("sess-abc", loaded);

        supervisor.Kill(task);
    }

    [Fact]
    public async Task A_cold_dispatch_opens_a_new_acp_session()
    {
        var task = TaskId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(new DispatchCommand(task, "default"), TestKit.Profile("echo-argv"), "m");

        var argv = await ReadArgvMarker(task);
        Assert.Equal(["echo-argv"], argv);
        var path = Path.Combine(_workRoot, task.ToString(), "acp-session");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(15)));
        Assert.False(File.Exists(Path.Combine(_workRoot, task.ToString(), "acp-loaded")));

        supervisor.Kill(task);
    }

    private async Task<string[]> ReadArgvMarker(TaskId task)
    {
        var raw = await ReadMarker(task, "argv");
        return raw.Split('\n', StringSplitOptions.None);
    }

    private async Task<string> ReadMarker(TaskId task, string name)
    {
        var path = Path.Combine(_workRoot, task.ToString(), name);
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(15)),
            "harness never recorded " + name);
        return (await File.ReadAllTextAsync(path)).Trim();
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
