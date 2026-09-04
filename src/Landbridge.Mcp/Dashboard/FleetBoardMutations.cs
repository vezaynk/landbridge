using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Circuit-side mutations for the fleet board. Same services as the HTTP POSTs,
/// without a navigation — the page reloads its snapshot in place.
///
/// Each call opens its own DI scope. The circuit's scoped
/// <see cref="LandbridgeDbContext"/> is already in use by the 2s refresh, and
/// Npgsql refuses a second command on that connection.
/// </summary>
public sealed class FleetBoardMutations(IServiceScopeFactory scopes, IConfiguration config)
{
    public Task<FleetNotice> BindMachineAsync(Guid humanId, Guid machineId, CancellationToken ct) =>
        WithAsync(async (sp, token) => await sp.GetRequiredService<LeadMachineBindingService>()
            .BindAsync(humanId, machineId, token) switch
        {
            LeadMachineBindResult.Bound b => new FleetNotice(
                "Machine bound",
                $"{b.Binding.MachineName} is your box. Non-HTTP forwards will open loopback ports on it."),
            LeadMachineBindResult.Refused r => new FleetNotice("Could not bind", r.Reason, Error: true),
            _ => new FleetNotice("Could not bind", "unknown bind result", Error: true),
        }, ct);

    public Task<FleetNotice> UnbindMachineAsync(Guid humanId, CancellationToken ct) =>
        WithAsync(async (sp, token) =>
        {
            var released = await sp.GetRequiredService<LeadMachineBindingService>().UnbindAsync(humanId, token);
            var msg = released is null
                ? "You had no machine bound."
                : $"Released {released.MachineName}. Forwards will refuse until you bind again.";
            return new FleetNotice("Machine unbound", msg);
        }, ct);

    public Task<FleetNotice> RegisterForwardAsync(Guid sessionId, string name, int port, CancellationToken ct) =>
        WithAsync(async (sp, token) =>
            NoticeOf(await sp.GetRequiredService<SessionStore>()
                .RegisterServiceFromDashboardAsync(new SessionId(sessionId), name, port, token),
                ok: $"Registered {name.Trim()}:{port}."),
            ct);

    public Task<FleetNotice> RevokeForwardAsync(Guid sessionId, string name, CancellationToken ct) =>
        WithAsync(async (sp, token) =>
            NoticeOf(await sp.GetRequiredService<SessionStore>()
                .UnregisterServiceAsync(new SessionId(sessionId), name, token),
                ok: $"Revoked {name}."),
            ct);

    public Task<FleetNotice> BindForwardAsync(Guid humanId, Guid teamId, string serviceName, CancellationToken ct) =>
        WithAsync(async (sp, token) =>
        {
            var bindings = sp.GetRequiredService<LeadMachineBindingService>();
            var bound = await bindings.GetAsync(humanId, token);
            if (bound is null)
                return new FleetNotice("No machine bound",
                    "Bind a machine in the left rail first. For HTTP, create a preview instead.",
                    Error: true);

            var issued = await sp.GetRequiredService<RelayGrantService>()
                .IssueForLeadAsync(new TeamId(teamId), serviceName, token);
            if (issued is not RelayGrantResult.Issued grant)
            {
                var why = issued is RelayGrantResult.Refused r ? r.Reason : "could not issue a grant";
                return new FleetNotice("Forward refused", why, Error: true);
            }

            var opened = await sp.GetRequiredService<ForwardOrchestrator>().EstablishForLeadAsync(
                bound.MachineId.ToString(), grant, serviceName, WorkerTools.RelayUrlFrom(config), token);
            return opened switch
            {
                ForwardEstablishResult.Established e => new FleetNotice(
                    "Forward open",
                    $"One connection, promptly. Connect on the bound machine ({bound.MachineName}).",
                    Detail: $"{WorkerTools.ForwardLoopbackHost}:{e.Port}"),
                ForwardEstablishResult.Failed f => new FleetNotice("Forward failed", f.Reason, Error: true),
                _ => new FleetNotice("Forward failed", "unknown forward result", Error: true),
            };
        }, ct);

    public Task<FleetNotice> PreviewAsync(Guid teamId, Guid sessionId, string serviceName, CancellationToken ct) =>
        WithAsync(async (sp, token) =>
        {
            var mint = await sp.GetRequiredService<PreviewMappingService>().CreateAsync(
                new TeamId(teamId), new SessionId(sessionId), serviceName,
                PreviewAuthPolicy.Gated, PreviewMint.ResolveTtl(PreviewAuthPolicy.Gated, null), token);
            return new FleetNotice(
                "Preview created",
                "Opening this link requires a Landbridge operator session in the browser.",
                Url: PreviewMint.Url(PreviewUrlBase(), mint.Label));
        }, ct);

    public Task<FleetNotice> RevokePreviewAsync(Guid previewId, CancellationToken ct) =>
        WithAsync(async (sp, token) =>
        {
            await sp.GetRequiredService<PreviewMappingService>().RevokeAsync(previewId, token);
            return new FleetNotice("Preview revoked", "New connections to that mapping are refused.");
        }, ct);

    public Task<FleetNotice> SetPreviewPublicAsync(Guid previewId, bool isPublic, CancellationToken ct) =>
        WithAsync(async (sp, token) =>
        {
            var policy = isPublic ? PreviewAuthPolicy.Public : PreviewAuthPolicy.Gated;
            var ok = await sp.GetRequiredService<PreviewMappingService>()
                .SetAuthPolicyAsync(previewId, policy, token);
            if (!ok)
                return new FleetNotice("Not found", "that preview is already gone.", Error: true);
            return isPublic
                ? new FleetNotice("Preview is public", "Anyone with the link can open it.")
                : new FleetNotice("Preview is gated",
                    "Opening this link requires a Landbridge operator session in the browser.");
        }, ct);

    public Task<FleetNotice> RevokeReceiptAsync(Guid forwardId, CancellationToken ct) =>
        WithAsync(async (sp, token) =>
        {
            var closed = await sp.GetRequiredService<RelayGrantService>().CloseConsumerAsync(forwardId, token);
            if (closed is null)
                return new FleetNotice("Not found", "that forward is already closed.", Error: true);
            var (producer, consumer, name) = closed.Value;
            await sp.GetRequiredService<ForwardTeardownService>().CloseAsync(
                [new ForwardTeardown(producer, forwardId.ToString(), consumer)], token);
            return new FleetNotice("Forward closed", $"Closed the receiving end of {name}.");
        }, ct);

    private static FleetNotice NoticeOf(StoreResult result, string ok) => result switch
    {
        StoreResult.Applied => new FleetNotice("Done", ok),
        StoreResult.Rejected r => new FleetNotice("Refused", r.Reason, Error: true),
        StoreResult.NotFound n => new FleetNotice("Not found", n.Reason, Error: true),
        StoreResult.Conflict c => new FleetNotice("Conflict", c.Reason, Error: true),
        _ => new FleetNotice("Failed", "unknown store result", Error: true),
    };

    private async Task<FleetNotice> WithAsync(
        Func<IServiceProvider, CancellationToken, Task<FleetNotice>> work, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await work(scope.ServiceProvider, ct);
    }

    private string PreviewUrlBase() =>
        config[PreviewMint.UrlBaseConfigKey]
        ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PREVIEW_URL_BASE")
        ?? "http://preview.localhost";
}

public sealed record FleetNotice(
    string Title,
    string Message,
    string? Detail = null,
    string? Url = null,
    bool Error = false);
