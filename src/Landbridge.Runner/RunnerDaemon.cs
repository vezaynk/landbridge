using System.Collections.Concurrent;
using System.Globalization;
using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.Runner;

/// <summary>The outcome of handling one runner command.</summary>
public abstract record CommandOutcome
{
    private CommandOutcome() { }

    /// <summary>A dispatch was accepted and the harness spawned.</summary>
    public sealed record Accepted(SessionId Session) : CommandOutcome;

    /// <summary>A dispatch was refused; the task stays undispatched and requeues elsewhere.</summary>
    public sealed record Refused(RefuseReason Reason, string Detail) : CommandOutcome;

    /// <summary>A control command (stop/kill/open-forward) was actioned.</summary>
    public sealed record Acknowledged(string Detail) : CommandOutcome;
}

/// <summary>Why a dispatch was refused (§10 concurrency).</summary>
public enum RefuseReason
{
    /// <summary>Under load/mem/disk pressure — stop accepting dispatch (§10).</summary>
    BackPressure,

    /// <summary>No such profile is declared — the task is not routable here (§7, §9 check 5).</summary>
    UnknownProfile,
}

/// <summary>
/// Ties the runner together, spec §10. On start it reaps strays and announces
/// <c>rebooted</c>; while running it gates dispatch on back-pressure,
/// drains the outbound ring to the control-plane
/// channel, and beats a machine heartbeat on its own <see cref="TimeProvider"/>
/// timer. On shutdown it kills everything it started. It holds no persistent
/// state — a restart is a reboot (§10 runner restart).
/// </summary>
public sealed class RunnerDaemon
{
    /// <summary>
    /// How long the ring pump waits before re-offering an event the channel refused
    /// (see <see cref="PumpRingAsync"/>). Deliberately short: this is a poll of a local
    /// socket-state flag, not a dial — the channel runs its own connect backoff — so the
    /// cost while offline is a few bool checks a second, and in exchange a reconnect
    /// flushes the buffer promptly.
    ///
    /// <para>Promptness is load-bearing for <c>rebooted</c>, which rides the ring while
    /// heartbeats go direct: the plane requeues everything a machine holds when it hears
    /// <c>rebooted</c> (<c>RunnerEventSink.HandleRebootedAsync</c>) and dispatches nothing
    /// to a connection until a heartbeat marks it ready. Landing well inside the shortest
    /// sane <c>heartbeat_seconds</c> keeps <c>rebooted</c> ahead of this machine's first
    /// readiness, so the requeue can only ever catch the previous generation's tasks and
    /// never work dispatched to this one.</para>
    /// </summary>
    private static readonly TimeSpan PublishRetryInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _machineId;
    private readonly RunnerConfig _config;
    private readonly IProcessSupervisor _supervisor;
    private readonly BackPressureMonitor _backPressure;
    private readonly IControlPlaneChannel _channel;
    private readonly OutboundEventRing _ring;
    private readonly IStrayReaper _reaper;
    private readonly TimeProvider _clock;
    private readonly RelayForwarder _forwarder;
    private readonly TranscriptReader? _transcripts;
    private readonly Action<string>? _log;

    /// <summary>§10 agent-started processes. Status rides the heartbeat.</summary>
    private readonly AgentProcessSupervisor? _processes;

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Task, byte> _transcriptReads = new();
    private ITimer? _heartbeatTimer;
    private Task? _pump;

