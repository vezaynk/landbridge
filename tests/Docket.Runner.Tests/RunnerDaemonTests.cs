using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.Runner.Tests;

/// <summary>
/// Daemon orchestration, spec §10: reboot announcement + stray reaping on
/// start, back-pressure / max_concurrent gating, heartbeat cadence, and the
/// closed-vocabulary command dispatch. Uses fakes so the logic is exercised
/// without real processes (those are covered by ProcessSupervisorTests).
/// </summary>
public class RunnerDaemonTests
{
    private static RunnerConfig Config(int? maxConcurrent = null, double heartbeatSeconds = 5)
    {
        var cap = maxConcurrent is { } m ? $", \"max_concurrent\": {m}" : "";
        return RunnerConfig.Load($$"""
        {
          "machine": { "work_root": "/tmp/docketd-fake", "heartbeat_seconds": {{heartbeatSeconds}} },
          "profiles": [ { "name": "default", "spawn": ["noop"]{{cap}} } ]
        }
        """);
    }

    private sealed class Harness
    {
        public required RunnerDaemon Daemon { get; init; }
        public required FakeProcessSupervisor Supervisor { get; init; }
        public required FakeStrayReaper Reaper { get; init; }
        public required IControlPlaneChannel Channel { get; init; }
        public required OutboundEventRing Ring { get; init; }
        public required FakeLoadReader Load { get; init; }
        public required FakeTimeProvider Clock { get; init; }

        /// <summary>The channel as the in-memory recorder (every test but the gated-send one).</summary>
        public InMemoryControlPlaneChannel Recorded => (InMemoryControlPlaneChannel)Channel;
    }

    private static Harness Build(
        RunnerConfig? config = null, int straysToReap = 0,
        TranscriptReader? transcripts = null, IControlPlaneChannel? channel = null,
        ServiceSupervisor? services = null)
    {
        var supervisor = new FakeProcessSupervisor();
        var reaper = new FakeStrayReaper(straysToReap);
        channel ??= new InMemoryControlPlaneChannel();
        var ring = new OutboundEventRing(64);
        var load = new FakeLoadReader();
        var clock = new FakeTimeProvider();
        var cfg = config ?? Config();
        var daemon = new RunnerDaemon(
            "machine-1", cfg, supervisor,
            new BackPressureMonitor(load, cfg.Machine.BackPressure),
            channel, ring, reaper, clock, transcripts: transcripts, services: services);
        return new Harness
        {
            Daemon = daemon, Supervisor = supervisor, Reaper = reaper,
            Channel = channel, Ring = ring, Load = load, Clock = clock,
        };
    }

