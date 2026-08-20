using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// Snapshot-on-wake sequence shared by HTTP SSE and the Lead MCP tool.
/// </summary>
public static class LeadInboxWatch
{
    public static async IAsyncEnumerable<LeadInboxView> Snapshots(
        SessionStore store,
        SessionEventFanout fanout,
        TeamId team,
        Guid? sessionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var sub = fanout.Subscribe(sessionId);
        yield return await store.GetLeadInboxAsync(team, sessionId, ct);
        await foreach (var _ in sub.Reader.ReadAllAsync(ct))
            yield return await store.GetLeadInboxAsync(team, sessionId, ct);
    }
}
