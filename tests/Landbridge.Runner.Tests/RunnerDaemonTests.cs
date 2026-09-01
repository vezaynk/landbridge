using System.Globalization;
using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Runner.Tests;

/// <summary>
/// Daemon orchestration, spec §10: reboot announcement + stray reaping on
/// start, back-pressure gating, heartbeat cadence, and the
/// closed-vocabulary command dispatch. Uses fakes so the logic is exercised
/// without real processes (those are covered by ProcessSupervisorTests).
/// </summary>
public class RunnerDaemonTests
{
    private static RunnerConfig Config(double heartbeatSeconds = 5) =>
        RunnerConfig.Load($$"""
        {
          "machine": { "work_root": "/tmp/landbridged-fake", "heartbeat_seconds": {{heartbeatSeconds}} },
          "profiles": [ { "name": "default", "prompt": "go", "spawn": ["noop"] } ]
        }
        """);

    private sealed class Harness
    {
        public required RunnerDaemon Daemon { get; init; }
        public required FakeProcessSupervisor Supervisor { get; init; }
        public required FakeStrayReaper Reaper { get; init; }
        public required IControlPlaneChannel Channel { get; init; }
        public required OutboundEventRing Ring { get; init; }
        public required FakeLoadReader Load { get; init; }
        public required FakeTimeProvider Clock { get; init; }

