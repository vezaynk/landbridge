using System.Text;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Landbridge.Mcp;

/// <summary>
/// Core-only mutation HTTP. Façades forward the inbound Bearer. The actor is
/// the principal; registry sends stay here because <c>/runner</c> is Core.
/// </summary>
public static class CoreWriteEndpoints
{
    public static IEndpointRouteBuilder MapCoreWrites(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/core/v1").RequireAuthorization();
        g.MapPost("/create-team", CreateTeamAsync);
        g.MapPost("/create-session", CreateSessionAsync);
        g.MapPost("/sessions/{id}/stop", StopAsync);
        g.MapPost("/sessions/{id}/park", ParkAsync);
        g.MapPost("/sessions/{id}/input-response", InputResponseAsync);
        g.MapPost("/sessions/{id}/input-request", InputRequestAsync);
        g.MapPost("/sessions/{id}/permission", PermissionAsync);
        g.MapPost("/sessions/{id}/report", ReportAsync);
        g.MapPost("/sessions/{id}/ask", AskAsync);
        g.MapPost("/sessions/{id}/services", RegisterServiceAsync);
        g.MapPost("/sessions/{id}/register-service", RegisterServiceFromDashboardAsync);
        g.MapPost("/sessions/{id}/unregister-service", UnregisterServiceAsync);
        g.MapPost("/bind-machine", BindMachineAsync);
        g.MapPost("/unbind-machine", UnbindMachineAsync);
        g.MapPost("/processes/start", ProcessStartAsync);
        g.MapPost("/processes/stop", ProcessStopAsync);
        g.MapPost("/processes/write", ProcessWriteAsync);
        g.MapPost("/forwards", OpenWorkerForwardAsync);
        g.MapPost("/forwards/lead", OpenLeadForwardAsync);
        g.MapPost("/forwards/close", CloseForwardAsync);
        g.MapPost("/previews", MintPreviewAsync);
        g.MapPost("/previews/patch", PatchPreviewAsync);
        g.MapPost("/friction", RecordFrictionAsync);
        g.MapPost("/machines/revoke", RevokeMachineAsync);
        return app;
    }

    private static async Task<IResult> CreateTeamAsync(
        HttpContext http, TokenService tokens, FriendlyIds ids, CancellationToken ct)
    {
        var lead = Lead(http);
        if (lead.Error is { } err)
            return err;
        var team = await tokens.CreateTeamAsync(lead.Principal!.CredentialId, ct);
        var slug = await ids.TeamAsync(team.Value, ct);
        return Results.Json(new CoreStoreReply("applied", Slug: slug, SessionId: team.Value.ToString("D"), Reason: slug), CoreWriteClient.Json);
    }

    private static async Task<IResult> CreateSessionAsync(
        HttpContext http, CoreCreateSessionBody body, SessionStore store, TokenService tokens,
        FriendlyIds ids, CancellationToken ct)
    {
        var lead = await LeadOn(http, tokens, ids, body.TeamId, ct);
        if (lead.Error is { } err)
            return err;
        SessionId? clientId = null;
        if (!string.IsNullOrWhiteSpace(body.SessionId)
            && Guid.TryParse(body.SessionId, out var parsed))
            clientId = new SessionId(parsed);
        var sessionId = clientId ?? SessionId.New();
        if (await AcceptIfPreferredAsync(
                http, CommandRow.LeadActor, Lead(http).Principal!.CredentialId, lead.Claim!.Team.Value,
                sessionId.Value, CommandRow.CreateSession,
                new CommandPayload(body.Description, body.Profile.Trim()), ct) is { } accepted)
            return accepted;
        var result = await store.CreateAsync(
            new CreateSession(lead.Claim!, lead.Claim!.Team, body.Description, body.Profile.Trim(), sessionId), ct);
        if (result is StoreResult.Applied a)
        {
            var slug = await ids.SessionAsync(a.Session.Id.Value, ct);
            return Results.Json(CoreStoreReply.From(result) with { Slug = slug, Reason = slug }, CoreWriteClient.Json);
        }
        return Store(result);
    }