    /// <param name="transcripts">
    /// §12 transcript serving. When supplied, this daemon answers
    /// <see cref="ReadTranscriptCommand"/> and advertises
    /// <see cref="MachineHeartbeat.TranscriptsServable"/>; null (most unit tests) refuses
    /// nothing — it simply acknowledges and does not reply, and the heartbeat says so.
    /// </param>
    /// <param name="log">
    /// This machine's own stdout, where an operator reads what <c>landbridged</c> did (the
    /// enroll skill sends them here first when a task behaves oddly). Used for <c>stop</c>
    /// outcomes, which have no wire representation: the frozen event vocabulary carries no
    /// delivery field, and the <see cref="CommandOutcome"/> a command handler returns is
    /// dropped by the receive loop that called it. Without this line, the honesty of a
    /// stop ack is a fact only the tests can see. Null in unit tests.
    /// </param>
    public RunnerDaemon(
        string machineId,
        RunnerConfig config,
        IProcessSupervisor supervisor,
        BackPressureMonitor backPressure,
        IControlPlaneChannel channel,
        OutboundEventRing ring,
        IStrayReaper reaper,
        TimeProvider clock,
        TimeSpan? forwardAcceptTimeout = null,
        TranscriptReader? transcripts = null,
        AgentProcessSupervisor? processes = null,
        Action<string>? log = null)
    {
        _machineId = machineId;
        _config = config;
        _supervisor = supervisor;
        _backPressure = backPressure;
        _channel = channel;
        _ring = ring;
        _reaper = reaper;
        _clock = clock;
        _transcripts = transcripts;
        _processes = processes;
        _log = log;
        // §8.3 data planes: the forwarder owns all live relay tunnels and emits
        // forward-opened/-closed onto the same ring as every other event. The
        // accept-timeout override keeps the consumer plane's bounded wait short in
        // tests; production uses the grant-TTL ceiling.
        _forwarder = new RelayForwarder(ring, forwardAcceptTimeout);
    }

    /// <summary>Strays killed on the last <see cref="StartAsync"/> (diagnostics/tests).</summary>
    public int StraysReaped { get; private set; }

    /// <summary>
    /// §10 runner restart: kill strays <em>before</em> accepting dispatch, then
    /// emit <c>rebooted</c> so the control plane requeues what this machine
    /// held against the infrastructure counter (§6). Starts the ring pump and
    /// the heartbeat timer.
    /// </summary>
    public Task StartAsync()
    {
        StraysReaped = _reaper.Reap(_machineId);

        _pump = Task.Run(() => PumpRingAsync(_cts.Token));
        _ring.Enqueue(new RebootedEvent(_machineId, _clock.GetUtcNow()));

        // The heartbeat timer carries both machine-level and per-task liveness: the
        // machine heartbeat, and one `alive` per live task (§10). They share a timer
        // because they answer the same question at two scopes and want the same
        // cadence — comfortably under the plane's per-task window, so a task is never
        // requeued for silence while its process is plainly still there.
        _heartbeatTimer = _clock.CreateTimer(
            _ =>
            {
                EmitHeartbeat();
                EmitAliveEvents();
            },
            state: null,
            dueTime: _config.Machine.HeartbeatInterval,
            period: _config.Machine.HeartbeatInterval);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Dispatches the closed vocabulary (§10). Only <c>dispatch</c> is subject
    /// to back-pressure/concurrency gating; <c>stop</c> and <c>kill</c> are the
    /// highest-priority control channel and always action (§10 channel
    /// separation).
    /// </summary>
    public async Task<CommandOutcome> HandleAsync(RunnerCommand command, CancellationToken ct = default)
    {
        switch (command)
        {
            case DispatchCommand dispatch:
                return HandleDispatch(dispatch);

            case StopCommand stop:
                var ack = await _supervisor.StopAsync(stop.Session, stop.Ttl, stop.Disposition, stop.Reason, ct);
                var stopDetail = DescribeStop(stop, ack);
                _log?.Invoke($"stop {stop.Session}: {stopDetail}");
                return new CommandOutcome.Acknowledged(stopDetail);

            case KillCommand kill:
                var killed = _supervisor.Kill(kill.Session);
                return new CommandOutcome.Acknowledged(killed ? "killed" : "not running");

            case PromptCommand prompt:
                // ideas/sessions.md stage 1. Best-effort and acked either way, like
                // close-forward: §10 commands do not fail, they report what happened. But
                // the two outcomes are named apart, because a Lead's message that went
                // nowhere must not read as delivered — and the two reasons it can go nowhere
                // (the session ended; this is a stream profile with no channel that accepts
                // a turn) have completely different fixes.
                var queued = _supervisor.TryPrompt(prompt.Session);
                var promptDetail = queued
                    ? "queued for the live session"
                    : "not delivered: no live ACP session for this task — it has ended, or this is a " +
                      "stream profile, whose worker has no channel that accepts a turn (§10)";
                _log?.Invoke($"prompt {prompt.Session}: {promptDetail}");
                return new CommandOutcome.Acknowledged(promptDetail);

            case OpenForwardCommand forward:
                return HandleOpenForward(forward);

            case CloseForwardCommand close:
                // §8.3: the plane says this forward's authority is gone. Best-effort like
                // every §10 command — a forward this machine no longer holds is acked as
                // already closed, which is the outcome the plane asked for.
                var closed = _forwarder.Close(close);
                return new CommandOutcome.Acknowledged(
                    closed ? $"close-forward {close.ForwardId}" : $"close-forward {close.ForwardId} (not held)");

            case ReadTranscriptCommand read:
                return HandleReadTranscript(read);

            case StartProcessCommand start:
                return await HandleStartProcessAsync(start, ct);

            case StopProcessCommand stop2:
                return await HandleStopProcessAsync(stop2, ct);

            case WriteProcessCommand write:
                return await HandleWriteProcessAsync(write, ct);

            default:
                // Unreachable: the hierarchy is closed (§10). Kept explicit so a
                // future member can't be silently ignored.
                throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "outside the runner vocabulary");
        }
    }

