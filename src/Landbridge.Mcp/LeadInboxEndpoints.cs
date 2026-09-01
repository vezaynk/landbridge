using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Http.Features;

namespace Landbridge.Mcp;

/// <summary>
/// Lead-only HTTP inbox: one JSON snapshot and one SSE feed of the same
/// snapshot. <c>?teamId=</c> is required (a Team this factory owns). Wakes on
/// session NOTIFY via <see cref="SessionEventFanout"/>; each event is a full
/// Team snapshot, not a delta. Team-wide is identifiers only;
/// <c>?sessionId=</c> (repeatable) carries bodies and marks unread report mail
/// as read.
/// </summary>
public static class LeadInboxEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    public static IEndpointRouteBuilder MapLeadInbox(this IEndpointRouteBuilder app)
    {
        app.MapGet("/lead/inbox", GetSnapshotAsync).RequireAuthorization();
        app.MapGet("/lead/inbox/events", StreamEventsAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> GetSnapshotAsync(
        HttpContext http, SessionStore store, TokenService tokens, CancellationToken ct)
    {
        if (RejectUnlessLead(http) is { } reject)
            return reject;
        if (ParseSessionFilter(http) is { } bad)
            return bad;
        var filter = SessionFilterOf(http);
        var factory = LandbridgeClaims.AsLeadPrincipal(http.User)!;
        var team = await RequireOwnedTeamAsync(http, factory, tokens, ct);
        if (team is null)
            return Results.Json(new { error = "teamId is required: a team id from create_team, or one a human gave you" },
                statusCode: StatusCodes.Status400BadRequest);
        var actor = filter is { Count: > 0 } ? new Landbridge.Core.LeadClaim(team.Value) : (Landbridge.Core.Actor?)null;
        var inbox = await store.GetLeadInboxAsync(team.Value, filter, ct, actor);
        return Results.Json(inbox, Json);
    }

    private static async Task StreamEventsAsync(
        HttpContext http, SessionStore store, SessionEventFanout fanout, TokenService tokens, CancellationToken ct)
    {
        if (RejectUnlessLead(http) is { } reject)
        {
            await reject.ExecuteAsync(http);
            return;
        }

        if (ParseSessionFilter(http) is { } bad)
        {
            await bad.ExecuteAsync(http);
            return;
        }

        var filter = SessionFilterOf(http);
        var factory = LandbridgeClaims.AsLeadPrincipal(http.User)!;
        var team = await RequireOwnedTeamAsync(http, factory, tokens, ct);
        if (team is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "teamId is required: a team id from create_team, or one a human gave you" }, ct);
            return;
        }
        var actor = filter is { Count: > 0 } ? new Landbridge.Core.LeadClaim(team.Value) : (Landbridge.Core.Actor?)null;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers.Connection = "keep-alive";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var snapshots = LeadInboxWatch
            .Snapshots(store, fanout, team.Value, filter, actor, ct)
            .GetAsyncEnumerator(ct);
        var next = snapshots.MoveNextAsync().AsTask();

        while (!ct.IsCancellationRequested)
        {
            bool moved;
            try
            {
                moved = await next.WaitAsync(PingInterval, ct);
            }
            catch (TimeoutException)
            {
                await WritePingAsync(http, ct);
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!moved)
                break;

            await WriteSnapshotAsync(http, snapshots.Current, ct);
            next = snapshots.MoveNextAsync().AsTask();
        }
    }

    private static IResult? ParseSessionFilter(HttpContext http)
    {
        foreach (var raw in SessionIdValues(http))
        {
            if (!Guid.TryParse(raw, out _))
            {
                return Results.Json(new { error = "sessionId is not a valid session id" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }
        return null;
    }

    private static IReadOnlyList<Guid>? SessionFilterOf(HttpContext http)
    {
        var ids = new List<Guid>();
        foreach (var raw in SessionIdValues(http))
        {
            if (Guid.TryParse(raw, out var id))
                ids.Add(id);
        }
        return ids.Count == 0 ? null : ids;
    }

    private static IEnumerable<string> SessionIdValues(HttpContext http)
    {
        foreach (var raw in http.Request.Query["sessionId"])
        {
            if (!string.IsNullOrWhiteSpace(raw))
                yield return raw;
        }
        foreach (var raw in http.Request.Query["sessionIds"])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
        }
    }

    private static async Task<TeamId?> RequireOwnedTeamAsync(
        HttpContext http, Principal.Lead factory, TokenService tokens, CancellationToken ct)
    {
        var raw = http.Request.Query["teamId"].ToString();
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out var g))
            return null;
        var team = new TeamId(g);
        return await tokens.OwnsTeamAsync(factory.CredentialId, team, ct) ? team : null;
    }

    private static IResult? RejectUnlessLead(HttpContext http)
    {
        if (LandbridgeClaims.AsEvictedLead(http.User) is { } evicted)
        {
            return Results.Json(new
            {
                error = $"your lead claim on team {evicted.Team.Value:N} was taken over by human "
                    + $"{evicted.EvictedByHuman:N} at {evicted.EvictedAt:O}; reattach to the Team to continue.",
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (LandbridgeClaims.AsLeadPrincipal(http.User) is not null)
            return null;

        return Results.Json(new { error = "lead credential required" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static async Task WriteSnapshotAsync(HttpContext http, LeadInboxView inbox, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(inbox, Json);
        await http.Response.WriteAsync("event: snapshot\ndata: ", ct);
        await http.Response.WriteAsync(json, ct);
        await http.Response.WriteAsync("\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    private static async Task WritePingAsync(HttpContext http, CancellationToken ct)
    {
        await http.Response.WriteAsync(": ping\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
