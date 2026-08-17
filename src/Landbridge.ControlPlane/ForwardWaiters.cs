using System.Collections.Concurrent;

namespace Landbridge.ControlPlane;

/// <summary>
/// In-memory rendezvous between an <c>open_forward</c> call in flight and the
/// consumer landbridged's <c>forward-opened</c> event (spec §8.3). The orchestrator
/// registers a waiter keyed by forward id and parks on it; the
/// <see cref="RunnerEventSink"/> completes it with the bound loopback port the
/// moment the event arrives. Kept in-memory and simple — a forward that never
/// opens is bounded by the orchestrator's own wait, and a control-plane restart
/// drops in-flight forwards along with every other volatile connection fact
/// (§10 single control plane for v1), the same spirit as the dispatch nudge.
/// </summary>
public sealed class ForwardWaiters
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<int>> _waiters =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Register interest in <paramref name="forwardId"/>. Dispose the returned
    /// waiter (a <c>using</c> in the orchestrator) to deregister — whether it
    /// completed, failed, or the caller gave up — so the table never leaks.
    /// </summary>
    public Waiter Register(string forwardId)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[forwardId] = tcs;
        return new Waiter(this, forwardId, tcs.Task);
    }

    /// <summary>Complete the waiter with the consumer's bound port (from <c>forward-opened</c>).</summary>
    public void Complete(string forwardId, int port)
    {
        if (_waiters.TryGetValue(forwardId, out var tcs))
            tcs.TrySetResult(port);
    }

    /// <summary>
    /// Fail any still-pending waiter — a <c>forward-closed</c> before the forward
    /// ever opened (e.g. the producer's service was dead). Harmless once the
    /// waiter has completed and been removed: it is simply not found.
    /// </summary>
    public void Fail(string forwardId, string reason)
    {
        if (_waiters.TryGetValue(forwardId, out var tcs))
            tcs.TrySetException(new ForwardClosedBeforeOpenException(reason));
    }

    private void Remove(string forwardId) => _waiters.TryRemove(forwardId, out _);

    /// <summary>A registered interest in one forward id's <c>forward-opened</c> event.</summary>
    public sealed class Waiter(ForwardWaiters owner, string forwardId, Task<int> opened) : IDisposable
    {
        /// <summary>Completes with the consumer's bound loopback port.</summary>
        public Task<int> Opened => opened;

        public void Dispose() => owner.Remove(forwardId);
    }
}

/// <summary>Raised into a waiter when a forward closes before it ever opened (§8.3).</summary>
public sealed class ForwardClosedBeforeOpenException(string message) : Exception(message);
