using Landbridge.ControlPlane;
using ModelContextProtocol;

namespace Landbridge.Mcp.Tools;

/// <summary>
/// The one place a <see cref="StoreResult"/> becomes a tool response (spec §10:
/// tools are thin adapters over store transitions). An applied transition
/// describes the new state; every refusal shape — engine rejection, missing
/// row, optimistic-concurrency conflict — maps to an <see cref="McpException"/>
/// so the calling harness sees the store's own reason, uniformly across the
/// Lead and worker tool surfaces.
/// </summary>
internal static class ToolResults
{
    public static string Describe(StoreResult result) => result switch
    {
        StoreResult.Applied a => $"ok: session is now {a.Session.State}",
        _ => throw Rejection(result),
    };

    public static McpException Rejection(StoreResult result) => result switch
    {
        StoreResult.Rejected r => new McpException($"rejected ({r.Rule}): {r.Reason}"),
        StoreResult.NotFound n => new McpException(n.Reason),
        StoreResult.Conflict c => new McpException($"conflict: {c.Reason}"),
        _ => new McpException("unknown store result"),
    };
}