    /// <summary>
    /// The operator-facing sentence for one <c>stop</c> outcome (§10). It states what this
    /// machine <em>did</em> and what backs it. The wording is careful for a reason the old
    /// stream mode earned: its line read <c>"stop delivered as Message"</c>, which a
    /// <c>claude -p</c> worker collected by having a pipe written into while it ran on
    /// untouched until the deadline killed it. A cancel is a stronger claim — the agent is
    /// specified to honour it — but it is still a notification with no reply, so the line
    /// says sent, not obeyed.
    /// </summary>
    private static string DescribeStop(StopCommand stop, StopAck ack) => ack.Delivery switch
    {
        StopDelivery.CancelSent =>
            "session/cancel sent on the worker's ACP connection — sent, not confirmed obeyed "
            + "(cancel is a notification with no reply, though the agent is specified to honour it); "
            + $"hard kill armed at min(ttl={Seconds(stop.Ttl)}, wind_down)",

        StopDelivery.DeadlineArmed =>
            "no cancel sent (the session had not opened, or its connection was already gone); "
            + $"hard kill armed at ttl={Seconds(stop.Ttl)} — the worker's own exit is the only graceful path left",

        StopDelivery.ImmediateKill =>
            "ttl=0 — process tree killed outright; nothing sent, nothing waited for (§9 check 12)",

        StopDelivery.NotRunning =>
            "not held by this machine; nothing to stop (§10 commands are best-effort)",

        _ => $"{ack.Delivery}",
    };

    private static string Seconds(TimeSpan span) =>
        ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";

    /// <summary>
    /// §8.3: stand up one end of a relay forward. Kicks the concurrent I/O off on
    /// a tracked background task and returns at once — the command handler never
    /// blocks (§10). A missing/unknown role (a pre-increment-3 envelope, whose new
    /// fields decode to empty) is acknowledged and ignored, never crashed on.
    /// </summary>
    private CommandOutcome HandleOpenForward(OpenForwardCommand forward)
    {
        switch (forward.Role)
        {
            case RelayTunnel.ConsumerRole:
            case RelayTunnel.ProducerRole:
                _forwarder.Open(forward);
                return new CommandOutcome.Acknowledged($"open-forward {forward.ForwardId} ({forward.Role})");
            default:
                return new CommandOutcome.Acknowledged($"open-forward {forward.ForwardId} (no role; ignored)");
        }
    }

