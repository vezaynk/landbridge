using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp;
using Landbridge.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Circuit-side mutations for the fleet board. Same services as the HTTP POSTs,
/// without a navigation — the page reloads its snapshot in place.
///
/// Each call opens its own DI scope. The circuit's scoped
/// <see cref="LandbridgeDbContext"/> is already in use by the Hub refetch, and
/// Npgsql refuses a second command on that connection. When
/// <c>Landbridge:CoreUrl</c> is set the mutation POSTs to Core with the
/// operator Bearer; tests without CoreUrl still Apply in-process.
/// </summary>
public sealed class FleetBoardMutations(IServiceScopeFactory scopes, IConfiguration config)
{
    public Task<FleetNotice> BindMachineAsync(Guid humanId, Guid machineId, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) => await sp.GetRequiredService<LeadMachineBindingService>()
                .BindAsync(humanId, machineId, token) switch
            {
                LeadMachineBindResult.Bound b => new FleetNotice(
                    "Machine bound",
                    $"{b.Binding.MachineName} is your box. Non-HTTP forwards will open loopback ports on it."),
                LeadMachineBindResult.Refused r => new FleetNotice("Could not bind", r.Reason, Error: true),
                _ => new FleetNotice("Could not bind", "unknown bind result", Error: true),
            },
            async (core, token, ct2) =>
            {
                var via = await core.PostAsync("/core/v1/bind-machine", token, new CoreBindBody(machineId.ToString("D")), ct2);
                return via.Status == "applied"
                    ? new FleetNotice("Machine bound", via.Reason ?? "bound")
                    : new FleetNotice("Could not bind", via.Reason ?? "refused", Error: true);
            },
            ct);

    public Task<FleetNotice> UnbindMachineAsync(Guid humanId, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) =>
            {
                var released = await sp.GetRequiredService<LeadMachineBindingService>().UnbindAsync(humanId, token);
                var msg = released is null
                    ? "You had no machine bound."
                    : $"Released {released.MachineName}. Forwards will refuse until you bind again.";
                return new FleetNotice("Machine unbound", msg);
            },
            async (core, token, ct2) =>
            {
                var via = await core.PostAsync("/core/v1/unbind-machine", token, new { }, ct2);
                return new FleetNotice("Machine unbound", via.Reason ?? "unbound");
            },
            ct);

    public Task<FleetNotice> RegisterForwardAsync(Guid sessionId, string name, int port, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) =>
                NoticeOf(await sp.GetRequiredService<SessionStore>()
                    .RegisterServiceFromDashboardAsync(new SessionId(sessionId), name, port, token),
                    ok: $"Registered {name.Trim()}:{port}."),
            async (core, token, ct2) => FromStore(
                await core.PostAsync($"/core/v1/sessions/{sessionId:D}/register-service", token,
                    new CoreSessionBody("", Name: name, Port: port), ct2),
                $"Registered {name.Trim()}:{port}."),
            ct);

    public Task<FleetNotice> RevokeForwardAsync(Guid sessionId, string name, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) =>
                NoticeOf(await sp.GetRequiredService<SessionStore>()
                    .UnregisterServiceAsync(new SessionId(sessionId), name, token),
                    ok: $"Revoked {name}."),
            async (core, token, ct2) => FromStore(
                await core.PostAsync($"/core/v1/sessions/{sessionId:D}/unregister-service", token,
                    new CoreSessionBody("", Name: name), ct2),
                $"Revoked {name}."),
            ct);

    public Task<FleetNotice> BindForwardAsync(Guid humanId, Guid teamId, string serviceName, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) =>
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
                    bound.MachineId, grant, serviceName, WorkerTools.RelayUrlFrom(config), token);
                return opened switch
                {
                    ForwardEstablishResult.Established e => new FleetNotice(
                        "Forward open",
                        $"One connection, promptly. Connect on the bound machine ({bound.MachineName}).",
                        Detail: $"{WorkerTools.ForwardLoopbackHost}:{e.Port}"),
                    ForwardEstablishResult.Failed f => new FleetNotice("Forward failed", f.Reason, Error: true),
                    _ => new FleetNotice("Forward failed", "unknown forward result", Error: true),
                };
            },
            async (core, token, ct2) =>
            {
                var via = await core.PostAsAsync<CoreForwardReply>("/core/v1/forwards/lead", token,
                    new CoreForwardBody(serviceName, teamId.ToString("D")), ct2);
                if (via is { Ok: true, Port: { } port })
                    return new FleetNotice(
                        "Forward open",
                        "One connection, promptly. Connect on the bound machine.",
                        Detail: $"{WorkerTools.ForwardLoopbackHost}:{port}");
                return new FleetNotice(
                    via?.Reason == "no machine bound" ? "No machine bound" : "Forward refused",
                    via?.Reason ?? "failed", Error: true);
            },
            ct);

    public Task<FleetNotice> PreviewAsync(Guid teamId, Guid sessionId, string serviceName, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) =>
            {
                var mint = await sp.GetRequiredService<PreviewMappingService>().CreateAsync(
                    new TeamId(teamId), new SessionId(sessionId), serviceName,
                    PreviewAuthPolicy.Gated, PreviewMint.ResolveTtl(PreviewAuthPolicy.Gated, null), token);
                return new FleetNotice(
                    "Preview created",
                    "Opening this link requires a Landbridge operator session in the browser.",
                    Url: PreviewMint.Url(PreviewUrlBase(), mint.Label));
            },
            async (core, token, ct2) =>
            {
                var via = await core.PostAsAsync<CorePreviewReply>("/core/v1/previews", token,
                    new CorePreviewMintBody(serviceName, TeamId: teamId.ToString("D"), SessionId: sessionId.ToString("D")), ct2);
                return via is { Ok: true, Url: { } url }
                    ? new FleetNotice(
                        "Preview created",
                        "Opening this link requires a Landbridge operator session in the browser.",
                        Url: url)
                    : new FleetNotice("Failed", via?.Reason ?? "preview mint failed", Error: true);
            },
            ct);

    public Task<FleetNotice> RevokePreviewAsync(Guid previewId, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) =>
            {
                await sp.GetRequiredService<PreviewMappingService>().RevokeAsync(previewId, token);
                return new FleetNotice("Preview revoked", "New connections to that mapping are refused.");
            },
            async (core, token, ct2) =>
            {
                await core.PostAsAsync<CorePreviewReply>("/core/v1/previews/patch", token,
                    new CorePreviewPatchBody(previewId, Revoke: true), ct2);
                return new FleetNotice("Preview revoked", "New connections to that mapping are refused.");
            },
            ct);

    public Task<FleetNotice> SetPreviewPublicAsync(Guid previewId, bool isPublic, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) =>
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
            },
            async (core, token, ct2) =>
            {
                var via = await core.PostAsAsync<CorePreviewReply>("/core/v1/previews/patch", token,
                    new CorePreviewPatchBody(previewId, IsPublic: isPublic), ct2);
                if (via is not { Ok: true })
                    return new FleetNotice("Not found", via?.Reason ?? "that preview is already gone.", Error: true);
                return isPublic
                    ? new FleetNotice("Preview is public", "Anyone with the link can open it.")
                    : new FleetNotice("Preview is gated",
                        "Opening this link requires a Landbridge operator session in the browser.");
            },
            ct);

    public Task<FleetNotice> RevokeReceiptAsync(Guid forwardId, string? bearer, CancellationToken ct) =>
        WithAsync(bearer,
            async (sp, token) =>
            {
                var closed = await sp.GetRequiredService<RelayGrantService>().CloseConsumerAsync(forwardId, token);
                if (closed is null)
                    return new FleetNotice("Not found", "that forward is already closed.", Error: true);
                var (producer, consumer, name) = closed.Value;
                await sp.GetRequiredService<ForwardTeardownService>().CloseAsync(
                    [new ForwardTeardown(producer, forwardId.ToString(), consumer)], token);
                return new FleetNotice("Forward closed", $"Closed the receiving end of {name}.");
            },
            async (core, token, ct2) =>
            {
                var via = await core.PostAsAsync<CoreCloseForwardReply>("/core/v1/forwards/close", token,
                    new CoreCloseForwardBody(forwardId), ct2);
                if (via is not { Ok: true })
                    return new FleetNotice("Not found", via?.Reason ?? "that forward is already closed.", Error: true);
                return new FleetNotice("Forward closed",
                    $"Closed the receiving end of {via.ServiceName}.");
            },
            ct);

    private static FleetNotice NoticeOf(StoreResult result, string ok) => result switch
    {
        StoreResult.Applied => new FleetNotice("Done", ok),
        StoreResult.Rejected r => new FleetNotice("Refused", r.Reason, Error: true),
        StoreResult.NotFound n => new FleetNotice("Not found", n.Reason, Error: true),
        StoreResult.Conflict c => new FleetNotice("Conflict", c.Reason, Error: true),
        _ => new FleetNotice("Failed", "unknown store result", Error: true),
    };

    private static FleetNotice FromStore(CoreStoreReply via, string ok) => via.Status == "applied"
        ? new FleetNotice("Done", via.Reason ?? ok)
        : new FleetNotice(via.Status == "not_found" ? "Not found" : "Refused", via.Reason ?? via.Status, Error: true);

    private async Task<FleetNotice> WithAsync(
        string? bearer,
        Func<IServiceProvider, CancellationToken, Task<FleetNotice>> local,
        Func<CoreWriteClient, string, CancellationToken, Task<FleetNotice>> remote,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        if (sp.GetService<CoreWriteClient>() is { Enabled: true } core && !string.IsNullOrEmpty(bearer))
            return await remote(core, bearer, ct);
        return await local(sp, ct);
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
