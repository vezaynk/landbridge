using System.Collections.Concurrent;
using Landbridge.Contracts;

namespace Landbridge.ControlPlane;

/// <summary>
/// In-memory rendezvous between a transcript range request in flight and the machine's
/// <c>transcript-chunk</c> reply (spec §12 serving), the direct analog of
/// <see cref="ForwardWaiters"/>: the relay service registers a waiter keyed by request id
/// and parks on it; the <see cref="RunnerEventSink"/> completes it the moment the reply
/// arrives.
///
/// <para><b>Completing must never block.</b> The sink runs on the runner socket's receive
/// loop, so anything it awaits delays every other inbound frame — heartbeats and
/// <c>alive</c> events included, which the liveness scan requeues tasks over. So
/// <see cref="Complete"/> is a <c>TrySetResult</c> and nothing more; back-pressure lives
/// in the reader's cursor (it simply does not ask for the next range), never here.</para>
///
/// <para>One outstanding request per id, and the id is fresh per range, so there is no
/// queue to bound. A reply nobody is waiting for — a late arrival after the requester
/// gave up — is dropped: the waiter is gone and there is nothing to correlate it to.</para>
/// </summary>
public sealed class TranscriptWaiters
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TranscriptChunkEvent>> _waiters =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Register interest in <paramref name="requestId"/>. Dispose the returned waiter (a
    /// <c>using</c> in the caller) to deregister — completed, timed out, or abandoned — so
    /// the table never leaks.
    /// </summary>
    public Waiter Register(string requestId)
    {
        var tcs = new TaskCompletionSource<TranscriptChunkEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[requestId] = tcs;
        return new Waiter(this, requestId, tcs.Task);
    }

    /// <summary>
    /// Hand a machine's reply to whoever is waiting for it. Returns false when nobody is —
    /// a late or unsolicited reply, which is dropped rather than treated as an error.
    /// Never blocks (see the class remarks).
    /// </summary>
    public bool Complete(TranscriptChunkEvent reply) =>
        _waiters.TryGetValue(reply.RequestId, out var tcs) && tcs.TrySetResult(reply);

    private void Remove(string requestId) => _waiters.TryRemove(requestId, out _);

    /// <summary>Whether any request is currently outstanding (diagnostics/tests).</summary>
    public int OutstandingCount => _waiters.Count;

    /// <summary>A registered interest in one request id's reply.</summary>
    public sealed class Waiter(TranscriptWaiters owner, string requestId, Task<TranscriptChunkEvent> reply) : IDisposable
    {
        /// <summary>Completes with the machine's reply for this request id.</summary>
        public Task<TranscriptChunkEvent> Reply => reply;

        public void Dispose() => owner.Remove(requestId);
    }
}