    /// <summary>
    /// §10 agent-started processes. The profile gate is enforced <b>here on the machine</b>,
    /// not on the plane — a profile's meaning is machine-local data the control plane never
    /// learns (§10), so the plane relays and the runner decides.
    /// </summary>
    private async Task<CommandOutcome> HandleStartProcessAsync(StartProcessCommand start, CancellationToken ct)
    {
        var profile = _supervisor.ProfileFor(start.Session) is { } declared ? _config.Resolve(declared) : null;

        // No supervised task means no profile to consult, so no policy can be applied. Refuse
        // rather than guess.
        if (_processes is null || profile is null)
        {
            _ring.Enqueue(new ProcessStartedEvent(
                start.Session, start.RequestId, start.Name, Started: false,
                Refusal: ProcessRefusals.ProfileNotPermitted));
            return new CommandOutcome.Acknowledged(
                $"start-process {start.Name} refused (no supervised task on this machine)");
        }

        var outcome = await _processes.StartProcessAsync(start, profile, ct);
        _ring.Enqueue(outcome switch
        {
            ProcessOutcome.StartedOk ok => new ProcessStartedEvent(
                start.Session, start.RequestId, start.Name, true, null, ok.LogPath),
            ProcessOutcome.RefusedOutcome no => new ProcessStartedEvent(
                start.Session, start.RequestId, start.Name, false, no.Refusal),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        });
        return new CommandOutcome.Acknowledged($"start-process {start.Name}");
    }

    /// <summary>
    /// §10: stop a process. Machine-scoped on purpose — the worker sent to clean up is a
    /// continuation with a different task id, so a task-scoped stop would be unusable by the
    /// very agent dispatched to tidy.
    /// </summary>
    private async Task<CommandOutcome> HandleStopProcessAsync(StopProcessCommand stop, CancellationToken ct)
    {
        var outcome = _processes is null
            ? ProcessOutcome.Refused(ProcessRefusals.NoSuchProcess, "this machine supervises no processes")
            : await _processes.StopProcessAsync(stop.Name, ct);
        _ring.Enqueue(outcome switch
        {
            ProcessOutcome.StoppedOk ok => new ProcessStoppedEvent(
                stop.Session, stop.RequestId, stop.Name, true, ok.ExitCode),
            ProcessOutcome.RefusedOutcome no => new ProcessStoppedEvent(
                stop.Session, stop.RequestId, stop.Name, false, null, no.Refusal),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        });
        return new CommandOutcome.Acknowledged($"stop-process {stop.Name}");
    }

    /// <summary>§10: write to a process's stdin — the same held-open pipe a message-mode worker
    /// stop injects a turn into.</summary>
    private async Task<CommandOutcome> HandleWriteProcessAsync(WriteProcessCommand write, CancellationToken ct)
    {
        var outcome = _processes is null
            ? ProcessOutcome.Refused(ProcessRefusals.NoSuchProcess, "this machine supervises no processes")
            : await _processes.WriteProcessAsync(write.Name, write.Data, write.AppendNewline, ct);
        _ring.Enqueue(outcome switch
        {
            ProcessOutcome.WrittenOk ok => new ProcessWrittenEvent(
                write.Session, write.RequestId, write.Name, true, ok.Bytes),
            ProcessOutcome.RefusedOutcome no => new ProcessWrittenEvent(
                write.Session, write.RequestId, write.Name, false, 0, no.Refusal),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        });
        return new CommandOutcome.Acknowledged($"write-process {write.Name}");
    }

