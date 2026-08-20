using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.ControlPlane;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Http.Features;

namespace Landbridge.Mcp;

/// <summary>
/// Lead-only HTTP inbox: one JSON snapshot and one SSE feed of the same
/// snapshot. Wakes on session NOTIFY via <see cref="SessionEventFanout"/>;
/// each event is a full Team snapshot, not a delta. Prose stays on
/// <c>get_session_question</c> / <c>get_session_report</c>.
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
        HttpContext http, SessionStore store, CancellationToken ct)
    {
        if (RejectUnlessLead(http) is { } reject)
            return reject;
        if (ParseSessionFilter(http) is { } bad)
            return bad;
        var sessionId = SessionFilterOf(http);
        var lead = LandbridgeClaims.AsLead(http.User)!;
        var inbox = await store.GetLeadInboxAsync(lead.Team, sessionId, ct);
        return Results.Json(inbox, Json);
    }

    private static async Task StreamEventsAsync(
        HttpContext http, SessionStore store, SessionEventFanout fanout, CancellationToken ct)
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

        var sessionId = SessionFilterOf(http);
        var lead = LandbridgeClaims.AsLead(http.User)!;
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers.Connection = "keep-alive";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var snapshots = LeadInboxWatch
            .Snapshots(store, fanout, lead.Team, sessionId, ct)
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
        var raw = http.Request.Query["sessionId"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (Guid.TryParse(raw, out _))
            return null;
        return Results.Json(new { error = "sessionId is not a valid session id" },
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static Guid? SessionFilterOf(HttpContext http)
    {
        var raw = http.Request.Query["sessionId"].ToString();
        return Guid.TryParse(raw, out var id) ? id : null;
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

        if (LandbridgeClaims.AsLead(http.User) is not null)
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