        /// <summary>What the daemon wrote to this machine's stdout — where an operator reads
        /// what a <c>stop</c> actually did, since no event carries that (§10).</summary>
        public required List<string> Logged { get; init; }

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
        var logged = new List<string>();
        var daemon = new RunnerDaemon(
            "machine-1", cfg, supervisor,
            new BackPressureMonitor(load, cfg.Machine.BackPressure),
            channel, ring, reaper, clock, transcripts: transcripts, services: services,
            log: logged.Add);
        return new Harness
        {
            Daemon = daemon, Supervisor = supervisor, Reaper = reaper,
            Channel = channel, Ring = ring, Load = load, Clock = clock, Logged = logged,
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

    /// <summary>
    /// §10 buffering: the ring exists so an event outlives a dead connection. The pump
    /// drains it against a best-effort channel, so an event whose publish is refused must
    /// be held and re-offered, not consumed — otherwise the buffer silently loses exactly
    /// the events the plane's recovery reads (a <c>session-started</c> that never arrives
    /// degrades a resume to a cold start with no signal at all).
    /// </summary>
    [Fact]
    public async Task An_event_enqueued_while_the_channel_is_down_is_delivered_after_reconnect()
    {
        var channel = new InMemoryControlPlaneChannel { Connected = false };
        var h = Build(channel: channel);
        await h.Daemon.StartAsync();
        var task = SessionId.New();

        h.Ring.Enqueue(new SessionStartedEvent(task, "sess-1", h.Clock.GetUtcNow()));

        // While the socket is down nothing lands — the point of the test is what happens
        // to the event in the meantime, and this window is where it used to be eaten.
        Assert.False(
            await TestKit.WaitUntilAsync(() => h.Recorded.Events.Count > 0, TimeSpan.FromMilliseconds(250)),
            "a disconnected channel recorded a publish");

        channel.Connected = true;

        // The parked pump wakes off the injected clock, so the fake has to be pushed past
        // the retry interval. Advancing inside the poll rather than once beforehand keeps
        // the wake independent of whether the pump had reached its delay yet.
        Assert.True(
            await TestKit.WaitUntilAsync(
                () =>
                {
                    h.Clock.Advance(TimeSpan.FromSeconds(2));
                    return h.Recorded.Events.Any(e => e.Event is SessionStartedEvent);
                },
                TimeSpan.FromSeconds(10)),
            "session-started enqueued while the channel was down was never delivered after reconnect");

        // Delivered once, and behind the rebooted that was already queued ahead of it:
        // retrying a refused publish must not double-publish or reorder.
        var started = Assert.Single(h.Recorded.Events, e => e.Event is SessionStartedEvent);
        Assert.Equal("sess-1", ((SessionStartedEvent)started.Event).SessionRef);
        Assert.IsType<RebootedEvent>(h.Recorded.Events[0].Event);

        await h.Daemon.ShutdownAsync();
    }

    /// <summary>
    /// The start ordering, §10 runner restart: <c>landbridged</c> starts the daemon and only
    /// then dials, so <c>rebooted</c> is always enqueued against a channel with no live
    /// socket. It has to survive that wait — it is the signal that requeues everything
    /// this machine was holding.
    /// </summary>
    [Fact]
    public async Task Rebooted_survives_being_enqueued_before_the_first_connection()
    {
        var channel = new InMemoryControlPlaneChannel { Connected = false };
        var h = Build(channel: channel);

        await h.Daemon.StartAsync();
        Assert.False(
            await TestKit.WaitUntilAsync(() => h.Recorded.Events.Count > 0, TimeSpan.FromMilliseconds(250)),
            "a disconnected channel recorded a publish");

        channel.Connected = true; // the first dial completes

        Assert.True(
            await TestKit.WaitUntilAsync(
                () =>
                {
                    h.Clock.Advance(TimeSpan.FromSeconds(2));
                    return h.Recorded.Events.Any(e => e.Event is RebootedEvent);
                },
                TimeSpan.FromSeconds(10)),
            "rebooted enqueued before the first connection was never delivered");
        Assert.Single(h.Recorded.Events, e => e.Event is RebootedEvent);

        await h.Daemon.ShutdownAsync();
    }

    /// <summary>
    /// Holding an event the channel refused must not make the loss accounting lie. An
    /// outage longer than the ring still drops — that is §10's bound, and the alternative
    /// is unbounded memory — but every drop has to surface as a gap marker on a survivor,
    /// including the drops that happen while the pump is parked on a held event.
    /// </summary>
    [Fact]
    public async Task Overflow_while_the_pump_is_parked_still_lands_as_a_gap_marker()
    {
        var channel = new InMemoryControlPlaneChannel { Connected = false };
        var h = Build(channel: channel);
        var task = SessionId.New();
        await h.Daemon.StartAsync();

        // Park the pump: it drains rebooted, is refused, and holds it.
        Assert.False(
            await TestKit.WaitUntilAsync(() => h.Recorded.Events.Count > 0, TimeSpan.FromMilliseconds(250)),
            "a disconnected channel recorded a publish");

        // Then overrun the ring behind the held event.
        const int enqueued = 200;
        for (var i = 0; i < enqueued; i++)
            h.Ring.Enqueue(new ToolCallEvent(task, i.ToString(), h.Clock.GetUtcNow()));
        var dropped = h.Ring.DroppedCount;
        Assert.True(dropped > 0, "the ring was expected to overflow during the outage");
        var expectedToolCalls = enqueued - dropped;

        channel.Connected = true;
        Assert.True(
            await TestKit.WaitUntilAsync(
                () =>
                {
                    h.Clock.Advance(TimeSpan.FromSeconds(2));
                    return h.Recorded.Events.Count(e => e.Event is ToolCallEvent) >= expectedToolCalls;
                },
                TimeSpan.FromSeconds(10)),
            "the ring's survivors were not delivered after reconnect");

        var delivered = h.Recorded.Events;
        // The held event went first and survived the overflow behind it.
        Assert.IsType<RebootedEvent>(delivered[0].Event);
        Assert.Equal(0, delivered[0].GapBefore);
        // Every drop is accounted for exactly once, on the survivor that followed it.
        Assert.Equal(dropped, delivered.Sum(e => e.GapBefore));
        // Nothing was delivered twice, and order held.
        var tools = delivered.Where(e => e.Event is ToolCallEvent)
            .Select(e => int.Parse(((ToolCallEvent)e.Event).Tool, CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(expectedToolCalls, tools.Length);
        Assert.Equal(tools.OrderBy(t => t).ToArray(), tools);
        Assert.Equal(enqueued - 1, tools[^1]);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task It_refuses_dispatch_under_back_pressure()
    {
        var h = Build();
        h.Load.Load = new SystemLoad(CpuLoad: 0, MemoryLoad: 0.99, DiskUsage: 0); // over the 0.90 default

        var outcome = await h.Daemon.HandleAsync(TestKit.Dispatch(SessionId.New()));

        var refused = Assert.IsType<CommandOutcome.Refused>(outcome);
        Assert.Equal(RefuseReason.BackPressure, refused.Reason);
        Assert.Empty(h.Supervisor.Spawned);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task It_refuses_a_dispatch_for_an_undeclared_profile()
    {
        var h = Build();

        var outcome = await h.Daemon.HandleAsync(TestKit.Dispatch(SessionId.New(), profile: "ghost"));

        var refused = Assert.IsType<CommandOutcome.Refused>(outcome);
        Assert.Equal(RefuseReason.UnknownProfile, refused.Reason);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task It_accepts_a_dispatch_when_healthy()
    {
        var h = Build();
        var task = SessionId.New();

        var outcome = await h.Daemon.HandleAsync(TestKit.Dispatch(task));

        var accepted = Assert.IsType<CommandOutcome.Accepted>(outcome);
        Assert.Equal(task, accepted.Session);
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
        var alive = SessionId.New();
        var dies = SessionId.New();
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
    public async Task A_start_process_command_is_refused_when_no_supervised_task_owns_it()
    {
        // §10: the profile carries the policy, and only a task this machine actually holds
        // has a profile to consult. Refusing beats guessing — a service with no owner has no
        // lifetime, which is the one thing this design will not allow.
        await using var services = new ServiceSupervisor("machine-1", TimeProvider.System);
        var h = Build(services: services);
        await h.Daemon.StartAsync(); // the ring pump is what carries the reply to the channel

        var outcome = await h.Daemon.HandleAsync(new StartProcessCommand(
            SessionId.New(), "req-1", "dev", [TestKit.HarnessPath(), "sleeper"]));

        Assert.IsType<CommandOutcome.Acknowledged>(outcome);
        Assert.True(await TestKit.WaitUntilAsync(
            () => h.Recorded.Events.Any(e => e.Event is ProcessStartedEvent), TimeSpan.FromSeconds(5)));
        var reply = (ProcessStartedEvent)h.Recorded.Events.First(e => e.Event is ProcessStartedEvent).Event;
        Assert.False(reply.Started);
        Assert.Equal(ProcessRefusals.ProfileNotPermitted, reply.Refusal);
        Assert.Equal("req-1", reply.RequestId);

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task A_start_process_command_replies_with_the_log_path()
    {
        // The reply is what a worker acts on: the port it must register (§8.2) and where its
        // own logs are, so it reads them with file tools rather than needing a serving path.
        var cwd = TestKit.NewWorkRoot();
        var state = TestKit.NewWorkRoot();
        try
        {
            var config = RunnerConfig.Load("""
            {
              "machine": { "work_root": "/tmp/landbridged-fake", "heartbeat_seconds": 5 },
              "profiles": [ { "name": "default", "spawn": ["noop"], "prompt": "go",
                "processes": { "agent_initiated": true } } ]
            }
            """);
            await using var services = new ServiceSupervisor(
                "machine-1", TimeProvider.System,
                logs: new ServiceLogStore(Path.Combine(state, ServiceLogStore.DirName)));
            var h = Build(config, services: services);
            await h.Daemon.StartAsync();

            // The daemon resolves the profile from the supervised task, so dispatch one first.
            var task = SessionId.New();
            await h.Daemon.HandleAsync(TestKit.Dispatch(task));

            await h.Daemon.HandleAsync(new StartProcessCommand(
                task, "req-2", "dev", [TestKit.HarnessPath(), "sleeper"],
                WorkingDirectory: cwd, Env: null));

            Assert.True(await TestKit.WaitUntilAsync(
                () => h.Recorded.Events.Any(e => e.Event is ProcessStartedEvent), TimeSpan.FromSeconds(15)));
            var reply = (ProcessStartedEvent)h.Recorded.Events.First(e => e.Event is ProcessStartedEvent).Event;
            Assert.True(reply.Started, reply.Refusal);
            Assert.Contains("dev", reply.LogPath!, StringComparison.Ordinal);

            await h.Daemon.ShutdownAsync();
        }
        finally
        {
            TestKit.TryDeleteRoot(cwd);
            TestKit.TryDeleteRoot(state);
        }
    }

    private static int AliveFor(Harness h, SessionId task) =>
        h.Recorded.Events.Count(e => e.Event is AliveEvent a && a.Session == task);

    [Fact]
    public async Task Stop_and_kill_are_always_actioned_as_the_control_channel()
    {
        var h = Build();
        var task = SessionId.New();

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

    /// <summary>
    /// What an operator is told a <c>stop</c> did (#103). This machine's stdout is the only place
    /// the delivery detail surfaces at all — the frozen event vocabulary has no field for it, and
    /// the <see cref="CommandOutcome"/> is dropped by the receive loop — so the wording is the
    /// whole operator-visible contract, and it must not overclaim. The written-turn line says
    /// outright that consumption was not confirmed; the deadline line does not mention a message
    /// at all. Previously both paths printed <c>"stop delivered as …"</c>, which read as delivery
    /// to the agent on either.
    /// </summary>
    [Fact]
    public async Task The_stop_the_operator_reads_names_what_this_machine_did_and_never_claims_the_agent_read_it()
    {
        var h = Build();
        var task = SessionId.New();
        var ttl = TimeSpan.FromSeconds(45);

        h.Supervisor.StopAckOverride = new StopAck(true, StopDelivery.CancelSent);
        var sent = Assert.IsType<CommandOutcome.Acknowledged>(
            await h.Daemon.HandleAsync(new StopCommand(task, ttl, StopDisposition.Preserve)));
        Assert.Contains("session/cancel sent", sent.Detail, StringComparison.Ordinal);
        Assert.Contains("not confirmed obeyed", sent.Detail, StringComparison.Ordinal);
        Assert.Contains("ttl=45s", sent.Detail, StringComparison.Ordinal);

        h.Supervisor.StopAckOverride = new StopAck(true, StopDelivery.DeadlineArmed);
        var armed = Assert.IsType<CommandOutcome.Acknowledged>(
            await h.Daemon.HandleAsync(new StopCommand(task, ttl, StopDisposition.Preserve)));
        Assert.Contains("no cancel sent", armed.Detail, StringComparison.Ordinal);
        Assert.Contains("hard kill armed at ttl=45s", armed.Detail, StringComparison.Ordinal);

        // A moot stop is said to be moot rather than acknowledged as if it landed.
        h.Supervisor.StopAckOverride = new StopAck(false, StopDelivery.NotRunning);
        var moot = Assert.IsType<CommandOutcome.Acknowledged>(
            await h.Daemon.HandleAsync(new StopCommand(task, ttl, StopDisposition.Preserve)));
        Assert.Contains("not held by this machine", moot.Detail, StringComparison.Ordinal);

        // Every one of them reached the operator's stdout, keyed by task.
        Assert.Equal(3, h.Logged.Count(l => l.Contains($"stop {task}", StringComparison.Ordinal)));
        Assert.DoesNotContain(h.Logged, l => l.Contains("delivered as", StringComparison.Ordinal));

        await h.Daemon.ShutdownAsync();
    }

    [Fact]
    public async Task Shutdown_kills_everything_it_started()
    {
        var h = Build();
        await h.Daemon.HandleAsync(TestKit.Dispatch(SessionId.New()));

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

        var outcome = await h.Daemon.HandleAsync(new ReadTranscriptCommand(SessionId.New(), "req-1", Ordinal: 1));

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
    public SessionId Capture(string[] stdout)
    {
        var task = SessionId.New();
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
    public List<SessionId> Stopped { get; } = [];
    public List<SessionId> Killed { get; } = [];
    public bool KilledAll { get; private set; }

    public SessionId Spawn(DispatchCommand dispatch, ProfileConfig profile, string machineId)
    {
        Spawned.Add(dispatch);
        _running[profile.Name] = RunningFor(profile.Name) + 1;
        return dispatch.Session;
    }

    /// <summary>Forces the ack a stop returns, so a test can drive the daemon's
    /// operator-facing wording down each path. Null keeps the default below.</summary>
    public StopAck? StopAckOverride { get; set; }

    public Task<StopAck> StopAsync(SessionId task, TimeSpan ttl, StopDisposition disposition, string? reason, CancellationToken ct)
    {
        Stopped.Add(task);
        return Task.FromResult(StopAckOverride
            ?? new StopAck(true, ttl <= TimeSpan.Zero ? StopDelivery.ImmediateKill : StopDelivery.CancelSent));
    }

    /// <summary>Follow-up turns the daemon routed here, and whether a live session took them
    /// (<c>ideas/sessions.md</c> stage 1). Null keeps the "no live session" default, which is
    /// what every stream-mode task answers.</summary>
    public List<SessionId> Prompted { get; } = [];
    public bool? PromptAccepted { get; set; }

    public bool TryPrompt(SessionId task)
    {
        Prompted.Add(task);
        return PromptAccepted ?? false;
    }

    public bool Kill(SessionId task)
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

    public IReadOnlyCollection<SessionId> RunningSessions => Spawned.Select(s => s.Session).ToArray();

    /// <summary>
    /// Every spawned task is "process-alive" unless a test declares otherwise via
    /// <see cref="Exited"/> — which is how the alive-emitter tests express a process
    /// that has died but whose bookkeeping is still around.
    /// </summary>
    public HashSet<SessionId> Exited { get; } = [];

    public IReadOnlyCollection<SessionId> LiveSessions =>
        Spawned.Select(s => s.Session).Where(t => !Exited.Contains(t)).ToArray();

    /// <summary>Every spawned task ran on "default" unless a test says otherwise — enough for
    /// §10 agent-service policy lookups, which only need the profile name.</summary>
    public string? ProfileFor(SessionId task) =>
        Spawned.Any(s => s.Session == task) ? "default" : null;
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