    /// <summary>
    /// §12 serving: read one range and reply with exactly one
    /// <see cref="TranscriptChunkEvent"/>. Two rules hold this together, and neither is
    /// an optimization to be tidied away:
    ///
    /// <para><b>1. The reply bypasses the outbound ring.</b> The ring is bounded
    /// drop-oldest with a gap marker (§10 buffering) — right for liveness events, wrong
    /// twice over for transcript bytes: a dropped chunk is a silently corrupted read, and
    /// a few hundred chunks would evict real <c>alive</c>/<c>exited</c> events from a
    /// shared buffer. Publishing straight to the channel keeps transcript traffic out of
    /// the liveness path entirely (§10 channel separation).</para>
    ///
    /// <para><b>2. The read runs detached.</b> Both a disk read and the channel write
    /// happen off the command handler, because the handler runs on the control socket's
    /// receive loop: awaiting a chunk's send here would delay every inbound command
    /// behind it — including a <c>kill</c>, which is precisely the broken escalation path
    /// §10 warns about. So this returns immediately, exactly like
    /// <see cref="HandleOpenForward"/>.</para>
    ///
    /// A best-effort send that finds no live connection is dropped and never retried; the
    /// plane's own bounded wait surfaces it as a failed read (§10).
    /// </summary>
    private CommandOutcome HandleReadTranscript(ReadTranscriptCommand read)
    {
        if (_transcripts is null)
            return new CommandOutcome.Acknowledged($"read-transcript {read.RequestId} (serving unavailable; ignored)");

        var reading = Task.Run(async () =>
        {
            try
            {
                var reply = _transcripts.Read(read);
                await _channel.PublishAsync(reply, gapBefore: 0, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutting down mid-read; the plane's wait expires on its own.
            }
        });

        // Tracked so shutdown can join in-flight reads. Registered before the
        // continuation is attached, and the continuation runs even if the task has
        // already finished, so neither ordering leaks an entry.
        _transcriptReads[reading] = 0;
        _ = reading.ContinueWith(
            t => _transcriptReads.TryRemove(t, out _),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return new CommandOutcome.Acknowledged($"read-transcript {read.RequestId}");
    }

    private CommandOutcome HandleDispatch(DispatchCommand dispatch)
    {
        // §10: stop accepting dispatch under pressure. The heartbeat also shows
        // the machine as saturated, but refusing here is the backstop.
        if (_backPressure.UnderPressure())
        {
            _log?.Invoke($"dispatch {dispatch.Session} refused: machine is under back-pressure");
            return new CommandOutcome.Refused(RefuseReason.BackPressure, "machine is under back-pressure");
        }

        var profile = _config.Resolve(dispatch.Profile);
        if (profile is null)
        {
            var unknown = $"no profile '{dispatch.Profile}' declared";
            _log?.Invoke($"dispatch {dispatch.Session} refused: {unknown}");
            return new CommandOutcome.Refused(RefuseReason.UnknownProfile, unknown);
        }

        var task = _supervisor.Spawn(dispatch, profile, _machineId);
        _log?.Invoke($"dispatch {dispatch.Session} profile={profile.Name}");
        return new CommandOutcome.Accepted(task);
    }

    private void EmitHeartbeat()
    {
        var reading = _backPressure.Evaluate();
        var heartbeat = new MachineHeartbeat(
            _machineId,
            Ready: !reading.UnderPressure,
            UnderBackPressure: reading.UnderPressure,
            reading.Load,
            _supervisor.RunningTotal,
            // §7, §10: the machine's declared profiles ride the heartbeat — the
            // only channel by which the control plane learns them for routing.
            _config.DeclaredProfiles.ToArray(),
            _clock.GetUtcNow(),
            // §12: whether this runner can answer read-transcript at all, so the
            // dashboard offers a transcript link only where one can be served rather
            // than one that silently times out against an older runner.
            TranscriptsServable: _transcripts is not null,
            Processes: _processes?.ReportProcesses());
        // Best-effort, fire-and-forget: never queue a command against the runner (§10).
        _ = _channel.HeartbeatAsync(heartbeat, _cts.Token);
    }

    /// <summary>
    /// §10 per-task liveness, process-alive half: one <c>alive</c> per supervised task
    /// whose process still exists. This is the only way the fact reaches the plane —
    /// the machine heartbeat is machine-scoped and refreshes no task's clock, and a
    /// worker's own MCP calls do not either. Without it, a profile with no
    /// <c>tool-call</c> source refreshes per-task liveness exactly once (at
    /// <c>started</c>) and every task outliving the plane's window is requeued
    /// forever; with it, an idle-but-alive worker — a long build, a service being
    /// babysat — survives, while the plane's separate no-progress ceiling still
    /// catches a wedged one.
    ///
    /// <para>Goes through the ring like any other event, so it is ordered with them,
    /// subject to the same drop-oldest bound, and needed no new wire member: <c>alive</c> sat
    /// in the frozen vocabulary with no producer until this method became one.</para>
    /// </summary>
    private void EmitAliveEvents()
    {
        var now = _clock.GetUtcNow();
        foreach (var task in _supervisor.LiveSessions)
            _ring.Enqueue(new AliveEvent(task, now));
    }

    /// <summary>
    /// Drains the ring to the control-plane channel, in order, one event at a time.
    ///
    /// <para><b>A read is not finished until the publish is.</b> The channel is
    /// best-effort against a live connection (§10): when the socket is not open it
    /// returns <c>false</c> — never throws, never queues — because buffering is
    /// explicitly this ring's job. So a refused publish <em>parks</em> the pump on the
    /// item it just drained and retries that same item; dropping it here is how the
    /// events the plane's recovery is built on would go missing with no trace — a lost
    /// <c>session-started</c> silently degrades resume to a cold start, a lost
    /// <c>exited</c> waits out the liveness window and burns a requeue, a lost
    /// <c>forward-opened</c> fails a relay open on the waiter TTL. It also covers the
    /// start ordering: <c>rebooted</c> is enqueued before anything has dialed
    /// (<c>landbridged</c> starts the daemon, then the socket), and now waits for the
    /// first connection instead of being published into a null socket.</para>
    ///
    /// <para>Overflow while parked is still bounded — the ring keeps dropping oldest and
    /// the next survivor carries the gap marker — so an outage longer than the buffer
    /// loses events with an accounted-for gap rather than silently. Head-of-line
    /// blocking is the deliberate trade: order holds, and nothing behind a held event
    /// overtakes it.</para>
    ///
    /// <para>At-most-once is preserved in the cases that matter: the real channel returns
    /// false either when nothing was sent (socket not open) or when the send faulted the
    /// socket, so a retry re-sends at most the one frame that died with the old
    /// connection, never an event the plane already accepted.</para>
    ///
    /// <para>The retry sleeps on the INJECTED clock, so a test that hands the daemon a
    /// fake time provider must advance it for a parked pump to wake — the same contract
    /// as <see cref="WebSocketControlPlaneChannel"/>'s reconnect backoff.</para>
    /// </summary>
    private async Task PumpRingAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _ring.ReadAllAsync(ct))
            {
                while (!await _channel.PublishAsync(item.Event, item.GapBefore, ct))
                    await Task.Delay(PublishRetryInterval, _clock, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown. An event still held here is one nobody is waiting
            // on: the machine is going away, and the next generation re-announces
            // itself with rebooted (§10 runner restart).
        }
    }

    /// <summary>
    /// §10 runner restart: on clean shutdown, kill every harness it started.
    /// Stops the timer and drains the ring.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _supervisor.KillAll();

        // §8.3/§10: tearing the machine down also tears down every live relay
        // tunnel it owns. Done before the ring completes so each forward's final
        // forward-closed can still drain.
        await _forwarder.DisposeAsync();

        // §12: join any in-flight transcript read so shutdown does not race a file
        // handle or a channel write. They are short and bounded by MaxBytes; a read
        // whose send is already gone completes on its own.
        try { await Task.WhenAll(_transcriptReads.Keys); }
        catch (Exception) { /* a read that failed or was cancelled is not a shutdown error */ }

        if (_heartbeatTimer is not null)
            await _heartbeatTimer.DisposeAsync();

        _ring.Complete();
        await _cts.CancelAsync();
        if (_pump is not null)
        {
            try { await _pump; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
