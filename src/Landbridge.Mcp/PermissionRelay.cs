using Landbridge.ControlPlane;
using Landbridge.Core;

namespace Landbridge.Mcp;

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
        CancellationToken ct,
        IPermissionClassifier? classifier = null,
        string? optionsJson = null)
    {
        if (PermissionPolicy.Classify(tool, proposedInput, caller.Session) == PermissionDisposition.AutoAllow)
            return PermissionRelayResult.Allowed();

        if (classifier is not null)
        {
            IReadOnlyList<string> leadMessages = [];
            try
            {
                leadMessages = await store
                    .GetLeadWorkerMessagesAsync(caller.Session, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Context is best-effort: a read miss still classifies, just
                // without the brief. Fail-closed on the classify call itself.
            }

            PermissionDisposition classified;
            try
            {
                classified = await classifier
                    .ClassifyAsync(caller.Session, tool, proposedInput, leadMessages, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                classified = PermissionDisposition.Ask;
            }

            if (classified == PermissionDisposition.AutoAllow)
            {
                try
                {
                    await store.RecordClassifierAllowAsync(caller.Session, tool, proposedInput, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Audit is best-effort: the call is still allowed.
                }
                return PermissionRelayResult.Allowed();
            }
        }

        var opened = await store.ApplyAsync(
            caller.Session,
            new RequestInput(caller, InputRequestKind.Permission, proposedInput, tool, optionsJson),
            ct);

        if (opened is not StoreResult.Applied)
            return PermissionRelayResult.Denied(
                "Landbridge could not put this permission request to your Lead: "
                + Reason(opened)
                + ". Do not retry the same call; if you cannot proceed without it, stop and "
                + "say so in your report.");

        var outcome = await store.AwaitPermissionVerdictAsync(caller, pollInterval, clock, ct);
        if (outcome is null)
            return PermissionRelayResult.Denied(
                "Nobody answered this permission request in time, so Landbridge stopped waiting and "
                + "the session was parked for a person to pick up. Stop here: do not retry the call "
                + "and do not work around it.");

        return outcome.Verdict == PermissionVerdict.Allow
            ? PermissionRelayResult.Allowed(outcome.OptionId)
            : PermissionRelayResult.Denied(
                outcome.Message ?? "Denied, with no reason recorded.", outcome.OptionId);
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
public sealed record PermissionRelayResult(bool Allow, string Message, string? OptionId = null)
{
    public static PermissionRelayResult Allowed(string? optionId = null) => new(true, "", optionId);
    public static PermissionRelayResult Denied(string message, string? optionId = null) =>
        new(false, message, optionId);
}
