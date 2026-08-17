using System.Collections.Concurrent;

namespace Landbridge.Relay;

/// <summary>
/// The relay's in-memory pairing table (spec §8.3, "Relay splices by forward
/// id"). Each forward id maps to at most one <see cref="ForwardEntry"/> holding
/// its consumer and producer tunnels. The entry is created when the first side
/// arrives and removed the moment the splice ends or the wait times out, so a
/// forward id can never leak. Concurrent forward ids are fully independent.
/// </summary>
public sealed class ForwardRegistry(IForwardUsageReporter? usage = null)
{
    private readonly ConcurrentDictionary<string, ForwardEntry> _entries =
        new(StringComparer.Ordinal);

    // §9.10: handed to each entry so its splice pump can count bytes against this forward id.
    // Optional so a directly-constructed registry (tests, a relay with no plane) still works;
    // absent, nothing is counted and nothing is reported.
    private readonly IForwardUsageReporter? _usage = usage;

    /// <summary>Live forward-id count — exposed so tests can assert no leak.</summary>
    public int ActiveForwardCount => _entries.Count;

    /// <summary>
    /// Claim the <paramref name="role"/> slot for <paramref name="forwardId"/>,
    /// creating the entry if this is the first arrival. Returns false if that
    /// role is already taken for this id — the duplicate must be rejected
    /// (spec §8.3). On success the caller owns the slot and must call
    /// <see cref="Release"/> exactly once when done.
    /// </summary>
    public bool TryJoin(string forwardId, RelayRole role, out ForwardEntry entry)
    {
        while (true)
        {
            var candidate = _entries.GetOrAdd(forwardId, id => new ForwardEntry(id, _usage));
            lock (candidate.Gate)
            {
                // Raced with a Release that evicted this instance; the dictionary
                // may now hold a fresh entry (or none) — retry the get-or-add.
                if (candidate.Removed)
                    continue;

                entry = candidate;
                return candidate.TryClaim(role);
            }
        }
    }

    /// <summary>
    /// Remove the entry so the forward id is free again. Idempotent, and removes
    /// only this exact instance, so a fresh entry created for the same id by a
    /// later reconnect is never clobbered.
    /// </summary>
    public void Release(string forwardId, ForwardEntry entry)
    {
        lock (entry.Gate)
            entry.Removed = true;

        _entries.TryRemove(new KeyValuePair<string, ForwardEntry>(forwardId, entry));
    }
}
