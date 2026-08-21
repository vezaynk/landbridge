using System.Runtime.CompilerServices;
using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// Snapshot-on-wake sequence shared by HTTP SSE and the Lead MCP tool.
/// </summary>
public static class LeadInboxWatch
{
    public static IAsyncEnumerable<LeadInboxView> Snapshots(
        SessionStore store,
        SessionEventFanout fanout,
        TeamId team,
        Guid? sessionId,
        CancellationToken ct) =>
        Snapshots(store, fanout, team, sessionId is { } id ? [id] : null, actor: null, ct);

    public static async IAsyncEnumerable<LeadInboxView> Snapshots(
        SessionStore store,
        SessionEventFanout fanout,
        TeamId team,
        IReadOnlyList<Guid>? sessionIds,
        Actor? actor,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var filter = sessionIds is { Count: > 0 }
            ? sessionIds.Where(id => id != Guid.Empty).ToHashSet()
            : null;
        using var sub = fanout.Subscribe(filter);
        yield return await store.GetLeadInboxAsync(team, sessionIds, ct, actor);
        await foreach (var _ in sub.Reader.ReadAllAsync(ct))
            yield return await store.GetLeadInboxAsync(team, sessionIds, ct, actor);
    }
}
