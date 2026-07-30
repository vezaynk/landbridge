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

    /// <summary>Fixed delay added to a wrong-passphrase login, mirroring
    /// <c>/oauth/authorize</c>'s brute-force friction (§5). Kept in lockstep with
    /// that endpoint's <c>FailedAttemptDelay</c>.</summary>
    private static readonly TimeSpan WrongPassphraseDelay = TimeSpan.FromMilliseconds(500);

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
    /// POST /dashboard/login — the first-party operator door. The dashboard is a
    /// same-origin surface on the authorization server itself, so there is no OAuth
    /// redirect dance here: third-party MCP clients (Claude Code) use the OAuth 2.1
    /// flow (§5), while the operator signs in directly with the <b>same</b>
    /// <see cref="IOperatorVerifier"/> passphrase that flow verifies.
    ///
    /// Two mutually-exclusive submissions, decided by which field is filled:
    /// <list type="bullet">
    /// <item>the operator passphrase (the primary door) — fail-closed 503 when no
    /// operator credential is configured, a fixed <see cref="WrongPassphraseDelay"/>
    /// on a wrong guess (both mirroring <c>/oauth/authorize</c>), and on success a
    /// freshly-minted human session (§5) dropped as the <c>docket_session</c>
    /// cookie;</item>
    /// <item>a pasted token (the secondary door) — for a Lead token or a
    /// headless-minted human session; validated through the same
    /// <see cref="TokenService.ValidateAsync"/> as every other §5 path and set as
    /// the cookie only if it resolves to a human or a live Lead.</item>
    /// </list>
    /// The only state-changing POST besides logout, and it only sets the caller's
    /// own session — from a passphrase they present or a token they already hold —
    /// so there is no CSRF surface to protect in v1.
    /// </summary>
    private static async Task<IResult> HandleLoginAsync(
        HttpContext http, IOperatorVerifier verifier, TokenService tokens, CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var passphrase = form["passphrase"].ToString();
        var token = form["token"].ToString().Trim();

        // Secondary door: a pasted token. Taken only when the passphrase field is
        // blank, so the operator's normal path is never ambiguous.
        if (string.IsNullOrEmpty(passphrase))
        {
            if (string.IsNullOrEmpty(token))
                return Html(DashboardRenderer.Login("Enter the operator passphrase."), 400);

            var principal = await tokens.ValidateAsync(token, ct);
            if (principal is not (Principal.Human or Principal.Lead))
                return Html(DashboardRenderer.Login("That token is not a valid human or Lead session."), 401);

            // A session cookie (no Expires): the pasted token's real lifetime lives
            // server-side and ValidateAsync re-checks it on every request, so the
            // browser hint need not — and for a no-expiry Lead token must not —
            // fabricate one.
            DashboardAuth.SetSessionCookie(http, token, expiresAt: null);
            return Results.Redirect("/dashboard/machines");
        }

        // Primary door: the operator passphrase. Fail-closed when unconfigured — the
        // server can verify no one, so it mints nothing (mirrors /oauth/authorize).
        if (!verifier.IsConfigured)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (!verifier.Verify(passphrase))
        {
            await Task.Delay(WrongPassphraseDelay, ct); // brute-force friction (§5)
            return Html(DashboardRenderer.Login("Incorrect operator passphrase."), 401);
        }

        // Verified operator → mint a fresh human session (§5, the root credential)
        // and drop the cookie for its own 12h lifetime.
        var issued = await tokens.IssueHumanSessionAsync(ct);
        DashboardAuth.SetSessionCookie(http, issued.Token, issued.ExpiresAt);
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
