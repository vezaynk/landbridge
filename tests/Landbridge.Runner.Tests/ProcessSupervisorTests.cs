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
        var task = SessionId.New();
        var supervisor = Supervisor();

        supervisor.Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "machine-42");

        Assert.Equal(1, supervisor.RunningTotal);
        Assert.Equal(1, supervisor.RunningFor("default"));

        // §10: spawned into {work_root}/{session_id} with DOCKET_* injected. The
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
        var task = SessionId.New();
        Supervisor().Spawn(TestKit.Dispatch(task), TestKit.Profile("run"), "m");
        Supervisor().Kill(task);

        var events = await DrainedEventsAsync();
        Assert.Contains(events, e => e is StartedEvent s && s.Session == task);
    }




    [Fact]
    public async Task Stop_signal_mode_hard_kills_at_the_ttl_deadline_with_no_injection()
    {
        var task = SessionId.New();
        var supervisor = Supervisor();
        // No message seam (signal mode): nothing is injected, but the plane granted a
        // TTL>0 grace the Lead chose on the wire, so the worker gets the FULL ttl to
        // finish and exit on its own before the hard kill — the runner does not
        // second-guess the plane's grace (§9 check 12: only ttl=0 is immediate).
        // wind_down (10s) is deliberately shorter than the ttl (30s). This harness mode
        // speaks no ACP, so there is no session to cancel and the kill must key off the
        // ttl, not wind_down — which is the cancel-path budget alone.
        supervisor.Spawn(
            TestKit.Dispatch(task),
            TestKit.Profile("run", windDown: TimeSpan.FromSeconds(10)), "m");
        supervisor.TryGet(task, out var supervised);
        Assert.True(await TestKit.WaitUntilAsync(() => supervised.ProcessAlive, TimeSpan.FromSeconds(5)));

        var ack = await supervisor.StopAsync(task, TimeSpan.FromSeconds(30), StopDisposition.Preserve, null, CancellationToken.None);
        Assert.Equal(StopDelivery.DeadlineArmed, ack.Delivery);
        Assert.True(supervised.ProcessAlive); // not killed immediately — the ttl grace holds

        _clock.Advance(TimeSpan.FromSeconds(10)); // wind_down would fire here, but must NOT apply
        Assert.True(supervised.ProcessAlive, "a signal-mode stop must wait the full ttl, not wind_down");

        _clock.Advance(TimeSpan.FromSeconds(20)); // reaches the ttl deadline → hard kill
        Assert.True(await TestKit.WaitUntilAsync(() => !supervised.ProcessAlive, TimeSpan.FromSeconds(10)));
    }



    [Fact]
    public async Task Kill_takes_the_whole_tree_down_and_leaves_siblings_alive()
    {
        var taskA = SessionId.New();
        var taskB = SessionId.New();
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
    /// §10 per-task liveness, process-alive half: <see cref="ProcessSupervisor.LiveSessions"/> is
    /// the set <c>RunnerDaemon.EmitAliveEvents</c> turns into one <c>alive</c> per task on every
    /// heartbeat, and it is the only channel by which a fact only the runner can observe reaches
    /// the plane. So this pins the property the wire actually depends on.
    ///
    /// <para>The subtlety worth a test is that it must be <em>narrower</em> than
    /// <see cref="ProcessSupervisor.RunningSessions"/>: a task whose process has died but whose
    /// bookkeeping has not yet been torn down still appears in <c>RunningSessions</c>, and
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
        var alive = SessionId.New();
        var doomed = SessionId.New();
        supervisor.Spawn(TestKit.Dispatch(alive), TestKit.Profile("run"), "m");
        supervisor.Spawn(TestKit.Dispatch(doomed), TestKit.Profile("run"), "m");

        Assert.True(
            await TestKit.WaitUntilAsync(() => supervisor.LiveSessions.Count == 2, TimeSpan.FromSeconds(10)),
            "both spawned workers should report process-alive");
        Assert.Contains(alive, supervisor.LiveSessions);
        Assert.Contains(doomed, supervisor.LiveSessions);

        supervisor.Kill(doomed);

        // The killed task drops out of LiveSessions — so no further `alive` is emitted for it and
        // the plane's aliveness clock is free to expire — while its sibling keeps reporting.
        Assert.True(
            await TestKit.WaitUntilAsync(
                () => !supervisor.LiveSessions.Contains(doomed), TimeSpan.FromSeconds(10)),
            "a killed task must stop reporting process-alive");
        Assert.Contains(alive, supervisor.LiveSessions);

        supervisor.Kill(alive);
        Assert.True(
            await TestKit.WaitUntilAsync(() => supervisor.LiveSessions.Count == 0, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Kill_all_stops_everything_the_supervisor_started()
    {
        var supervisor = Supervisor();
        var tasks = new List<SessionId>();
        for (var i = 0; i < 3; i++)
        {
            var t = SessionId.New();
            tasks.Add(t);
            supervisor.Spawn(TestKit.Dispatch(t), TestKit.Profile("run"), "m");
        }
        Assert.Equal(3, supervisor.RunningTotal);

        supervisor.KillAll();

        Assert.True(await TestKit.WaitUntilAsync(() => supervisor.RunningTotal == 0, TimeSpan.FromSeconds(10)));
    }

    // ── §11 resume: spawn argv selection ────────────────────────────────────────




    private async Task<string[]> ReadArgvMarker(SessionId task)
    {
        var path = Path.Combine(_workRoot, task.ToString(), "argv");
        Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(path), TimeSpan.FromSeconds(15)),
            "harness never recorded its argv");
        return await File.ReadAllLinesAsync(path);
    }

    private async Task<int> ReadChildPid(SessionId task)
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
