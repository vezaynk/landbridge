using System.Text.Json;
using System.Text.Json.Serialization;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;

namespace Docket.Mcp.Dashboard;

/// <summary>
/// The §12 dashboard's HTTP surface: the three views plus the event log, each
/// served as server-rendered HTML and — from the same query layer — as a JSON twin
/// (§4/§12: consumable as structured data by a Lead). Routes are gated by
/// <see cref="DashboardAuth"/>'s own bearer-or-cookie resolution rather than
/// <c>.RequireAuthorization()</c>, so the browser path redirects to a login page
/// instead of tripping the MCP challenge. The one static asset (the stylesheet) and
/// the login/logout endpoints are deliberately open.
///
/// A thin transport shell, in the house style of <see cref="VerifierEndpoints"/>:
/// all reads live in <see cref="DashboardQueries"/>, all HTML in
/// <see cref="DashboardRenderer"/>; the handlers only resolve the caller, negotiate
/// the representation, and map to an <see cref="IResult"/>.
/// </summary>
public static class DashboardEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder app)
    {
        // Open: the single stylesheet, and the login/logout seam.
        app.MapGet("/dashboard/dashboard.css", () =>
            Results.Text(DashboardCss.Content, DashboardCss.ContentType));

        app.MapGet("/dashboard/login", (string? error) =>
            Html(DashboardRenderer.Login(error)));

        app.MapPost("/dashboard/login", HandleLoginAsync);
        app.MapPost("/dashboard/logout", (HttpContext http) =>
        {
            DashboardAuth.ClearSessionCookie(http);
            return Results.Redirect("/dashboard/login");
        });

        // Gated views. "/dashboard" lands on the Machine Group view.
        app.MapGet("/dashboard", () => Results.Redirect("/dashboard/machines"));

        app.MapGet("/dashboard/machines", async (
            HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock, CancellationToken ct) =>
            await Gated(http, tokens, ct, async () =>
            {
                var machines = await queries.GetMachinesAsync(ct);
                return Negotiated(http, machines, () => DashboardRenderer.Machines(machines, clock.GetUtcNow()));
            }));

        app.MapGet("/dashboard/teams", async (
            HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock, CancellationToken ct) =>
            await Gated(http, tokens, ct, async () =>
            {
                var teams = await queries.GetTeamsAsync(ct);
                return Negotiated(http, teams, () => DashboardRenderer.Teams(teams, clock.GetUtcNow()));
            }));

        app.MapGet("/dashboard/teams/{teamId}", async (
            string teamId, HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock,
            CancellationToken ct) =>
            await Gated(http, tokens, ct, async () =>
            {
                if (!Guid.TryParse(teamId, out var id))
                    return Results.BadRequest(new { error = "invalid team id" });
                var team = await queries.GetTeamAsync(id, ct);
                if (team is null)
                    return WantsJson(http)
                        ? Results.Json(new { error = "no such team" }, Json, statusCode: 404)
                        : Html(DashboardRenderer.Login(null), 404); // unknown team: back to a known surface
                return Negotiated(http, team, () => DashboardRenderer.TeamDetail(team, clock.GetUtcNow()));
            }));

        app.MapGet("/dashboard/inbox", async (
            HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock, CancellationToken ct) =>
            await Gated(http, tokens, ct, async () =>
            {
                var inbox = await queries.GetInboxAsync(ct);
                return Negotiated(http, inbox, () => DashboardRenderer.Inbox(inbox, clock.GetUtcNow()));
            }));

        app.MapGet("/dashboard/events", async (
            HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock, CancellationToken ct) =>
            await Gated(http, tokens, ct, async () =>
            {
                var events = await queries.GetEventsAsync(200, ct);
                return Negotiated(http, events, () => DashboardRenderer.Events(events, clock.GetUtcNow()));
            }));

        return app;
    }

    /// <summary>
    /// POST /dashboard/login — the v1 token paste-box (the OAuth seam). Validates
    /// the pasted token as a human or Lead and, on success, drops the
    /// <c>docket_session</c> cookie and lands on the dashboard; otherwise re-renders
    /// the login with an error. The only state-changing POST besides logout, and it
    /// only sets the caller's own session from a token they already hold — so there
    /// is no CSRF surface to protect in v1 (noted for the OAuth follow-up).
    /// </summary>
    private static async Task<IResult> HandleLoginAsync(HttpContext http, TokenService tokens, CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var token = form["token"].ToString().Trim();
        if (string.IsNullOrEmpty(token))
            return Html(DashboardRenderer.Login("Enter a token."), 400);

        var principal = await tokens.ValidateAsync(token, ct);
        if (principal is not (Principal.Human or Principal.Lead))
            return Html(DashboardRenderer.Login("That token is not a valid human session."), 401);

        // The cookie tracks the token's own lifetime; a human session is 12h (§5).
        DashboardAuth.SetSessionCookie(http, token, DateTimeOffset.UtcNow.Add(TokenService.HumanSessionTtl));
        return Results.Redirect("/dashboard/machines");
    }

    /// <summary>
    /// Runs <paramref name="body"/> only for an authenticated human/Lead; otherwise
    /// a JSON caller gets 401 and a browser is redirected to the login page.
    /// </summary>
    private static async Task<IResult> Gated(
        HttpContext http, TokenService tokens, CancellationToken ct, Func<Task<IResult>> body)
    {
        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        if (principal is null)
            return WantsJson(http)
                ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                : Results.Redirect("/dashboard/login");
        return await body();
    }

    /// <summary>Serve the model as JSON, or the rendered HTML, per content negotiation.</summary>
    private static IResult Negotiated<T>(HttpContext http, T model, Func<string> html) =>
        WantsJson(http) ? Results.Json(model, Json) : Html(html());

    /// <summary>
    /// JSON is requested by <c>?format=json</c> (the primary switch) or by an
    /// <c>Accept: application/json</c> that does not also accept HTML (a bare API
    /// client). A browser, which sends <c>text/html</c>, always gets HTML unless it
    /// explicitly asks with the query string.
    /// </summary>
    private static bool WantsJson(HttpContext http)
    {
        var format = http.Request.Query["format"].ToString();
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
            return false;
        var accept = http.Request.Headers.Accept.ToString();
        return accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Html(string html, int status = 200) =>
        Results.Content(html, "text/html; charset=utf-8", statusCode: status);
}
