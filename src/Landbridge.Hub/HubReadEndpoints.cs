using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Landbridge.Hub;

/// <summary>
/// JSON twins. Auth is Bearer; Hub owns the read policy.
/// </summary>
public static class HubReadEndpoints
{
    public static IEndpointRouteBuilder MapHubReads(this IEndpointRouteBuilder app)
    {
        app.MapGet("/sessions", ListSessionsAsync);
        app.MapGet("/sessions/{id}", GetSessionAsync);
        app.MapGet("/sessions/{id}/log", GetLogAsync);
        app.MapGet("/sessions/{id}/exchange", GetExchangeAsync);
        app.MapGet("/sessions/{id}/services", ListSessionServicesAsync);
        app.MapGet("/services", ListServicesAsync);
        app.MapGet("/forwards", ListForwardsAsync);
        app.MapGet("/forwards/{id}", GetForwardAsync);
        app.MapGet("/previews", ListPreviewsAsync);
        app.MapGet("/previews/{id}", GetPreviewAsync);
        app.MapGet("/machines", ListMachinesAsync);
        app.MapGet("/machines/{id}", GetMachineAsync);
        app.MapGet("/machines/{id}/processes", ListMachineProcessesAsync);
        app.MapGet("/processes", ListProcessesAsync);
        app.MapGet("/processes/{id:guid}", GetProcessAsync);
        app.MapGet("/teams", ListTeamsAsync);
        app.MapGet("/teams/{id}", GetTeamAsync);
        app.MapGet("/friction", ListFrictionAsync);
        app.MapGet("/lead-events", ListLeadEventsAsync);
        return app;
    }

