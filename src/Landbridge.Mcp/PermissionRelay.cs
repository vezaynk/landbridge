using Docket.ControlPlane;
using Docket.Core;

namespace Docket.Mcp;

/// <summary>
/// The shared §11 permission-bridge body: open a typed permission request on
/// the incumbent's session, then wait for a Lead or human verdict. Both the MCP
/// <c>request_permission</c> tool (legacy harness hook) and
/// <c>POST /worker/permission</c> (ACP <c>session/request_permission</c>) run
/// this, so the two transports cannot disagree about what a decision means.
/// </summary>
public static class PermissionRelay
{
    public static async Task<PermissionRelayResult> OpenAndAwaitAsync(
        SessionStore store,
        WorkerCaller caller,
        string tool,
        string proposedInput,
        TimeSpan pollInterval,
        TimeProvider clock,
        CancellationToken ct)
    {
        var opened = await store.ApplyAsync(
            caller.Session,
            new RequestInput(caller, InputRequestKind.Permission, proposedInput, tool),
            ct);

        if (opened is not StoreResult.Applied)
            return PermissionRelayResult.Denied(
                "Docket could not put this permission request to your Lead: "
                + Reason(opened)
                + ". Do not retry the same call; if you cannot proceed without it, stop and "
                + "say so in your report.");

        var outcome = await store.AwaitPermissionVerdictAsync(caller, pollInterval, clock, ct);
        if (outcome is null)
            return PermissionRelayResult.Denied(
                "Nobody answered this permission request in time, so Docket stopped waiting and "
                + "the session was parked for a person to pick up. Stop here: do not retry the call "
                + "and do not work around it.");

        return outcome.Verdict == PermissionVerdict.Allow
            ? PermissionRelayResult.Allowed()
            : PermissionRelayResult.Denied(outcome.Message ?? "Denied, with no reason recorded.");
    }

    public static string Reason(StoreResult result) => result switch
    {
        StoreResult.Rejected r => $"{r.Reason} ({r.Rule})",
        StoreResult.NotFound n => n.Reason,
        StoreResult.Conflict c => c.Reason,
        _ => "unknown store result",
    };
}

/// <summary>A plane permission verdict, transport-neutral.</summary>
public sealed record PermissionRelayResult(bool Allow, string Message)
{
    public static PermissionRelayResult Allowed() => new(true, "");
    public static PermissionRelayResult Denied(string message) => new(false, message);
}
