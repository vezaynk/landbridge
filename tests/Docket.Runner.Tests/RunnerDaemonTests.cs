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
        public required InMemoryControlPlaneChannel Channel { get; init; }
        public required FakeLoadReader Load { get; init; }
        public required FakeTimeProvider Clock { get; init; }
    }

    private static Harness Build(RunnerConfig? config = null, int straysToReap = 0)
    {
        var supervisor = new FakeProcessSupervisor();
        var reaper = new FakeStrayReaper(straysToReap);
        var channel = new InMemoryControlPlaneChannel();
        var load = new FakeLoadReader();
        var clock = new FakeTimeProvider();
        var cfg = config ?? Config();
        var daemon = new RunnerDaemon(
            "machine-1", cfg, supervisor,
            new BackPressureMonitor(load, cfg.Machine.BackPressure),
            channel, new OutboundEventRing(64), reaper, clock);
        return new Harness
        {
            Daemon = daemon, Supervisor = supervisor, Reaper = reaper,
            Channel = channel, Load = load, Clock = clock,
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
            () => h.Channel.Events.Any(e => e.Event is RebootedEvent), TimeSpan.FromSeconds(5)));
        var rebooted = (RebootedEvent)h.Channel.Events.First(e => e.Event is RebootedEvent).Event;
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
        Assert.NotEmpty(h.Channel.Heartbeats);
        var healthy = h.Channel.Heartbeats[^1];
        Assert.True(healthy.Ready);
        Assert.False(healthy.UnderBackPressure);

        h.Load.Load = new SystemLoad(0, 0.99, 0);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        var saturated = h.Channel.Heartbeats[^1];
        Assert.False(saturated.Ready);
        Assert.True(saturated.UnderBackPressure);

        await h.Daemon.ShutdownAsync();
    }

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