    private static async Task<IResult> ListSessionsAsync(
        HttpContext http, TokenService tokens, HubReads reads, FriendlyIds ids,
        string? teamId, bool hidden = false, CancellationToken ct = default)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var team = await ResolveTeamAsync(ids, teamId, caller, ct);
        if (team.Error is { } teamErr)
            return teamErr;
        return Json(await reads.SessionsAsync(caller, team.Id, hidden, ct));
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext http, TokenService tokens, string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var session = await ResolveSessionAsync(ids, id, ct);
        if (session.Error is { } sidErr)
            return sidErr;
        var doc = await reads.SessionAsync(session.Id!.Value, ct);
        if (doc is null || !caller.MaySession(doc.Id, doc.TeamId))
            return NotFound();
        return Json(doc);
    }

    private static async Task<IResult> GetLogAsync(
        HttpContext http, TokenService tokens, string id, HubReads reads, FriendlyIds ids,
        int? limit, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var session = await ResolveSessionAsync(ids, id, ct);
        if (session.Error is { } sidErr)
            return sidErr;
        var row = await reads.SessionAsync(session.Id!.Value, ct);
        if (row is null || !caller.MaySession(row.Id, row.TeamId))
            return NotFound();
        return Json(await reads.LogAsync(session.Id!.Value, limit ?? HubReads.DefaultLimit, ct) ?? []);
    }

    private static async Task<IResult> GetExchangeAsync(
        HttpContext http, TokenService tokens, string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var session = await ResolveSessionAsync(ids, id, ct);
        if (session.Error is { } sidErr)
            return sidErr;
        var row = await reads.SessionAsync(session.Id!.Value, ct);
        if (row is null || !caller.MaySession(row.Id, row.TeamId))
            return NotFound();
        var exchange = await reads.ExchangeAsync(session.Id!.Value, ct);
        return exchange is null ? NotFound() : Json(exchange);
    }

    private static async Task<IResult> ListSessionServicesAsync(
        HttpContext http, TokenService tokens, string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var session = await ResolveSessionAsync(ids, id, ct);
        if (session.Error is { } sidErr)
            return sidErr;
        var row = await reads.SessionAsync(session.Id!.Value, ct);
        if (row is null || !caller.MaySession(row.Id, row.TeamId))
            return NotFound();
        return Json(await reads.ServicesAsync(caller, teamId: null, session.Id, ct));
    }

    private static async Task<IResult> ListServicesAsync(
        HttpContext http, TokenService tokens, HubReads reads, FriendlyIds ids,
        string? teamId, string? sessionId, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var team = await ResolveTeamAsync(ids, teamId, caller, ct);
        if (team.Error is { } teamErr)
            return teamErr;
        var session = await ResolveSessionAsync(ids, sessionId, ct);
        if (session.Error is { } sessionErr)
            return sessionErr;
        return Json(await reads.ServicesAsync(caller, team.Id, session.Id, ct));
    }

    private static async Task<IResult> ListForwardsAsync(
        HttpContext http, TokenService tokens, HubReads reads, FriendlyIds ids,
        string? teamId, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var team = await ResolveTeamAsync(ids, teamId, caller, ct);
        if (team.Error is { } teamErr)
            return teamErr;
        return Json(await reads.ForwardsAsync(caller, team.Id, ct));
    }

    private static async Task<IResult> GetForwardAsync(
        HttpContext http, TokenService tokens, Guid id, HubReads reads, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var doc = await reads.ForwardAsync(id, ct);
        if (doc is null || !caller.MayTeam(doc.TeamId))
            return NotFound();
        if (caller.Principal is Principal.Worker
            && doc.ProducerSessionId != caller.WorkerSession
            && doc.ConsumerSessionId != caller.WorkerSession)
            return NotFound();
        return Json(doc);
    }

    private static async Task<IResult> ListPreviewsAsync(
        HttpContext http, TokenService tokens, HubReads reads, FriendlyIds ids,
        string? teamId, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var team = await ResolveTeamAsync(ids, teamId, caller, ct);
        if (team.Error is { } teamErr)
            return teamErr;
        return Json(await reads.PreviewsAsync(caller, team.Id, ct));
    }

    private static async Task<IResult> GetPreviewAsync(
        HttpContext http, TokenService tokens, Guid id, HubReads reads, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        var doc = await reads.PreviewAsync(id, ct);
        if (doc is null || !caller.MaySession(doc.SessionId, doc.TeamId))
            return NotFound();
        return Json(doc);
    }

    private static async Task<IResult> ListMachinesAsync(
        HttpContext http, TokenService tokens, HubReads reads, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (!caller.MayMachines)
            return HubCaller.Forbid();
        return Json(await reads.MachinesAsync(caller, ct));
    }

    private static async Task<IResult> GetMachineAsync(
        HttpContext http, TokenService tokens, string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (!caller.MayMachines)
            return HubCaller.Forbid();
        var machine = await ResolveMachineAsync(ids, id, ct);
        if (machine.Error is { } midErr)
            return midErr;
        var doc = await reads.MachineAsync(caller, machine.Id!.Value, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListMachineProcessesAsync(
        HttpContext http, TokenService tokens, string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (!caller.MayProcesses)
            return HubCaller.Forbid();
        var machine = await ResolveMachineAsync(ids, id, ct);
        if (machine.Error is { } midErr)
            return midErr;
        return Json(await reads.ProcessesAsync(caller, machine.Id, ct));
    }

    private static async Task<IResult> ListProcessesAsync(
        HttpContext http, TokenService tokens, HubReads reads, FriendlyIds ids,
        string? machineId, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (!caller.MayProcesses)
            return HubCaller.Forbid();
        var machine = await ResolveMachineAsync(ids, machineId, ct);
        if (machine.Error is { } midErr)
            return midErr;
        return Json(await reads.ProcessesAsync(caller, machine.Id, ct));
    }

    private static async Task<IResult> GetProcessAsync(
        HttpContext http, TokenService tokens, Guid id, HubReads reads, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (!caller.MayProcesses)
            return HubCaller.Forbid();
        var doc = await reads.ProcessAsync(id, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListTeamsAsync(
        HttpContext http, TokenService tokens, HubReads reads, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (caller.Principal is Principal.Worker)
            return HubCaller.Forbid();
        return Json(await reads.TeamsAsync(caller, ct));
    }

    private static async Task<IResult> GetTeamAsync(
        HttpContext http, TokenService tokens, string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (caller.Principal is Principal.Worker)
            return HubCaller.Forbid();
        var team = await ResolveTeamAsync(ids, id, caller, ct, required: true);
        if (team.Error is { } teamErr)
            return teamErr;
        var doc = await reads.TeamAsync(team.Id!.Value, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListFrictionAsync(
        HttpContext http, TokenService tokens, HubReads reads, FriendlyIds ids,
        string? teamId, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (caller.Principal is Principal.Worker)
            return HubCaller.Forbid();
        var team = await ResolveTeamAsync(ids, teamId, caller, ct);
        if (team.Error is { } teamErr)
            return teamErr;
        return Json(await reads.FrictionAsync(caller, team.Id, ct));
    }

    private static async Task<IResult> ListLeadEventsAsync(
        HttpContext http, TokenService tokens, HubReads reads, FriendlyIds ids,
        string? teamId, CancellationToken ct)
    {
        var caller = await GateAsync(http, tokens, ct);
        if (caller.Error is { } err)
            return err;
        if (caller.Principal is Principal.Worker)
            return HubCaller.Forbid();
        var team = await ResolveTeamAsync(ids, teamId, caller, ct);
        if (team.Error is { } teamErr)
            return teamErr;
        return Json(await reads.LeadEventsAsync(caller, team.Id, ct));
    }

    private static Task<HubCaller> GateAsync(HttpContext http, TokenService tokens, CancellationToken ct) =>
        HubCaller.ResolveAsync(http, tokens, ct);

    private static JsonHttpResult<object> Json(object body) =>
        TypedResults.Json(body, HubEndpoints.Json);

    private static IResult NotFound() =>
        TypedResults.Json(new { error = "not found" }, HubEndpoints.Json, statusCode: StatusCodes.Status404NotFound);

    private static IResult BadId() =>
        TypedResults.Json(new { error = "invalid id" }, HubEndpoints.Json, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<(Guid? Id, IResult? Error)> ResolveTeamAsync(
        FriendlyIds ids, string? text, HubCaller caller, CancellationToken ct, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return required ? (null, BadId()) : (null, null);
        var team = await ids.TryTeamAsync(text, ct);
        if (team is not { } t || !caller.MayTeam(t.Value))
            return (null, NotFound());
        return (t.Value, null);
    }

    private static async Task<(Guid? Id, IResult? Error)> ResolveSessionAsync(
        FriendlyIds ids, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);
        var session = await ids.TrySessionAsync(text, ct);
        return session is { } s ? (s.Value, null) : (null, NotFound());
    }

    private static async Task<(Guid? Id, IResult? Error)> ResolveMachineAsync(
        FriendlyIds ids, string? text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);
        var machine = await ids.TryMachineAsync(text, ct);
        return machine is { } m ? (m, null) : (null, NotFound());
    }
}
