using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Docket.Runner;

/// <summary>One item drained from the ring, with the count of events dropped
/// immediately before it — the gap marker (§10 buffering).</summary>
public readonly record struct RingItem(RunnerEvent Event, long GapBefore);

/// <summary>
/// The runner → control-plane buffer, spec §10: <b>a bounded in-memory ring,
/// drop-oldest, record a gap marker</b>. Not disk — that reintroduces the
/// state the restart model eliminated. Depth need only cover the liveness
/// window; past that the control plane has already requeued and the buffered
/// events describe work nobody is waiting on.
///
/// Backed by a bounded <see cref="Channel{T}"/> with
/// <see cref="BoundedChannelFullMode.DropOldest"/>. The <c>itemDropped</c>
/// callback counts drops so the next drained item carries the gap.
/// </summary>
public sealed class OutboundEventRing
{
    private readonly Channel<RunnerEvent> _channel;
    private long _pendingGap;
    private long _totalDropped;

    public OutboundEventRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _channel = Channel.CreateBounded<RunnerEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            itemDropped: _ =>
            {
                Interlocked.Increment(ref _pendingGap);
                Interlocked.Increment(ref _totalDropped);
            });
    }

    /// <summary>Total events ever dropped — a running gap total for diagnostics.</summary>
    public long DroppedCount => Interlocked.Read(ref _totalDropped);

    /// <summary>Never blocks: when full it drops the oldest and records a gap (§10).</summary>
    public void Enqueue(RunnerEvent evt) => _channel.Writer.TryWrite(evt);

    /// <summary>
    /// Drains items in order, each tagged with the number of events dropped
    /// since the previous drain — the gap marker the control plane records.
    /// </summary>
    public async IAsyncEnumerable<RingItem> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
        {
            var gap = Interlocked.Exchange(ref _pendingGap, 0);
            yield return new RingItem(evt, gap);
        }
    }

    /// <summary>Closes the ring for writing; drainers finish and stop (clean shutdown).</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