    [Fact]
    public async Task On_start_it_reaps_strays_for_this_machine_and_emits_rebooted()
    {
        var h = Build(straysToReap: 3);

        await h.Daemon.StartAsync();

        Assert.Equal(3, h.Daemon.StraysReaped);
        Assert.Equal("machine-1", h.Reaper.ReapedMachine);
        Assert.True(await TestKit.WaitUntilAsync(
            () => h.Recorded.Events.Any(e => e.Event is RebootedEvent), TimeSpan.FromSeconds(5)));
        var rebooted = (RebootedEvent)h.Recorded.Events.First(e => e.Event is RebootedEvent).Event;
        Assert.Equal("machine-1", rebooted.MachineId);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task It_refuses_dispatch_under_back_pressure()
    {
        var h = Build();
        h.Load.Load = new SystemLoad(CpuLoad: 0, MemoryLoad: 0.99, DiskUsage: 0); // over the 0.90 default

        var outcome = await h.Daemon.HandleAsync(TestKit.Dispatch(TaskId.New()));

        var refused = Assert.IsType<CommandOutcome.Refused>(outcome);
        Assert.Equal(RefuseReason.BackPressure, refused.Reason);
        Assert.Empty(h.Supervisor.Spawned);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task It_enforces_a_profile_max_concurrent_cap()
    {
        var h = Build(Config(maxConcurrent: 1));

        var first = await h.Daemon.HandleAsync(TestKit.Dispatch(TaskId.New()));
        var second = await h.Daemon.HandleAsync(TestKit.Dispatch(TaskId.New()));

        Assert.IsType<CommandOutcome.Accepted>(first);
        var refused = Assert.IsType<CommandOutcome.Refused>(second);
        Assert.Equal(RefuseReason.MaxConcurrent, refused.Reason);
        Assert.Single(h.Supervisor.Spawned);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task It_refuses_a_dispatch_for_an_undeclared_profile()
    {
        var h = Build();

        var outcome = await h.Daemon.HandleAsync(TestKit.Dispatch(TaskId.New(), profile: "ghost"));

        var refused = Assert.IsType<CommandOutcome.Refused>(outcome);
        Assert.Equal(RefuseReason.UnknownProfile, refused.Reason);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task It_accepts_a_dispatch_when_healthy()
    {
        var h = Build();
        var task = TaskId.New();

        var outcome = await h.Daemon.HandleAsync(TestKit.Dispatch(task));

        var accepted = Assert.IsType<CommandOutcome.Accepted>(outcome);
        Assert.Equal(task, accepted.Task);
        Assert.Single(h.Supervisor.Spawned);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task Heartbeat_fires_on_its_own_timer_and_reflects_back_pressure()
    {
        var h = Build();
        await h.Daemon.StartAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(h.Recorded.Heartbeats);
        var healthy = h.Recorded.Heartbeats[^1];
        Assert.True(healthy.Ready);
        Assert.False(healthy.UnderBackPressure);

        h.Load.Load = new SystemLoad(0, 0.99, 0);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var saturated = h.Recorded.Heartbeats[^1];
        Assert.False(saturated.Ready);
        Assert.True(saturated.UnderBackPressure);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task The_heartbeat_timer_emits_one_alive_per_live_task()
    {
        // §10 per-task liveness, process-alive half. This is the only channel by which
        // "the harness process still exists" reaches the plane: the machine heartbeat
        // is machine-scoped, and a profile with no tool-call source produces nothing
        // else after `started`. Without these events the plane requeues every task
        // that outlives its liveness window.
        var h = Build();
        var alive = TaskId.New();
        var dies = TaskId.New();
        await h.Daemon.HandleAsync(TestKit.Dispatch(alive));
        await h.Daemon.HandleAsync(TestKit.Dispatch(dies));
        await h.Daemon.StartAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(5)); // one heartbeat interval
        Assert.True(await TestKit.WaitUntilAsync(
            () => AliveFor(h, alive) == 1 && AliveFor(h, dies) == 1, TimeSpan.FromSeconds(5)));

        // One process exits; its bookkeeping lingers until teardown. Reporting it alive
        // would hold off a requeue that should happen, so only the survivor beats on.
        h.Supervisor.Exited.Add(dies);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await TestKit.WaitUntilAsync(() => AliveFor(h, alive) == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, AliveFor(h, dies));

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task An_idle_machine_emits_no_alive_events()
    {
        // Nothing supervised, nothing to assert liveness about — the beat is per task,
        // not a machine-level keepalive (that is the heartbeat's job).
        var h = Build();
        await h.Daemon.StartAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.NotEmpty(h.Recorded.Heartbeats);
        Assert.DoesNotContain(h.Recorded.Events, e => e.Event is AliveEvent);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task The_heartbeat_carries_declared_service_status()
    {
        // §10/§12: the heartbeat is the whole channel by which services reach the plane.
        // It sits outside the frozen command/event enum, so this is an ordinary additive
        // field rather than a vocabulary change.
        var config = RunnerConfig.Load("""
        {
          "machine": { "work_root": "/tmp/docketd-fake", "heartbeat_seconds": 5 },
          "profiles": [ { "name": "default", "spawn": ["noop"] } ],
          "services": [ { "name": "api", "spawn": ["/bin/echo"], "port": 7101 } ]
        }
        """);
        await using var services = new ServiceSupervisor(
            config.DeclaredServices, "m1", TimeProvider.System);
        var h = Build(config, services: services);
        await h.Daemon.StartAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(h.Recorded.Heartbeats);
        var reported = Assert.Single(h.Recorded.Heartbeats[^1].Services!);
        Assert.Equal("api", reported.Name);
        Assert.Equal(7101, reported.Port);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task A_machine_declaring_no_services_reports_null_not_an_empty_list()
    {
        // Null is "says nothing", which is what an older runner also sends — so the
        // plane has exactly one shape to handle for "nothing to render here".
        var h = Build();
        await h.Daemon.StartAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(h.Recorded.Heartbeats[^1].Services);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task A_start_service_command_is_refused_when_no_supervised_task_owns_it()
    {
        // §10: the profile carries the policy, and only a task this machine actually holds
        // has a profile to consult. Refusing beats guessing — a service with no owner has no
        // lifetime, which is the one thing this design will not allow.
        await using var services = new ServiceSupervisor([], "machine-1", TimeProvider.System);
        var h = Build(services: services);
        await h.Daemon.StartAsync(); // the ring pump is what carries the reply to the channel

        var outcome = await h.Daemon.HandleAsync(new StartServiceCommand(
            TaskId.New(), "req-1", "dev", [TestKit.HarnessPath(), "sleeper"]));

        Assert.IsType<CommandOutcome.Acknowledged>(outcome);
        Assert.True(await TestKit.WaitUntilAsync(
            () => h.Recorded.Events.Any(e => e.Event is ServiceStartedEvent), TimeSpan.FromSeconds(5)));
        var reply = (ServiceStartedEvent)h.Recorded.Events.First(e => e.Event is ServiceStartedEvent).Event;
        Assert.False(reply.Started);
        Assert.Equal(ServiceRefusals.ProfileNotPermitted, reply.Refusal);
        Assert.Equal("req-1", reply.RequestId);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task A_start_service_command_replies_with_the_port_and_log_path()
    {
        // The reply is what a worker acts on: the port it must register (§8.2) and where its
        // own logs are, so it reads them with file tools rather than needing a serving path.
        var cwd = TestKit.NewWorkRoot();
        var state = TestKit.NewWorkRoot();
        try
        {
            var config = RunnerConfig.Load("""
            {
              "machine": { "work_root": "/tmp/docketd-fake", "heartbeat_seconds": 5 },
              "profiles": [ { "name": "default", "spawn": ["noop"],
                "services": { "agent_initiated": true } } ]
            }
            """);
            await using var services = new ServiceSupervisor(
                [], "machine-1", TimeProvider.System,
                logs: new ServiceLogStore(Path.Combine(state, ServiceLogStore.DirName)),
                probe: (_, _) => Task.FromResult(true));
            var h = Build(config, services: services);
            await h.Daemon.StartAsync();

            // The daemon resolves the profile from the supervised task, so dispatch one first.
            var task = TaskId.New();
            await h.Daemon.HandleAsync(TestKit.Dispatch(task));

            await h.Daemon.HandleAsync(new StartServiceCommand(
                task, "req-2", "dev", [TestKit.HarnessPath(), "sleeper"],
                WorkingDirectory: cwd, Env: null, Port: 7401, ReadinessTcpPort: 7401));

            Assert.True(await TestKit.WaitUntilAsync(
                () => h.Recorded.Events.Any(e => e.Event is ServiceStartedEvent), TimeSpan.FromSeconds(15)));
            var reply = (ServiceStartedEvent)h.Recorded.Events.First(e => e.Event is ServiceStartedEvent).Event;
            Assert.True(reply.Started, reply.Refusal);
            Assert.Equal(7401, reply.Port);
            Assert.Contains("dev", reply.LogPath!, StringComparison.Ordinal);

            await h.Daemon.ShutdownAsync();
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
            TestKit.TryDeleteRoot(state);
        }
    }

    private static int AliveFor(Harness h, TaskId task) =>
        h.Recorded.Events.Count(e => e.Event is AliveEvent a && a.Task == task);

    [Fact]
    public async Task Stop_and_kill_are_always_actioned_as_the_control_channel()
    {
        var h = Build();
        var task = TaskId.New();

        var stop = await h.Daemon.HandleAsync(new StopCommand(task, TimeSpan.FromSeconds(30), StopDisposition.Preserve));
        var kill = await h.Daemon.HandleAsync(new KillCommand(task));
        // A legacy open-forward with no role (the pre-increment-3 envelope shape)
        // is acknowledged and ignored — never crashed on (§8.3, §10).
        var forward = await h.Daemon.HandleAsync(new OpenForwardCommand(task, "fwd-1", "postgres"));

        Assert.IsType<CommandOutcome.Acknowledged>(stop);
        Assert.IsType<CommandOutcome.Acknowledged>(kill);
        Assert.Contains(task, h.Supervisor.Stopped);
        Assert.Contains(task, h.Supervisor.Killed);
        Assert.Contains("no role", Assert.IsType<CommandOutcome.Acknowledged>(forward).Detail);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_kills_everything_it_started()
    {
        var h = Build();
        await h.Daemon.HandleAsync(TestKit.Dispatch(TaskId.New()));

        await h.Daemon.ShutdownAsync();

        Assert.True(h.Supervisor.KilledAll);
    }

    // ── §12 transcript serving ────────────────────────────────────────────────

    [Fact]
    public async Task A_transcript_reply_bypasses_the_outbound_ring()
    {
        // The proof is in what is NOT started: the ring only reaches the channel through
        // the pump StartAsync launches. With no pump, an event put on the ring can never
        // be delivered — so a transcript reply that arrives anyway did not ride the ring
        // (§10 buffering: a dropped chunk is a corrupted read, and chunks must never
        // evict liveness events).
        using var root = new TempTranscripts();
        var task = root.Capture(["a line of transcript"]);
        var h = Build(transcripts: root.Reader());

        h.Ring.Enqueue(new AliveEvent(task, DateTimeOffset.UtcNow));
        var outcome = await h.Daemon.HandleAsync(new ReadTranscriptCommand(task, "req-1", Ordinal: 1));

        Assert.IsType<CommandOutcome.Acknowledged>(outcome);
        Assert.True(await TestKit.WaitUntilAsync(
            () => h.Recorded.Events.Any(e => e.Event is TranscriptChunkEvent), TimeSpan.FromSeconds(5)));
        var chunk = (TranscriptChunkEvent)h.Recorded.Events.Single(e => e.Event is TranscriptChunkEvent).Event;
        Assert.Equal("a line of transcript\n", chunk.Text);
        Assert.DoesNotContain(h.Recorded.Events, e => e.Event is AliveEvent);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task A_transcript_read_never_blocks_the_command_handler()
    {
        // The handler runs on the control socket's receive loop, so awaiting a chunk's
        // send here would delay every inbound command behind it — including a kill, the
        // broken escalation path §10 warns about. With the send wedged open, the handler
        // must still return.
        using var root = new TempTranscripts();
        var task = root.Capture(["content"]);
        var gate = new GatedControlPlaneChannel();
        var h = Build(transcripts: root.Reader(), channel: gate);

        var outcome = await h.Daemon.HandleAsync(new ReadTranscriptCommand(task, "req-1", Ordinal: 1));

        Assert.IsType<CommandOutcome.Acknowledged>(outcome);
        Assert.True(await TestKit.WaitUntilAsync(() => gate.Blocked, TimeSpan.FromSeconds(5)),
            "the reply send should be in flight while the handler has already returned");

        // A kill arriving mid-transcript is actioned immediately, not queued behind it.
        Assert.IsType<CommandOutcome.Acknowledged>(await h.Daemon.HandleAsync(new KillCommand(task)));
        Assert.Contains(task, h.Supervisor.Killed);

        gate.Release();
        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task Read_transcript_is_acknowledged_and_unanswered_when_serving_is_not_wired()
    {
        // A daemon built without a reader (most unit tests, and any future posture that
        // withholds serving) must not crash on a command it cannot answer.
        var h = Build();

        var outcome = await h.Daemon.HandleAsync(new ReadTranscriptCommand(TaskId.New(), "req-1", Ordinal: 1));

        Assert.Contains("serving unavailable", Assert.IsType<CommandOutcome.Acknowledged>(outcome).Detail);
        Assert.DoesNotContain(h.Recorded.Events, e => e.Event is TranscriptChunkEvent);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task The_heartbeat_advertises_transcript_serving_only_when_a_reader_is_wired()
    {
        // The flag exists so the dashboard offers a transcript link only where one can be
        // served: an older runner rejects read-transcript at the wire boundary and simply
        // never replies, which is indistinguishable from a slow machine (§12).
        using var root = new TempTranscripts();
        var serving = Build(transcripts: root.Reader());
        var notServing = Build();

        await serving.Daemon.StartAsync();
        await notServing.Daemon.StartAsync();
        serving.Clock.Advance(TimeSpan.FromSeconds(5));
        notServing.Clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(serving.Recorded.Heartbeats[^1].TranscriptsServable);
        Assert.False(notServing.Recorded.Heartbeats[^1].TranscriptsServable);

        await serving.Daemon.ShutdownAsync();
        await notServing.Daemon.ShutdownAsync();
    }
}

/// <summary>A temp transcripts root with one captured instance, for the §12 serving tests.</summary>
internal sealed class TempTranscripts : IDisposable
{
    private readonly string _root = TestKit.NewWorkRoot();

    public TranscriptStore Store() => new(_root, TimeSpan.FromDays(7), TimeProvider.System);

    public TranscriptReader Reader() => new(Store());

    /// <summary>Captures one instance for a fresh task and returns its id.</summary>
    public TaskId Capture(string[] stdout)
    {
        var task = TaskId.New();
        var writer = Store().CreateWriter(task, TranscriptDefaults.MaxBytes);
        foreach (var line in stdout)
            writer.WriteStdoutLine(line);
        writer.Dispose();
        return task;
    }

    public void Dispose() => TestKit.TryDeleteRoot(_root);
}

/// <summary>
/// A channel whose event send blocks until released — so a test can hold a transcript
/// reply in flight and prove the command handler already returned.
/// </summary>
internal sealed class GatedControlPlaneChannel : IControlPlaneChannel
{
    private readonly SemaphoreSlim _release = new(0, 1);

    public bool Blocked { get; private set; }

    public async Task<bool> PublishAsync(RunnerEvent evt, long gapBefore, CancellationToken ct)
    {
        Blocked = true;
        await _release.WaitAsync(CancellationToken.None);
        Blocked = false;
        return true;
    }

    public Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct) => Task.FromResult(true);

    public void Release() => _release.Release();
}

/// <summary>A supervisor that records calls without touching real processes.</summary>
internal sealed class FakeProcessSupervisor : IProcessSupervisor
{
    private readonly Dictionary<string, int> _running = new(StringComparer.Ordinal);

    public List<DispatchCommand> Spawned { get; } = [];
    public List<TaskId> Stopped { get; } = [];
    public List<TaskId> Killed { get; } = [];
    public bool KilledAll { get; private set; }

    public TaskId Spawn(DispatchCommand dispatch, ProfileConfig profile, string machineId)
    {
        Spawned.Add(dispatch);
        _running[profile.Name] = RunningFor(profile.Name) + 1;
        return dispatch.Task;
    }

    public Task<StopAck> StopAsync(TaskId task, TimeSpan ttl, StopDisposition disposition, string? reason, CancellationToken ct)
    {
        Stopped.Add(task);
        return Task.FromResult(new StopAck(true, ttl <= TimeSpan.Zero ? StopDelivery.ImmediateKill : StopDelivery.Message));
    }

    public bool Kill(TaskId task)
    {
        Killed.Add(task);
        return true;
    }

    public void KillAll()
    {
        KilledAll = true;
        _running.Clear();
    }

    public int RunningFor(string profile) => _running.TryGetValue(profile, out var n) ? n : 0;

    public int RunningTotal => _running.Values.Sum();

    public IReadOnlyCollection<TaskId> RunningTasks => Spawned.Select(s => s.Task).ToArray();

    /// <summary>
    /// Every spawned task is "process-alive" unless a test declares otherwise via
    /// <see cref="Exited"/> — which is how the alive-emitter tests express a process
    /// that has died but whose bookkeeping is still around.
    /// </summary>
    public HashSet<TaskId> Exited { get; } = [];

    public IReadOnlyCollection<TaskId> LiveTasks =>
        Spawned.Select(s => s.Task).Where(t => !Exited.Contains(t)).ToArray();

    /// <summary>Every spawned task ran on "default" unless a test says otherwise — enough for
    /// §10 agent-service policy lookups, which only need the profile name.</summary>
    public string? ProfileFor(TaskId task) =>
        Spawned.Any(s => s.Task == task) ? "default" : null;
}

/// <summary>A reaper that reports a fixed count and records the machine it was asked to clean.</summary>
internal sealed class FakeStrayReaper(int count) : IStrayReaper
{
    public string? ReapedMachine { get; private set; }

    public int Reap(string machineId)
    {
        ReapedMachine = machineId;
        return count;
    }
}
