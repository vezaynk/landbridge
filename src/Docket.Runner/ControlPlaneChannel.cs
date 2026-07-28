namespace Docket.Runner;

/// <summary>
/// The machine-level heartbeat, spec §10: docketd on its own timer. Loss means
/// every task on the machine is suspect. Carries the derived readiness the
/// control plane dispatches against — <c>ready</c> unless under back-pressure,
/// in which case the machine shows as <c>saturated</c> (§10 concurrency).
/// </summary>
public sealed record MachineHeartbeat(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    SystemLoad Load,
    int RunningTasks,
    DateTimeOffset At);

/// <summary>
/// The runner's link to the control plane, spec §10 channel separation.
/// <b>docketd only makes outbound connections and never listens on a network
/// interface</b> (§10) — so this is outbound-only. Delivery is <b>best-effort
/// against a live connection</b>: an implementation returns <c>false</c> when
/// the connection is down and never throws and never queues (the outbound
/// command queue is explicitly not built — §10, §15). Runner→control-plane
/// buffering is the caller's <see cref="OutboundEventRing"/>, not this channel.
///
/// The real wire transport is deferred; tests use
/// <see cref="InMemoryControlPlaneChannel"/>.
/// </summary>
public interface IControlPlaneChannel
{
    /// <summary>Ships one buffered event with its gap marker. Returns delivery success.</summary>
    Task<bool> PublishAsync(RunnerEvent evt, long gapBefore, CancellationToken ct);

    /// <summary>Ships a machine heartbeat. Returns delivery success.</summary>
    Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct);
}

/// <summary>
/// An in-memory <see cref="IControlPlaneChannel"/> for tests and for the
/// daemon skeleton until the real wire lands. Records everything shipped and
/// models a droppable connection via <see cref="Connected"/> — best-effort,
/// never throwing, never queuing.
/// </summary>
public sealed class InMemoryControlPlaneChannel : IControlPlaneChannel
{
    private readonly object _gate = new();
    private readonly List<PublishedEvent> _events = [];
    private readonly List<MachineHeartbeat> _heartbeats = [];

    /// <summary>When false, publishes/heartbeats are dropped (best-effort semantics).</summary>
    public bool Connected { get; set; } = true;

    public IReadOnlyList<PublishedEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public IReadOnlyList<MachineHeartbeat> Heartbeats
    {
        get { lock (_gate) return _heartbeats.ToArray(); }
    }

    public Task<bool> PublishAsync(RunnerEvent evt, long gapBefore, CancellationToken ct)
    {
        if (!Connected)
            return Task.FromResult(false);
        lock (_gate)
            _events.Add(new PublishedEvent(evt, gapBefore));
        return Task.FromResult(true);
    }

    public Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct)
    {
        if (!Connected)
            return Task.FromResult(false);
        lock (_gate)
            _heartbeats.Add(heartbeat);
        return Task.FromResult(true);
    }
}

/// <summary>An event as it reached the channel, with its gap marker.</summary>
public sealed record PublishedEvent(RunnerEvent Event, long GapBefore);