    private static async Task<IResult> StopAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, TokenService tokens,
        FriendlyIds ids, RunnerConnectionRegistry registry, CancellationToken ct)
    {
        var lead = await LeadOn(http, tokens, ids, body.TeamId, ct);
        if (lead.Error is { } err)
            return err;
        var session = await ids.TrySessionAsync(id, ct);
        if (session is null)
            return Store(new StoreResult.NotFound("no such session"));
        if (await AcceptIfPreferredAsync(
                http, CommandRow.LeadActor, Lead(http).Principal!.CredentialId, lead.Claim!.Team.Value,
                session.Value.Value, CommandRow.StopSession,
                new CommandPayload(TtlSeconds: body.TtlSeconds), ct) is { } accepted)
            return accepted;
        var ttl = body.TtlSeconds is null ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromSeconds(Math.Max(0, body.TtlSeconds.Value));
        var machine = registry.MachineFor(session.Value);
        var applied = await store.ApplyAsync(session.Value, new StopSession(lead.Claim!), ct);
        if (applied is StoreResult.Applied ok
            && ok.Session.OccupancyObserved == Occupancy.Running
            && machine is not null)
        {
            await registry.SendAsync(machine.Value,
                new StopCommand(session.Value, ttl, StopDisposition.Preserve, "stop"), ct);
        }
        return Store(applied);
    }

    private static async Task<IResult> ParkAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, TokenService tokens,
        FriendlyIds ids, RunnerConnectionRegistry registry, CancellationToken ct)
    {
        var lead = await LeadOn(http, tokens, ids, body.TeamId, ct);
        if (lead.Error is { } err)
            return err;
        var session = await ids.TrySessionAsync(id, ct);
        if (session is null)
            return Store(new StoreResult.NotFound("no such session"));
        if (await AcceptIfPreferredAsync(
                http, CommandRow.LeadActor, Lead(http).Principal!.CredentialId, lead.Claim!.Team.Value,
                session.Value.Value, CommandRow.ParkSession, new CommandPayload(), ct) is { } accepted)
            return accepted;
        var machine = registry.MachineFor(session.Value);
        if (machine is not { } parked)
            return Results.Json(new CoreStoreReply("rejected", Reason: "this task is not tracked on any machine, so it cannot be parked"), CoreWriteClient.Json);
        var result = await store.ApplyAsync(session.Value, new Park(lead.Claim!, new ParkRecord(parked)), ct);
        if (result is StoreResult.Applied)
        {
            await registry.SendAsync(parked,
                new StopCommand(session.Value, TimeSpan.FromSeconds(30), StopDisposition.PreserveAndPark, "park"), ct);
        }
        return Store(result);
    }

    private static async Task<IResult> InputResponseAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, TokenService tokens,
        FriendlyIds ids, RunnerConnectionRegistry registry, CancellationToken ct)
    {
        var lead = await LeadOn(http, tokens, ids, body.TeamId, ct);
        if (lead.Error is { } err)
            return err;
        var session = await ids.TrySessionAsync(id, ct);
        if (session is null)
            return Store(new StoreResult.NotFound("no such session"));
        if (await AcceptIfPreferredAsync(
                http, CommandRow.LeadActor, Lead(http).Principal!.CredentialId, lead.Claim!.Team.Value,
                session.Value.Value, CommandRow.InputResponse,
                new CommandPayload(Answer: body.Answer), ct) is { } accepted)
            return accepted;
        var machine = registry.MachineFor(session.Value);
        var live = registry.HasLiveProcess(session.Value);
        var result = await store.SendInputResponseAsync(lead.Claim!, session.Value, machine, body.Answer, live, ct);
        await Doorbell(registry, session.Value, machine, live, result, ct);
        return Store(result);
    }

    private static async Task<IResult> InputRequestAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, TokenService tokens,
        FriendlyIds ids, RunnerConnectionRegistry registry, CancellationToken ct)
    {
        var lead = await LeadOn(http, tokens, ids, body.TeamId, ct);
        if (lead.Error is { } err)
            return err;
        var session = await ids.TrySessionAsync(id, ct);
        if (session is null)
            return Store(new StoreResult.NotFound("no such session"));
        if (await AcceptIfPreferredAsync(
                http, CommandRow.LeadActor, Lead(http).Principal!.CredentialId, lead.Claim!.Team.Value,
                session.Value.Value, CommandRow.InputRequest,
                new CommandPayload(Text: body.Text), ct) is { } accepted)
            return accepted;
        var machine = registry.MachineFor(session.Value);
        var live = registry.HasLiveProcess(session.Value);
        var result = await store.SendInputRequestAsync(lead.Claim!, session.Value, machine, body.Text, live, ct);
        await Doorbell(registry, session.Value, machine, live, result, ct);
        return Store(result);
    }

    private static async Task<IResult> PermissionAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, TokenService tokens,
        FriendlyIds ids, CancellationToken ct)
    {
        var session = await ids.TrySessionAsync(id, ct);
        if (session is null)
            return Store(new StoreResult.NotFound("no such session"));
        Actor actor;
        if (LandbridgeClaims.ToPrincipal(http.User) is Principal.Human human)
        {
            actor = new HumanSession();
            if (await AcceptIfPreferredAsync(
                    http, CommandRow.HumanActor, human.HumanId, Guid.Empty,
                    session.Value.Value, CommandRow.Permission,
                    new CommandPayload(Option: body.Option?.Trim(), Message: body.Message), ct) is { } humanAccepted)
                return humanAccepted;
        }
        else
        {
            var lead = await LeadOn(http, tokens, ids, body.TeamId, ct);
            if (lead.Error is { } err)
                return err;
            actor = lead.Claim!;
            if (await AcceptIfPreferredAsync(
                    http, CommandRow.LeadActor, Lead(http).Principal!.CredentialId, lead.Claim!.Team.Value,
                    session.Value.Value, CommandRow.Permission,
                    new CommandPayload(Option: body.Option?.Trim(), Message: body.Message), ct) is { } accepted)
                return accepted;
        }
        return Store(await store.AnswerPermissionAsync(actor, session.Value, body.Option?.Trim() ?? "", body.Message, ct));
    }

    private static async Task<IResult> ReportAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, CancellationToken ct)
    {
        var worker = Worker(http);
        if (worker.Error is { } err)
            return err;
        if (worker.Caller!.Session.Value.ToString("D") != id
            && !string.Equals(id, worker.Caller.Session.Value.ToString("N"), StringComparison.OrdinalIgnoreCase))
            return Results.Json(new CoreStoreReply("rejected", Reason: "worker token is not for that session"), CoreWriteClient.Json, statusCode: 403);
        if (await AcceptIfPreferredAsync(
                http, CommandRow.WorkerActor, worker.Caller.Session.Value, worker.Caller.Team.Value,
                worker.Caller.Session.Value, CommandRow.Report,
                new CommandPayload(
                    ResultReference: body.ResultReference, Report: body.Report,
                    InstanceId: worker.Caller.Instance.Value), ct) is { } accepted)
            return accepted;
        return Store(await store.ApplyAsync(
            worker.Caller.Session, new ReportResult(worker.Caller, body.ResultReference ?? "", body.Report), ct));
    }

    private static async Task<IResult> AskAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, CancellationToken ct)
    {
        var worker = Worker(http);
        if (worker.Error is { } err)
            return err;
        if (!Enum.TryParse<InputRequestKind>(body.Kind, ignoreCase: true, out var kind))
            return Results.Json(new CoreStoreReply("rejected", Reason: "unknown input kind"), CoreWriteClient.Json);
        if (await AcceptIfPreferredAsync(
                http, CommandRow.WorkerActor, worker.Caller!.Session.Value, worker.Caller.Team.Value,
                worker.Caller.Session.Value, CommandRow.Ask,
                new CommandPayload(
                    Kind: body.Kind, Text: body.Text ?? body.Answer,
                    InstanceId: worker.Caller.Instance.Value), ct) is { } accepted)
            return accepted;
        return Store(await store.ApplyAsync(
            worker.Caller!.Session, new RequestInput(worker.Caller, kind, body.Text ?? body.Answer), ct));
    }

    private static async Task<IResult> RegisterServiceAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, CancellationToken ct)
    {
        var worker = Worker(http);
        if (worker.Error is { } err)
            return err;
        if (string.IsNullOrWhiteSpace(body.Name) || body.Port is null)
            return Results.Json(new CoreStoreReply("rejected", Reason: "name and port required"), CoreWriteClient.Json);
        if (await AcceptIfPreferredAsync(
                http, CommandRow.WorkerActor, worker.Caller!.Session.Value, worker.Caller.Team.Value,
                worker.Caller.Session.Value, CommandRow.RegisterService,
                new CommandPayload(
                    Name: body.Name, Port: body.Port,
                    InstanceId: worker.Caller.Instance.Value), ct) is { } accepted)
            return accepted;
        return Store(await store.RegisterServiceAsync(worker.Caller!, body.Name, body.Port.Value, ct));
    }

    private static async Task<IResult> RegisterServiceFromDashboardAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, FriendlyIds ids, CancellationToken ct)
    {
        if (RequireHuman(http) is { } err)
            return err;
        var session = await ids.TrySessionAsync(id, ct);
        if (session is null)
            return Store(new StoreResult.NotFound("no such session"));
        if (string.IsNullOrWhiteSpace(body.Name) || body.Port is null)
            return Results.Json(new CoreStoreReply("rejected", Reason: "name and port required"), CoreWriteClient.Json);
        return Store(await store.RegisterServiceFromDashboardAsync(session.Value, body.Name, body.Port.Value, ct));
    }

    private static async Task<IResult> UnregisterServiceAsync(
        HttpContext http, string id, CoreSessionBody body, SessionStore store, FriendlyIds ids, CancellationToken ct)
    {
        if (RequireHuman(http) is { } err)
            return err;
        var session = await ids.TrySessionAsync(id, ct);
        if (session is null)
            return Store(new StoreResult.NotFound("no such session"));
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.Json(new CoreStoreReply("rejected", Reason: "name required"), CoreWriteClient.Json);
        return Store(await store.UnregisterServiceAsync(session.Value, body.Name, ct));
    }

    private static async Task<IResult> BindMachineAsync(
        HttpContext http, CoreBindBody body,
        [FromServices] LeadMachineBindingService machines, FriendlyIds ids, CancellationToken ct)
    {
        var human = HumanId(http);
        if (human is null)
            return Results.Json(new CoreStoreReply("rejected", Reason: "a human identity is required to bind a machine"), CoreWriteClient.Json);
        var machine = await ids.TryMachineAsync(body.MachineId, ct);
        if (machine is null)
            return Results.Json(new CoreStoreReply("rejected", Reason: "invalid machine id"), CoreWriteClient.Json);
        return await machines.BindAsync(human.Value, machine.Value, ct) switch
        {
            LeadMachineBindResult.Bound b => Results.Json(
                new CoreStoreReply("applied", Detail: b.Binding.MachineName, SessionId: b.Binding.MachineId.ToString("D"),
                    Reason: $"ok: machine {b.Binding.MachineName} is now your machine"), CoreWriteClient.Json),
            LeadMachineBindResult.Refused r => Results.Json(new CoreStoreReply("rejected", Reason: r.Reason), CoreWriteClient.Json),
            _ => Results.Json(new CoreStoreReply("conflict", Reason: "unknown bind result"), CoreWriteClient.Json),
        };
    }

    private static async Task<IResult> UnbindMachineAsync(
        HttpContext http, [FromServices] LeadMachineBindingService machines, CancellationToken ct)
    {
        var human = HumanId(http);
        if (human is null)
            return Results.Json(new CoreStoreReply("rejected", Reason: "a human identity is required to unbind a machine"), CoreWriteClient.Json);
        var released = await machines.UnbindAsync(human.Value, ct);
        return Results.Json(new CoreStoreReply("applied",
            Reason: released is null ? "ok: you had no machine bound" : $"ok: released {released.MachineName}"),
            CoreWriteClient.Json);
    }

    private static async Task<IResult> ProcessStartAsync(
        HttpContext http, CoreProcessStartBody body, ProcessControlRelay processes, CancellationToken ct)
    {
        var worker = Worker(http);
        if (worker.Error is { } err)
            return err;
        var result = await processes.StartAsync(
            worker.Caller!.Session, body.Name, body.Spawn, body.WorkingDirectory, body.Env, body.OpenStdin, ct);
        return Results.Json(new CoreProcessStartReply(result.Started, result.LogPath, result.Refusal), CoreWriteClient.Json);
    }

    private static async Task<IResult> ProcessStopAsync(
        HttpContext http, CoreProcessBody body, ProcessControlRelay processes, CancellationToken ct)
    {
        var worker = Worker(http);
        if (worker.Error is { } err)
            return err;
        var r = await processes.StopAsync(worker.Caller!.Session, body.Name, ct);
        return Results.Json(new CoreProcessActionReply(r.Ok, r.Refusal, r.Value), CoreWriteClient.Json);
    }

    private static async Task<IResult> ProcessWriteAsync(
        HttpContext http, CoreProcessBody body, ProcessControlRelay processes, CancellationToken ct)
    {
        var worker = Worker(http);
        if (worker.Error is { } err)
            return err;
        var r = await processes.WriteAsync(worker.Caller!.Session, body.Name, body.Data ?? "", body.AppendNewline, ct);
        return Results.Json(new CoreProcessActionReply(r.Ok, r.Refusal, r.Value), CoreWriteClient.Json);
    }

    private static async Task<IResult> OpenWorkerForwardAsync(
        HttpContext http, CoreForwardBody body, RelayGrantService grants, ForwardOrchestrator forwards,
        IConfiguration config, CancellationToken ct)
    {
        var worker = Worker(http);
        if (worker.Error is { } err)
            return err;
        var issued = await grants.IssueAsync(worker.Caller!, body.ServiceName, ct);
        if (issued is not RelayGrantResult.Issued grant)
        {
            var why = issued is RelayGrantResult.Refused r ? r.Reason : "unknown grant result";
            var rule = issued is RelayGrantResult.Refused refused ? refused.Rule.ToString() : null;
            return Results.Json(new CoreForwardReply(false, null, null, null, null, why, rule), CoreWriteClient.Json);
        }
        return await forwards.EstablishAsync(worker.Caller!, grant, body.ServiceName, RelayUrl(config), ct) switch
        {
            ForwardEstablishResult.Established e => Results.Json(
                new CoreForwardReply(true, "127.0.0.1", e.Port, grant.ForwardId.ToString(), grant.ExpiresAt, null),
                CoreWriteClient.Json),
            ForwardEstablishResult.Failed f => Results.Json(
                new CoreForwardReply(false, null, null, null, null, f.Reason), CoreWriteClient.Json),
            _ => Results.Json(new CoreForwardReply(false, null, null, null, null, "unknown forward result"), CoreWriteClient.Json),
        };
    }

    private static async Task<IResult> OpenLeadForwardAsync(
        HttpContext http, CoreForwardBody body, TokenService tokens, FriendlyIds ids,
        [FromServices] LeadMachineBindingService machines, RelayGrantService grants, ForwardOrchestrator forwards,
        IConfiguration config, CancellationToken ct)
    {
        var human = HumanId(http);
        if (human is null)
            return Results.Json(new CoreForwardReply(false, null, null, null, null, "a human identity is required"), CoreWriteClient.Json);
        if (LandbridgeClaims.AsLeadPrincipal(http.User) is not null)
        {
            var lead = await LeadOn(http, tokens, ids, body.TeamId ?? "", ct);
            if (lead.Error is { } err)
                return err;
        }
        var team = await ids.TryTeamAsync(body.TeamId, ct);
        if (team is null)
            return Results.Json(new CoreForwardReply(false, null, null, null, null, "invalid team id"), CoreWriteClient.Json);
        var bound = await machines.GetAsync(human.Value, ct);
        if (bound is null)
            return Results.Json(new CoreForwardReply(false, null, null, null, null, "no machine bound"), CoreWriteClient.Json);
        var issued = await grants.IssueForLeadAsync(team.Value, body.ServiceName, ct);
        if (issued is not RelayGrantResult.Issued grant)
        {
            var why = issued is RelayGrantResult.Refused r ? r.Reason : "could not issue a grant";
            var rule = issued is RelayGrantResult.Refused refused ? refused.Rule.ToString() : null;
            return Results.Json(new CoreForwardReply(false, null, null, null, null, why, rule), CoreWriteClient.Json);
        }
        return await forwards.EstablishForLeadAsync(bound.MachineId, grant, body.ServiceName, RelayUrl(config), ct) switch
        {
            ForwardEstablishResult.Established e => Results.Json(
                new CoreForwardReply(true, "127.0.0.1", e.Port, grant.ForwardId.ToString(), grant.ExpiresAt, null),
                CoreWriteClient.Json),
            ForwardEstablishResult.Failed f => Results.Json(
                new CoreForwardReply(false, null, null, null, null, f.Reason), CoreWriteClient.Json),
            _ => Results.Json(new CoreForwardReply(false, null, null, null, null, "unknown forward result"), CoreWriteClient.Json),
        };
    }

    private static async Task<IResult> CloseForwardAsync(
        HttpContext http, CoreCloseForwardBody body, RelayGrantService grants,
        ForwardTeardownService teardown, CancellationToken ct)
    {
        if (RequireHuman(http) is { } err)
            return err;
        var closed = await grants.CloseConsumerAsync(body.ForwardId, ct);
        if (closed is null)
            return Results.Json(new CoreCloseForwardReply(false, Reason: "not found"), CoreWriteClient.Json);
        var (producer, consumer, name) = closed.Value;
        await teardown.CloseAsync(
            [new ForwardTeardown(producer, body.ForwardId.ToString(), consumer)], ct);
        return Results.Json(new CoreCloseForwardReply(true, name), CoreWriteClient.Json);
    }

    private static async Task<IResult> MintPreviewAsync(
        HttpContext http, CorePreviewMintBody body, PreviewMappingService previews,
        TokenService tokens, FriendlyIds ids, IConfiguration config, CancellationToken ct)
    {
        var policy = body.IsPublic ? PreviewAuthPolicy.Public : PreviewAuthPolicy.Gated;
        var ttl = PreviewMint.ResolveTtl(policy, body.TtlMinutes);
        PreviewMintResult mint;
        if (Worker(http).Caller is { } caller)
        {
            var created = await previews.CreateForWorkerAsync(caller, body.ServiceName, policy, ttl, ct);
            if (created is null)
                return Results.Json(new CorePreviewReply(false, Reason:
                    $"you have not registered a service named '{body.ServiceName}' on this session; register it with " +
                    "register_service first (a preview only ever exposes your own session's service)."),
                    CoreWriteClient.Json);
            mint = created;
        }
        else
        {
            var team = await ids.TryTeamAsync(body.TeamId, ct);
            var session = await ids.TrySessionAsync(body.SessionId, ct);
            if (team is null || session is null)
                return Results.Json(new CorePreviewReply(false, Reason: "team and session required"), CoreWriteClient.Json);
            if (LandbridgeClaims.AsLeadPrincipal(http.User) is { } lead
                && !await tokens.OwnsTeamAsync(lead.CredentialId, team.Value, ct))
                return Results.Json(new CorePreviewReply(false, Reason: "not your team"), CoreWriteClient.Json, statusCode: 403);
            mint = await previews.CreateAsync(team.Value, session.Value, body.ServiceName, policy, ttl, ct);
        }
        var baseUrl = config[PreviewMint.UrlBaseConfigKey]
            ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PREVIEW_URL_BASE")
            ?? "http://preview.localhost";
        return Results.Json(new CorePreviewReply(
            true, PreviewMint.Url(baseUrl, mint.Label), policy.ToString().ToLowerInvariant(),
            mint.Mapping.ExpiresAt, mint.Mapping.Id, Label: mint.Label), CoreWriteClient.Json);
    }

    private static async Task<IResult> PatchPreviewAsync(
        HttpContext http, CorePreviewPatchBody body, PreviewMappingService previews, CancellationToken ct)
    {
        if (HumanId(http) is null && LandbridgeClaims.AsHuman(http.User) is null)
            return Results.Json(new CorePreviewReply(false, Reason: "human required"), CoreWriteClient.Json, statusCode: 403);
        if (body.Revoke)
        {
            await previews.RevokeAsync(body.PreviewId, ct);
            return Results.Json(new CorePreviewReply(true, PreviewId: body.PreviewId), CoreWriteClient.Json);
        }
        if (body.IsPublic is { } pub)
        {
            var policy = pub ? PreviewAuthPolicy.Public : PreviewAuthPolicy.Gated;
            if (!await previews.SetAuthPolicyAsync(body.PreviewId, policy, ct))
                return Results.Json(new CorePreviewReply(false, Reason: "not found"), CoreWriteClient.Json, statusCode: 404);
            return Results.Json(new CorePreviewReply(true, Auth: policy.ToString().ToLowerInvariant(), PreviewId: body.PreviewId), CoreWriteClient.Json);
        }
        return Results.Json(new CorePreviewReply(false, Reason: "nothing to patch"), CoreWriteClient.Json);
    }

    private static async Task<IResult> RecordFrictionAsync(
        HttpContext http, CoreFrictionBody body, FrictionStore friction, TokenService tokens,
        FriendlyIds ids, CancellationToken ct)
    {
        string role;
        Guid team;
        Guid? sessionId;
        Guid? humanId;
        if (LandbridgeClaims.AsLeadPrincipal(http.User) is { } lead)
        {
            var owned = await ids.TryTeamAsync(body.TeamId, ct);
            if (owned is null || !await tokens.OwnsTeamAsync(lead.CredentialId, owned.Value, ct))
                return Results.Json(new CoreStoreReply("rejected", Reason: "teamId is required and must be owned"), CoreWriteClient.Json, statusCode: 403);
            role = FrictionReportRow.LeadRole;
            team = owned.Value.Value;
            sessionId = null;
            humanId = lead.HumanId;
        }
        else if (LandbridgeClaims.AsWorker(http.User) is { } worker)
        {
            role = FrictionReportRow.WorkerRole;
            team = worker.Team.Value;
            sessionId = worker.Session.Value;
            humanId = null;
        }
        else
            return Results.Json(new CoreStoreReply("rejected", Reason: "lead or worker required"), CoreWriteClient.Json, statusCode: 403);
        if (string.IsNullOrWhiteSpace(body.Message))
            return Results.Json(new CoreStoreReply("rejected", Reason:
                "message is required: say what friction you felt in Landbridge and how it could be improved"),
                CoreWriteClient.Json);
        if (Encoding.UTF8.GetByteCount(body.Message) > FrictionStore.MaxMessageBytes)
            return Results.Json(new CoreStoreReply("rejected", Reason:
                $"message is over the {FrictionStore.MaxMessageBytes / 1024} KB cap; shorten it"),
                CoreWriteClient.Json);
        await friction.RecordAsync(role, team, sessionId, humanId, body.Message, ct);
        return Results.Json(new CoreStoreReply("applied", Reason: "ok: friction recorded"), CoreWriteClient.Json);
    }

    private static async Task<IResult> RevokeMachineAsync(
        HttpContext http, CoreRevokeMachineBody body, MachineRevocationService revocations,
        FriendlyIds ids, CancellationToken ct)
    {
        if (LandbridgeClaims.ToPrincipal(http.User) is not Principal.Human)
            return Results.Json(new CoreRevokeMachineReply(false, false, 0, 0, "human-only"), CoreWriteClient.Json, statusCode: 403);
        var machine = await ids.TryMachineAsync(body.MachineId, ct);
        if (machine is null)
            return Results.Json(new CoreRevokeMachineReply(false, false, 0, 0, "invalid machine id"), CoreWriteClient.Json);
        var revoked = await revocations.RevokeAsync(machine.Value, ct);
        return Results.Json(new CoreRevokeMachineReply(true, revoked.ChannelClosed, revoked.SessionsRequeued, revoked.WorkersRevoked, null), CoreWriteClient.Json);
    }

    private static string RelayUrl(IConfiguration config) =>
        config["Landbridge:RelayUrl"]
        ?? Environment.GetEnvironmentVariable("LANDBRIDGE_RELAY_URL")
        ?? "http://127.0.0.1:5100";

    private static async Task Doorbell(
        RunnerConnectionRegistry registry, SessionId id, Guid? machine, bool live, StoreResult result, CancellationToken ct)
    {
        if (result is StoreResult.Applied applied
            && applied.Session.State == SessionState.Working
            && applied.Session.CurrentInstance is not null
            && live
            && machine is { } dest)
        {
            await registry.SendAsync(dest, new PromptCommand(id), ct);
        }
    }

    private static async Task<IResult?> AcceptIfPreferredAsync(
        HttpContext http,
        string actorKind,
        Guid actorId,
        Guid teamId,
        Guid sessionId,
        string kind,
        object payload,
        CancellationToken ct)
    {
        if (!PreferHeader.WantsRespondAsync(http.Request))
            return null;
        var queue = http.RequestServices.GetRequiredService<CommandQueue>();
        var row = await queue.EnqueueAsync(actorKind, actorId, teamId, sessionId, kind, payload, ct);
        PreferHeader.ApplyRespondAsync(http.Response);
        return Results.Json(CoreStoreReply.FromCommand(row), CoreWriteClient.Json,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult Store(StoreResult result)
    {
        var reply = CoreStoreReply.From(result);
        var code = reply.Status == "applied" ? 200
            : reply.Status == "not_found" ? 404
            : 409;
        return Results.Json(reply, CoreWriteClient.Json, statusCode: code);
    }

    private static IResult? RequireHuman(HttpContext http) =>
        LandbridgeClaims.ToPrincipal(http.User) is Principal.Human
            ? null
            : Results.Json(new CoreStoreReply("rejected", Reason: "human-only"), CoreWriteClient.Json, statusCode: 403);

    private static Guid? HumanId(HttpContext http) => LandbridgeClaims.ToPrincipal(http.User) switch
    {
        Principal.Human h => h.HumanId,
        Principal.Lead l => l.HumanId,
        _ => null,
    };

    private static (Principal.Lead? Principal, IResult? Error) Lead(HttpContext http)
    {
        if (LandbridgeClaims.AsEvictedLead(http.User) is { } evicted)
            return (null, Results.Json(new CoreStoreReply("rejected",
                Reason: $"your lead claim on team {evicted.Team.Value:N} was taken over"), CoreWriteClient.Json, statusCode: 403));
        var lead = LandbridgeClaims.AsLeadPrincipal(http.User);
        return lead is null
            ? (null, Results.Json(new CoreStoreReply("rejected", Reason: "lead required"), CoreWriteClient.Json, statusCode: 403))
            : (lead, null);
    }

    private static async Task<(LeadClaim? Claim, IResult? Error)> LeadOn(
        HttpContext http, TokenService tokens, FriendlyIds ids, string teamId, CancellationToken ct)
    {
        var lead = Lead(http);
        if (lead.Error is { } err)
            return (null, err);
        var team = await ids.TryTeamAsync(teamId, ct);
        if (team is null || !await tokens.OwnsTeamAsync(lead.Principal!.CredentialId, team.Value, ct))
            return (null, Results.Json(new CoreStoreReply("rejected", Reason: "this lead credential does not own that team"), CoreWriteClient.Json, statusCode: 403));
        return (new LeadClaim(team.Value), null);
    }

    private static (WorkerCaller? Caller, IResult? Error) Worker(HttpContext http)
    {
        var w = LandbridgeClaims.AsWorker(http.User);
        return w is null
            ? (null, Results.Json(new CoreStoreReply("rejected", Reason: "worker required"), CoreWriteClient.Json, statusCode: 403))
            : (w, null);
    }
}
