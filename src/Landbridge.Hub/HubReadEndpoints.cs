using Landbridge.ControlPlane;
using Landbridge.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Landbridge.Hub;

/// <summary>
/// JSON twins for every hub catalog noun (and Teams / friction / lead events,
/// which are HTTP-only). Same host as the SSE wakes; refetch on
/// <c>event: change</c>. Still unauthenticated — auth lands when a client
/// consumes this.
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
        HubReads reads, FriendlyIds ids, string? teamId, bool hidden = false, CancellationToken ct = default)
    {
        var team = await ResolveTeamAsync(ids, teamId, ct);
        if (team.Error is { } err)
            return err;
        return Json(await reads.SessionsAsync(team.Id, hidden, ct));
    }

    private static async Task<IResult> GetSessionAsync(
        string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var session = await ResolveSessionAsync(ids, id, ct);
        if (session.Error is { } err)
            return err;
        var doc = await reads.SessionAsync(session.Id!.Value, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> GetLogAsync(
        string id, HubReads reads, FriendlyIds ids, int? limit, CancellationToken ct)
    {
        var session = await ResolveSessionAsync(ids, id, ct);
        if (session.Error is { } err)
            return err;
        var doc = await reads.LogAsync(session.Id!.Value, limit ?? HubReads.DefaultLimit, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> GetExchangeAsync(
        string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var session = await ResolveSessionAsync(ids, id, ct);
        if (session.Error is { } err)
            return err;
        var doc = await reads.ExchangeAsync(session.Id!.Value, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListSessionServicesAsync(
        string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var session = await ResolveSessionAsync(ids, id, ct);
        if (session.Error is { } err)
            return err;
        return Json(await reads.ServicesAsync(teamId: null, session.Id, ct));
    }

    private static async Task<IResult> ListServicesAsync(
        HubReads reads, FriendlyIds ids, string? teamId, string? sessionId, CancellationToken ct)
    {
        var team = await ResolveTeamAsync(ids, teamId, ct);
        if (team.Error is { } teamErr)
            return teamErr;
        var session = await ResolveSessionAsync(ids, sessionId, ct);
        if (session.Error is { } sessionErr)
            return sessionErr;
        return Json(await reads.ServicesAsync(team.Id, session.Id, ct));
    }

    private static async Task<IResult> ListForwardsAsync(
        HubReads reads, FriendlyIds ids, string? teamId, CancellationToken ct)
    {
        var team = await ResolveTeamAsync(ids, teamId, ct);
        if (team.Error is { } err)
            return err;
        return Json(await reads.ForwardsAsync(team.Id, ct));
    }

    private static async Task<IResult> GetForwardAsync(Guid id, HubReads reads, CancellationToken ct)
    {
        var doc = await reads.ForwardAsync(id, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListPreviewsAsync(
        HubReads reads, FriendlyIds ids, string? teamId, CancellationToken ct)
    {
        var team = await ResolveTeamAsync(ids, teamId, ct);
        if (team.Error is { } err)
            return err;
        return Json(await reads.PreviewsAsync(team.Id, ct));
    }

    private static async Task<IResult> GetPreviewAsync(Guid id, HubReads reads, CancellationToken ct)
    {
        var doc = await reads.PreviewAsync(id, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListMachinesAsync(HubReads reads, CancellationToken ct) =>
        Json(await reads.MachinesAsync(ct));

    private static async Task<IResult> GetMachineAsync(
        string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var machine = await ResolveMachineAsync(ids, id, ct);
        if (machine.Error is { } err)
            return err;
        var doc = await reads.MachineAsync(machine.Id!.Value, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListMachineProcessesAsync(
        string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var machine = await ResolveMachineAsync(ids, id, ct);
        if (machine.Error is { } err)
            return err;
        return Json(await reads.ProcessesAsync(machine.Id, ct));
    }

    private static async Task<IResult> ListProcessesAsync(
        HubReads reads, FriendlyIds ids, string? machineId, CancellationToken ct)
    {
        var machine = await ResolveMachineAsync(ids, machineId, ct);
        if (machine.Error is { } err)
            return err;
        return Json(await reads.ProcessesAsync(machine.Id, ct));
    }

    private static async Task<IResult> GetProcessAsync(Guid id, HubReads reads, CancellationToken ct)
    {
        var doc = await reads.ProcessAsync(id, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListTeamsAsync(HubReads reads, CancellationToken ct) =>
        Json(await reads.TeamsAsync(ct));

    private static async Task<IResult> GetTeamAsync(
        string id, HubReads reads, FriendlyIds ids, CancellationToken ct)
    {
        var team = await ResolveTeamAsync(ids, id, ct, required: true);
        if (team.Error is { } err)
            return err;
        var doc = await reads.TeamAsync(team.Id!.Value, ct);
        return doc is null ? NotFound() : Json(doc);
    }

    private static async Task<IResult> ListFrictionAsync(
        HubReads reads, FriendlyIds ids, string? teamId, CancellationToken ct)
    {
        var team = await ResolveTeamAsync(ids, teamId, ct);
        if (team.Error is { } err)
            return err;
        return Json(await reads.FrictionAsync(team.Id, ct));
    }

    private static async Task<IResult> ListLeadEventsAsync(
        HubReads reads, FriendlyIds ids, string? teamId, CancellationToken ct)
    {
        var team = await ResolveTeamAsync(ids, teamId, ct);
        if (team.Error is { } err)
            return err;
        return Json(await reads.LeadEventsAsync(team.Id, ct));
    }

    private static JsonHttpResult<object> Json(object body) =>
        TypedResults.Json(body, HubEndpoints.Json);

    private static IResult NotFound() =>
        TypedResults.Json(new { error = "not found" }, HubEndpoints.Json, statusCode: StatusCodes.Status404NotFound);

    private static IResult BadId() =>
        TypedResults.Json(new { error = "invalid id" }, HubEndpoints.Json, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<(Guid? Id, IResult? Error)> ResolveTeamAsync(
        FriendlyIds ids, string? text, CancellationToken ct, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return required ? (null, BadId()) : (null, null);
        var team = await ids.TryTeamAsync(text, ct);
        return team is { } t ? (t.Value, null) : (null, NotFound());
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
