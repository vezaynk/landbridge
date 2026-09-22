using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

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
        g.MapPost("/bind-machine", BindMachineAsync);
        g.MapPost("/unbind-machine", UnbindMachineAsync);
        g.MapPost("/processes/start", ProcessStartAsync);
        g.MapPost("/processes/stop", ProcessStopAsync);
        g.MapPost("/processes/write", ProcessWriteAsync);
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
        var result = await store.CreateAsync(
            new CreateSession(lead.Claim!, lead.Claim!.Team, body.Description, body.Profile.Trim()), ct);
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
        if (LandbridgeClaims.AsHuman(http.User) is not null)
            actor = new HumanSession();
        else
        {
            var lead = await LeadOn(http, tokens, ids, body.TeamId, ct);
            if (lead.Error is { } err)
                return err;
            actor = lead.Claim!;
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
        return Store(await store.RegisterServiceAsync(worker.Caller!, body.Name, body.Port.Value, ct));
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

    private static IResult Store(StoreResult result)
    {
        var reply = CoreStoreReply.From(result);
        var code = reply.Status == "applied" ? 200
            : reply.Status == "not_found" ? 404
            : 409;
        return Results.Json(reply, CoreWriteClient.Json, statusCode: code);
    }

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
