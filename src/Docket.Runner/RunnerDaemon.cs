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

    private readonly CancellationTokenSource _cts = new();
    private ITimer? _heartbeatTimer;
    private Task? _pump;

    public RunnerDaemon(
        string machineId,
        RunnerConfig config,
        IProcessSupervisor supervisor,
        BackPressureMonitor backPressure,
        IControlPlaneChannel channel,
        OutboundEventRing ring,
        IStrayReaper reaper,
        TimeProvider clock)
    {
        _machineId = machineId;
        _config = config;
        _supervisor = supervisor;
        _backPressure = backPressure;
        _channel = channel;
        _ring = ring;
        _reaper = reaper;
        _clock = clock;
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

        _heartbeatTimer = _clock.CreateTimer(
            _ => EmitHeartbeat(),
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
                // §8.3 relay forwarding internals are deferred; the vocabulary
                // member is handled so an unknown command still can't slip in.
                return new CommandOutcome.Acknowledged($"open-forward {forward.ForwardId} (relay internals deferred)");

            default:
                // Unreachable: the hierarchy is closed (§10). Kept explicit so a
                // future member can't be silently ignored.
                throw new ArgumentOutOfRangeException(nameof(command), command.GetType().Name, "outside the runner vocabulary");
        }
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
            _clock.GetUtcNow());
        // Best-effort, fire-and-forget: never queue a command against the runner (§10).
        _ = _channel.HeartbeatAsync(heartbeat, _cts.Token);
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
