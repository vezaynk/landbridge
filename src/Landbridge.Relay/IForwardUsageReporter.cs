namespace Docket.Relay;

/// <summary>
/// Where a forward's byte count goes (spec §9 check 10 / §9.10). The relay counts bytes it
/// splices and hands the totals upstream; it does not interpret them, hold policy about them,
/// or gate anything on them — it moves opaque bytes (design principle 1), and now says how
/// many.
///
/// <para>An abstraction rather than a direct HTTP call for the same reason
/// <see cref="IGrantValidator"/> is one: a relay with no control plane configured must run
/// perfectly well, so the default implementation counts nothing and the real one is registered
/// only when a plane URL exists.</para>
/// </summary>
public interface IForwardUsageReporter
{
    /// <summary>
    /// The counter a forward accumulates into. Resolved <b>once</b> when a forward is created,
    /// never per frame: the splice pump is the hottest loop in the system, so it must not pay a
    /// dictionary lookup or an allocation per 64 KiB frame — it does one interlocked add on a
    /// field of the object returned here.
    /// </summary>
    ForwardByteCounter CounterFor(string forwardId);

    /// <summary>
    /// Send whatever has accumulated and reset the counters. Called on a timer and once more
    /// when a splice ends, so a long-lived forward is visible while it runs rather than only
    /// when it finishes. <b>Best-effort:</b> never throws, and a failure drops that interval's
    /// bytes rather than retrying — losing this costs visibility, never correctness (§9.10).
    /// </summary>
    Task FlushAsync(CancellationToken ct);
}

/// <summary>
/// One forward's accumulated byte count. A mutable class, deliberately: the pump holds a
/// reference and does <c>Interlocked.Add</c> on <see cref="Bytes"/>, which is why this is not a
/// struct or a dictionary entry.
/// </summary>
public sealed class ForwardByteCounter
{
    /// <summary>Bytes spliced since the last flush, both directions summed. Read and reset with
    /// <c>Interlocked.Exchange</c>, so a flush racing live traffic loses nothing.</summary>
    public long Bytes;
}

/// <summary>
/// The default: count into a throwaway counter and report nothing. What a relay does with no
/// control plane configured — the same posture as
/// <see cref="StaticSecretGrantValidator"/>'s place in the DI story, except that here the safe
/// default is silence rather than refusal, because byte accounting gates nothing.
/// </summary>
public sealed class NoOpForwardUsageReporter : IForwardUsageReporter
{
    private readonly ForwardByteCounter _sink = new();

    public ForwardByteCounter CounterFor(string forwardId) => _sink;

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}
