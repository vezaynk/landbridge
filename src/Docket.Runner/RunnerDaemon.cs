using System.Collections.Concurrent;
using Docket.Contracts;
using Docket.Core;

namespace Docket.Runner;

/// <summary>The outcome of handling one runner command.</summary>
public abstract record CommandOutcome
{
    private CommandOutcome() { }

    /// <summary>A dispatch was accepted and the harness spawned.</summary>
    public sealed record Accepted(TaskId Task) : CommandOutcome;

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

    /// <summary>The profile's optional <c>max_concurrent</c> cap is reached (§10).</summary>
    MaxConcurrent,

    /// <summary>No such profile is declared — the task is not routable here (§7, §9 check 5).</summary>
    UnknownProfile,
}

/// <summary>
/// Ties the runner together, spec §10. On start it reaps strays and announces
/// <c>rebooted</c>; while running it gates dispatch on back-pressure and any
/// <c>max_concurrent</c> cap, drains the outbound ring to the control-plane
/// channel, and beats a machine heartbeat on its own <see cref="TimeProvider"/>
/// timer. On shutdown it kills everything it started. It holds no persistent
/// state — a restart is a reboot (§10 runner restart).
/// </summary>
public sealed class RunnerDaemon
{
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

    /// <summary>§10 operator-declared services, when this machine declares any. Their
    /// status rides the heartbeat; the daemon itself never interprets it.</summary>
    private readonly ServiceSupervisor? _services;

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
        ServiceSupervisor? services = null)
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
        _services = services;
        // §8.3 data planes: the forwarder owns all live relay tunnels and emits
        // forward-opened/-closed onto the same ring as every other event. The
        // accept-timeout override keeps the consumer plane's bounded wait short in
        // tests; production uses the grant-TTL ceiling.
        // §8.2 refuse-at-dial: the forwarder asks the service supervisor whether a dial
        // target belongs to a declared service that is currently down, and refuses rather
        // than connecting to whatever else may hold the port. Null probe (no declared
        // services) leaves every dial exactly as before.
        _forwarder = new RelayForwarder(
            ring, forwardAcceptTimeout,
            serviceOnPort: services is null ? null : services.IsServiceOnPort);
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
                var ack = await _supervisor.StopAsync(stop.Task, stop.Ttl, stop.Disposition, stop.Reason, ct);
                return new CommandOutcome.Acknowledged($"stop delivered as {ack.Delivery}");

            case KillCommand kill:
                var killed = _supervisor.Kill(kill.Task);
                return new CommandOutcome.Acknowledged(killed ? "killed" : "not running");

            case OpenForwardCommand forward:
                return HandleOpenForward(forward);

            case ReadTranscriptCommand read:
                return HandleReadTranscript(read);

            case StartServiceCommand start:
                return await HandleStartServiceAsync(start, ct);

            default:
                // Unreachable: the hierarchy is closed (§10). Kept explicit so a
                // future member can't be silently ignored.
                throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "outside the runner vocabulary");
        }
    }

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
    /// §10 agent-initiated services: admit, start, and reply. The profile gate is enforced
    /// <b>here on the machine</b>, not on the plane — a profile's meaning is machine-local
    /// data the control plane never learns (§10), so the plane relays the request and the
    /// runner decides.
    /// </summary>
    private async Task<CommandOutcome> HandleStartServiceAsync(StartServiceCommand start, CancellationToken ct)
    {
        var profile = _supervisor.ProfileFor(start.Task) is { } declared ? _config.Resolve(declared) : null;

        // No supervised task means no profile to consult, so no policy can be applied. Refuse
        // rather than guess: a service outliving a task that is not here has no owner.
        if (_services is null || profile is null)
        {
            _ring.Enqueue(new ServiceStartedEvent(
                start.Task, start.RequestId, start.Name, Started: false,
                Refusal: ServiceRefusals.ProfileNotPermitted));
            return new CommandOutcome.Acknowledged(
                $"start-service {start.Name} refused (no supervised task on this machine)");
        }

        var outcome = await _services.StartForTaskAsync(start, profile, ct);
        var evt = outcome switch
        {
            ServiceStartOutcome.StartedOk ok => new ServiceStartedEvent(
                start.Task, start.RequestId, start.Name, Started: true, ok.Port, null, ok.LogPath),
            ServiceStartOutcome.ReclaimedOk ok => new ServiceStartedEvent(
                start.Task, start.RequestId, start.Name, Started: true, ok.Port, null, ok.LogPath),
            ServiceStartOutcome.RefusedOutcome no => new ServiceStartedEvent(
                start.Task, start.RequestId, start.Name, Started: false, null, no.Refusal),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        _ring.Enqueue(evt);
        return new CommandOutcome.Acknowledged(
            $"start-service {start.Name}: {(evt.Started ? "started" : evt.Refusal)}");
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
            return new CommandOutcome.Refused(RefuseReason.BackPressure, "machine is under back-pressure");

        var profile = _config.Resolve(dispatch.Profile);
        if (profile is null)
            return new CommandOutcome.Refused(RefuseReason.UnknownProfile, $"no profile '{dispatch.Profile}' declared");

        if (profile.MaxConcurrent is { } cap && _supervisor.RunningFor(profile.Name) >= cap)
            return new CommandOutcome.Refused(RefuseReason.MaxConcurrent, $"profile '{profile.Name}' at max_concurrent {cap}");

        var task = _supervisor.Spawn(dispatch, profile, _machineId);
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
            // §10/§12: the machine's own view of its declared services, reported for the
            // Machine Group view. Null when this machine declares none, which is a
            // different answer from an empty list.
            Services: _services?.Report());
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
    /// subject to the same drop-oldest bound, and needs no new wire member:
    /// <c>alive</c> has been in the frozen vocabulary all along with no producer.</para>
    /// </summary>
    private void EmitAliveEvents()
    {
        var now = _clock.GetUtcNow();
        foreach (var task in _supervisor.LiveTasks)
            _ring.Enqueue(new AliveEvent(task, now));
    }

    private async Task PumpRingAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _ring.ReadAllAsync(ct))
                await _channel.PublishAsync(item.Event, item.GapBefore, ct);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
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
