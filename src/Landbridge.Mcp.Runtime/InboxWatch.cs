using System.Runtime.CompilerServices;
using Landbridge.ControlPlane;
using Landbridge.Core;
using ModelContextProtocol;

namespace Landbridge.Mcp;

/// <summary>
/// Snapshot-on-wake for Lead and worker inbox. Hub SSE is the doorbell when
/// <c>Landbridge:HubUrl</c> is set; <see cref="SessionEventFanout"/> remains
/// the test-host path. The worker snapshot may POST the receipt to Core.
/// </summary>
public static class InboxWatch
{
    public static async IAsyncEnumerable<LeadInboxView> Lead(
        SessionStore store,
        HubClient? hub,
        string? bearer,
        SessionEventFanout? fanout,
        TeamId team,
        string teamId,
        IReadOnlyList<Guid>? sessionIds,
        Actor? actor,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var filter = sessionIds is { Count: > 0 }
            ? sessionIds.Where(id => id != Guid.Empty).ToHashSet()
            : null;

        async Task<LeadInboxView> Snapshot(CancellationToken token)
        {
            if (filter is null
                && hub is { Enabled: true }
                && bearer is { Length: > 0 }
                && await hub.GetAsync<List<HubSessionListItem>>(
                    $"/sessions?teamId={Uri.EscapeDataString(teamId)}", bearer, token) is { } rows)
                return HubPackager.InboxIdentifiers(rows);
            return await store.GetLeadInboxAsync(team, sessionIds, token, actor);
        }

        if (hub is { Enabled: true } && bearer is { Length: > 0 })
        {
            await foreach (var snap in OnHub(hub, bearer, "/sessions/events", filter, Snapshot, ct))
                yield return snap;
            yield break;
        }

        if (fanout is null)
            throw new McpException("the inbox feed is not available in this process.");
        await foreach (var snap in LeadInboxWatch.Snapshots(store, fanout, team, sessionIds, actor, ct))
            yield return snap;
    }

    public static async IAsyncEnumerable<WorkerInboxView> Worker(
        HubClient? hub,
        string? bearer,
        SessionEventFanout? fanout,
        WorkerCaller caller,
        Func<CancellationToken, Task<WorkerInboxView>> snapshot,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (hub is { Enabled: true } && bearer is { Length: > 0 })
        {
            await foreach (var snap in OnHub(
                hub, bearer, $"/sessions/{caller.Session.Value:D}/events", filter: null, snapshot, ct))
                yield return snap;
            yield break;
        }

        if (fanout is null)
            throw new McpException("the inbox feed is not available in this process.");
        using var sub = fanout.Subscribe(caller.Session.Value);
        yield return await snapshot(ct);
        await foreach (var _ in sub.Reader.ReadAllAsync(ct))
            yield return await snapshot(ct);
    }

    internal static async IAsyncEnumerable<T> OnHub<T>(
        HubClient hub,
        string bearer,
        string path,
        IReadOnlySet<Guid>? filter,
        Func<CancellationToken, Task<T>> snapshot,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var wakes = hub.WatchLiveAsync(path, bearer, ct).GetAsyncEnumerator(ct);
        if (!await wakes.MoveNextAsync())
            yield break;
        yield return await snapshot(ct);
        while (await wakes.MoveNextAsync())
        {
            var change = wakes.Current;
            if (change.IsLiveOpen)
                continue;
            if (filter is { Count: > 0 }
                && change.EntityId is { } id
                && !filter.Contains(id))
                continue;
            yield return await snapshot(ct);
        }
    }
}
