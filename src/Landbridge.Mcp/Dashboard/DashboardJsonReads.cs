using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.Extensions.Configuration;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Serves the JSON twin of the dashboard views so <c>?format=json</c> never
/// goes through Blazor. Called from middleware before the component router.
/// </summary>
internal static class DashboardJsonReads
{
    public static async Task<bool> TryWriteAsync(HttpContext http)
    {
        var path = http.Request.Path.Value ?? "";
        if (!path.StartsWith("/dashboard", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!DashboardNegotiate.WantsJson(http) || !HttpMethods.IsGet(http.Request.Method))
            return false;

        var tokens = http.RequestServices.GetRequiredService<TokenService>();
        var queries = http.RequestServices.GetRequiredService<DashboardQueries>();
        var ct = http.RequestAborted;

        if (string.Equals(path, "/dashboard/connect", StringComparison.OrdinalIgnoreCase))
        {
            if (await DashboardAuth.ResolveAsync(http, tokens, ct) is null)
            {
                await WriteError(http, 401, "unauthorized");
                return true;
            }
            var config = http.RequestServices.GetRequiredService<IConfiguration>();
            await http.Response.WriteAsJsonAsync(
                ConnectEndpoints.Guide(ConnectEndpoints.ResolveMcpUrl(config, http)),
                DashboardNegotiate.Json, ct);
            return true;
        }

        if (string.Equals(path, "/dashboard/conformance", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequireHumanJsonAsync(http, tokens, ct))
                return true;
            await http.Response.WriteAsJsonAsync(new
            {
                post = "/dashboard/conformance",
                profileField = "required; exact-match name from the runner config",
                kinds = ConformanceCatalog.Kinds,
            }, DashboardNegotiate.Json, ct);
            return true;
        }

        if (path.StartsWith("/dashboard/conformance/", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequireHumanJsonAsync(http, tokens, ct))
                return true;
            var idText = path["/dashboard/conformance/".Length..];
            if (!Guid.TryParse(idText, out var runId))
            {
                http.Response.StatusCode = 400;
                await http.Response.WriteAsJsonAsync(new { error = "invalid run id" }, DashboardNegotiate.Json, ct);
                return true;
            }
            var registry = http.RequestServices.GetRequiredService<RunnerConnectionRegistry>();
            var rows = await queries.GetConformanceTasksAsync(runId, ct);
            if (rows.Count == 0)
            {
                http.Response.StatusCode = 404;
                await http.Response.WriteAsJsonAsync(new { error = "no such conformance run" }, DashboardNegotiate.Json, ct);
                return true;
            }
            var profile = rows[0].Profile ?? "";
            var tasks = rows.Select(r => new ConformanceSessionView(
                r.Id, ConformanceCatalog.KindOf(r.Description) ?? "unknown",
                r.State, r.Attempt, r.ResultReference, r.LastRequeueReason?.ToString(),
                r.MessageState)).ToList();
            var machines = new List<string>();
            foreach (var id in registry.MachineIds())
            {
                if (registry.SnapshotFor(id)?.DeclaredProfiles.Contains(profile) == true)
                    machines.Add(id);
            }
            await http.Response.WriteAsJsonAsync(
                ConformanceRunView.From(runId, profile, tasks, machines), DashboardNegotiate.Json, ct);
            return true;
        }

        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        if (principal is null)
        {
            await WriteError(http, 401, "unauthorized");
            return true;
        }

        IReadOnlyCollection<Guid>? teamScope = principal switch
        {
            Principal.Lead l => await tokens.OwnedTeamIdsAsync(l.CredentialId, ct),
            Principal.Human => null,
            _ => Array.Empty<Guid>(),
        };

        if (string.Equals(path, "/dashboard/machines", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/dashboard", StringComparison.OrdinalIgnoreCase))
        {
            if (principal is not Principal.Human)
            {
                await WriteError(http, 403,
                    "the machine group is a human-operator view; a Lead session sees its owned Teams "
                    + "on /dashboard/teams and through get_team_state");
                return true;
            }
            var machines = await queries.GetMachinesAsync(ct);
            await http.Response.WriteAsJsonAsync(machines, DashboardNegotiate.Json, ct);
            return true;
        }

        if (string.Equals(path, "/dashboard/teams", StringComparison.OrdinalIgnoreCase))
        {
            var teams = await queries.GetTeamsAsync(teamScope, ct);
            await http.Response.WriteAsJsonAsync(teams, DashboardNegotiate.Json, ct);
            return true;
        }

        if (path.StartsWith("/dashboard/teams/", StringComparison.OrdinalIgnoreCase))
        {
            var idText = path["/dashboard/teams/".Length..];
            if (!Guid.TryParse(idText, out var id))
            {
                http.Response.StatusCode = 400;
                await http.Response.WriteAsJsonAsync(new { error = "invalid team id" }, DashboardNegotiate.Json, ct);
                return true;
            }
            if (principal is Principal.Lead lead
                && (teamScope is null || !teamScope.Contains(id)))
            {
                await WriteError(http, 403, "this session may only read a Team it owns");
                return true;
            }
            var team = await queries.GetTeamAsync(id, ct);
            if (team is null)
            {
                http.Response.StatusCode = 404;
                await http.Response.WriteAsJsonAsync(new { error = "no such team" }, DashboardNegotiate.Json, ct);
                return true;
            }
            await http.Response.WriteAsJsonAsync(team, DashboardNegotiate.Json, ct);
            return true;
        }

        if (string.Equals(path, "/dashboard/inbox", StringComparison.OrdinalIgnoreCase))
        {
            var inbox = await queries.GetInboxAsync(teamScope, ct);
            await http.Response.WriteAsJsonAsync(inbox, DashboardNegotiate.Json, ct);
            return true;
        }

        if (string.Equals(path, "/dashboard/events", StringComparison.OrdinalIgnoreCase))
        {
            var events = await queries.GetEventsAsync(200, teamScope, ct);
            await http.Response.WriteAsJsonAsync(events, DashboardNegotiate.Json, ct);
            return true;
        }

        if (string.Equals(path, "/dashboard/friction", StringComparison.OrdinalIgnoreCase))
        {
            var friction = await queries.GetFrictionAsync(200, teamScope, ct);
            await http.Response.WriteAsJsonAsync(friction, DashboardNegotiate.Json, ct);
            return true;
        }

        return false;
    }

    private static async Task<bool> RequireHumanJsonAsync(
        HttpContext http, TokenService tokens, CancellationToken ct)
    {
        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        if (principal is null)
        {
            await WriteError(http, 401, "unauthorized");
            return false;
        }
        if (principal is not Principal.Human)
        {
            await WriteError(http, 403,
                "profile checks are a human-operator view; a Lead session sees its owned Teams "
                + "on /dashboard/teams and through get_team_state");
            return false;
        }
        return true;
    }

    private static async Task WriteError(HttpContext http, int status, string error)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { error }, DashboardNegotiate.Json);
    }
}
