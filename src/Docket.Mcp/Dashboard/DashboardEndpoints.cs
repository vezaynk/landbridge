using System.Text.Json;
using System.Text.Json.Serialization;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Microsoft.Extensions.Configuration;

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
/// A thin transport shell, in the house style of <see cref="EnrollmentEndpoints"/>:
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

        app.MapGet("/dashboard/login", (string? error, string? next) =>
            Html(DashboardRenderer.Login(error, next)));

        app.MapPost("/dashboard/login", HandleLoginAsync);

        // §8.4 gated-browser-flow confirm: an operator with a docket_session confirms
        // access to a preview's Team and the plane mints a one-time code, sent back
        // to the preview origin. Open like login (it establishes, not consumes, auth).
        app.MapGet("/dashboard/preview-auth", HandlePreviewAuthAsync);
        app.MapPost("/dashboard/logout", (HttpContext http) =>
        {
            DashboardAuth.ClearSessionCookie(http);
            return Results.Redirect("/dashboard/login");
        });

        // Gated views. "/dashboard" lands on the Machine Group view.
        app.MapGet("/dashboard", () => Results.Redirect("/dashboard/machines"));

        app.MapGet("/dashboard/machines", async (
            HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock, CancellationToken ct) =>
            await Gated(http, tokens, ct, async _ =>
            {
                var machines = await queries.GetMachinesAsync(ct);
                return Negotiated(http, machines, () => DashboardRenderer.Machines(machines, clock.GetUtcNow()));
            }));

        app.MapGet("/dashboard/teams", async (
            HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock, CancellationToken ct) =>
            await Gated(http, tokens, ct, async _ =>
            {
                var teams = await queries.GetTeamsAsync(ct);
                return Negotiated(http, teams, () => DashboardRenderer.Teams(teams, clock.GetUtcNow()));
            }));

        app.MapGet("/dashboard/teams/{teamId}", async (
            string teamId, HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock,
            CancellationToken ct) =>
            await Gated(http, tokens, ct, async principal =>
            {
                if (!Guid.TryParse(teamId, out var id))
                    return Results.BadRequest(new { error = "invalid team id" });
                var team = await queries.GetTeamAsync(id, ct);
                if (team is null)
                    return WantsJson(http)
                        ? Results.Json(new { error = "no such team" }, Json, statusCode: 404)
                        : Html(DashboardRenderer.Login(null), 404); // unknown team: back to a known surface
                // The budget form is drawn for a human only (§9.9); the JSON twin carries the
                // same numbers either way, since reading a ceiling is not the gated act.
                return Negotiated(http, team, () =>
                    DashboardRenderer.TeamDetail(team, clock.GetUtcNow(), principal is Principal.Human));
            }));

        app.MapGet("/dashboard/inbox", async (
            HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock, CancellationToken ct) =>
            await Gated(http, tokens, ct, async _ =>
            {
                var inbox = await queries.GetInboxAsync(ct);
                return Negotiated(http, inbox, () => DashboardRenderer.Inbox(inbox, clock.GetUtcNow()));
            }));

        app.MapGet("/dashboard/events", async (
            HttpContext http, TokenService tokens, DashboardQueries queries, TimeProvider clock, CancellationToken ct) =>
            await Gated(http, tokens, ct, async _ =>
            {
                var events = await queries.GetEventsAsync(200, ct);
                return Negotiated(http, events, () => DashboardRenderer.Events(events, clock.GetUtcNow()));
            }));

        // §12 preview mint: 'Create preview' from the Team's registered-services view.
        app.MapPost("/dashboard/preview", HandleCreatePreviewAsync);

        // §9.9 budget ceiling: human-only, and the ONLY write path — there is no MCP tool.
        app.MapPost("/dashboard/budget", HandleSetBudgetAsync);

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
        // Where to land after sign-in — only a local /dashboard path is honoured, so
        // the gated-preview flow can bounce the operator back to /dashboard/preview-auth
        // without opening a redirect to an arbitrary origin.
        var next = SafeNext(form["next"].ToString());

        // Secondary door: a pasted token. Taken only when the passphrase field is
        // blank, so the operator's normal path is never ambiguous.
        if (string.IsNullOrEmpty(passphrase))
        {
            if (string.IsNullOrEmpty(token))
                return Html(DashboardRenderer.Login("Enter the operator passphrase.", next), 400);

            var principal = await tokens.ValidateAsync(token, ct);
            if (principal is not (Principal.Human or Principal.Lead))
                return Html(DashboardRenderer.Login("That token is not a valid human or Lead session.", next), 401);

            // A session cookie (no Expires): the pasted token's real lifetime lives
            // server-side and ValidateAsync re-checks it on every request, so the
            // browser hint need not — and for a no-expiry Lead token must not —
            // fabricate one.
            DashboardAuth.SetSessionCookie(http, token, expiresAt: null);
            return Results.Redirect(next);
        }

        // Primary door: the operator passphrase. Fail-closed when unconfigured — the
        // server can verify no one, so it mints nothing (mirrors /oauth/authorize).
        if (!verifier.IsConfigured)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (!verifier.Verify(passphrase))
        {
            await Task.Delay(WrongPassphraseDelay, ct); // brute-force friction (§5)
            return Html(DashboardRenderer.Login("Incorrect operator passphrase.", next), 401);
        }

        // Verified operator → mint a fresh human session (§5, the root credential)
        // and drop the cookie for its own 12h lifetime.
        var issued = await tokens.IssueHumanSessionAsync(ct);
        DashboardAuth.SetSessionCookie(http, issued.Token, issued.ExpiresAt);
        return Results.Redirect(next);
    }

    /// <summary>
    /// A post-login redirect target restricted to a local <c>/dashboard/…</c> path
    /// (no <c>//</c> host-relative escape), so a <c>?next=</c> can carry the operator
    /// back into the gated-preview confirm without becoming an open redirect. Falls
    /// back to the Machine Group view.
    /// </summary>
    private static string SafeNext(string? next) =>
        !string.IsNullOrEmpty(next) && next.StartsWith("/dashboard/", StringComparison.Ordinal)
        && !next.StartsWith("/dashboard//", StringComparison.Ordinal)
            ? next
            : "/dashboard/machines";

    /// <summary>The wildcard preview base URL both mint surfaces build labels onto (§8.4).</summary>
    private static string PreviewUrlBase(IConfiguration config) =>
        config[PreviewMint.UrlBaseConfigKey]
        ?? Environment.GetEnvironmentVariable("DOCKET_PREVIEW_URL_BASE")
        ?? "http://preview.localhost";

    private static bool OperatorMayAccess(Principal principal, Docket.Core.TeamId team) => principal switch
    {
        Principal.Human => true,
        Principal.Lead l => l.Team == team,
        _ => false,
    };

    /// <summary>
    /// POST /dashboard/preview — mint a shareable preview for a registered service
    /// (§12 button, §8.4). Operator-gated; a Lead may mint only for its own Team. The
    /// service field is <c>{taskId}:{name}</c> from the Team view so the mapping binds
    /// the exact owning task. Returns the URL (HTML result page, or JSON twin).
    /// </summary>
    private static async Task<IResult> HandleCreatePreviewAsync(
        HttpContext http, TokenService tokens,
        [Microsoft.AspNetCore.Mvc.FromServices] PreviewMappingService previews,
        IConfiguration config, CancellationToken ct)
    {
        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        if (principal is null)
            return WantsJson(http)
                ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                : Results.Redirect("/dashboard/login");

        var form = await http.Request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["teamId"].ToString(), out var teamId))
            return Results.BadRequest(new { error = "invalid team id" });
        if (!OperatorMayAccess(principal, new Docket.Core.TeamId(teamId)))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // "{taskId}:{name}" — the option value the Team view emits per service.
        var service = form["service"].ToString();
        var sep = service.IndexOf(':');
        if (sep <= 0 || !Guid.TryParse(service[..sep], out var taskId))
            return Results.BadRequest(new { error = "invalid service selection" });
        var serviceName = service[(sep + 1)..];

        var isPublic = string.Equals(form["auth"].ToString(), "public", StringComparison.OrdinalIgnoreCase);
        var policy = isPublic ? Docket.Core.PreviewAuthPolicy.Public : Docket.Core.PreviewAuthPolicy.Gated;
        var ttl = PreviewMint.ResolveTtl(policy, int.TryParse(form["ttl"].ToString(), out var m) ? m : null);

        var mint = await previews.CreateAsync(
            new Docket.Core.TeamId(teamId), new Docket.Core.TaskId(taskId), serviceName, policy, ttl, ct);
        var url = PreviewMint.Url(PreviewUrlBase(config), mint.Label);

        return WantsJson(http)
            ? Results.Json(new { url, auth = policy.ToString().ToLowerInvariant(), expiresAt = mint.Mapping.ExpiresAt }, Json)
            : Html(DashboardRenderer.PreviewCreated(url, policy, mint.Mapping.ExpiresAt, teamId));
    }

    /// <summary>
    /// POST /dashboard/budget — set a Team's budget ceiling and per-dispatch cap (§9.9).
    ///
    /// <para><b>Human-only, and the only write path that exists.</b> A Lead session is refused
    /// here even for its own Team — unlike every other Team-scoped surface on this dashboard,
    /// where <see cref="OperatorMayAccess"/> lets a Lead act within its own Team. The
    /// difference is the point: a budget is the control that bounds the Lead itself, so a Lead
    /// able to raise it is enforcement living exactly where a model can reason past it (§2
    /// principle 3). Same rule and same reason as the transcript endpoints' human gate, and
    /// there is deliberately no MCP tool anywhere for this.</para>
    ///
    /// <para>A blank field clears that limit rather than setting zero: zero is not a coherent
    /// ceiling (<see cref="TeamBudgetService.SetLimitsAsync"/> rejects it), and clearing means
    /// unbounded. Committed-to-date is never an input — raising a ceiling is how a human
    /// unblocks a Team, and must not double as erasing what was already authorized.</para>
    /// </summary>
    private static async Task<IResult> HandleSetBudgetAsync(
        HttpContext http, TokenService tokens,
        [Microsoft.AspNetCore.Mvc.FromServices] TeamBudgetService budgets,
        CancellationToken ct)
    {
        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        switch (principal)
        {
            case Principal.Human:
                break;
            case Principal.Lead:
                return WantsJson(http)
                    ? Results.Json(
                        new { error = "setting a team budget is human-only" }, Json,
                        statusCode: StatusCodes.Status403Forbidden)
                    : Results.Content(
                        DashboardRenderer.BudgetLeadRefused(), "text/html; charset=utf-8",
                        statusCode: StatusCodes.Status403Forbidden);
            default:
                return WantsJson(http)
                    ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                    : Results.Redirect("/dashboard/login");
        }

        var form = await http.Request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["teamId"].ToString(), out var teamId))
            return Results.BadRequest(new { error = "invalid team id" });

        if (!TryParseUsd(form["ceilingUsd"].ToString(), out var ceiling))
            return Results.BadRequest(new { error = "ceilingUsd must be a number, or blank to clear" });
        if (!TryParseUsd(form["perTaskUsd"].ToString(), out var perTask))
            return Results.BadRequest(new { error = "perTaskUsd must be a number, or blank to clear" });

        var team = new Docket.Core.TeamId(teamId);
        try
        {
            await budgets.SetLimitsAsync(team, ceiling, perTask, ct);
        }
        catch (ArgumentOutOfRangeException e)
        {
            // A non-positive amount: the service is the authority on what a coherent limit is,
            // so its refusal is surfaced rather than re-litigated here.
            return Results.BadRequest(new { error = e.Message });
        }

        var view = await budgets.ReadAsync(team, ct);
        return WantsJson(http)
            ? Results.Json(view, Json)
            : Html(DashboardRenderer.BudgetUpdated(teamId, view));
    }

    /// <summary>
    /// A USD form field: blank means "clear this limit" (null), otherwise an invariant decimal.
    /// Returns false only on input that is present but not a number, so a typo is a 400 rather
    /// than a silently cleared ceiling.
    /// </summary>
    private static bool TryParseUsd(string? raw, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        if (!decimal.TryParse(
                raw, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;
        value = parsed;
        return true;
    }

    /// <summary>
    /// GET /dashboard/preview-auth?label=&amp;return= — the gated-browser-flow confirm
    /// (§8.4). An operator with a live <c>docket_session</c> (host-scoped to the
    /// dashboard origin) confirms access to the preview's Team; the plane mints a
    /// one-time code and 302s it back to the preview origin. No session → bounce
    /// through login and back (<c>?next=</c>). The <c>return</c> is validated to be
    /// exactly the label's preview origin, so this can never become an open redirect.
    /// </summary>
    private static async Task<IResult> HandlePreviewAuthAsync(
        HttpContext http, TokenService tokens,
        [Microsoft.AspNetCore.Mvc.FromServices] PreviewMappingService previews,
        [Microsoft.AspNetCore.Mvc.FromServices] PreviewAuthStore previewAuth,
        IConfiguration config, CancellationToken ct)
    {
        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        if (principal is null)
        {
            var self = http.Request.Path + http.Request.QueryString;
            return Results.Redirect($"/dashboard/login?next={Uri.EscapeDataString(self)}");
        }

        var label = http.Request.Query["label"].ToString();
        var ret = http.Request.Query["return"].ToString();
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(ret))
            return Html(DashboardRenderer.PreviewAuthError("This preview link is malformed."), 400);

        if (await previews.ResolveAsync(label, ct) is not PreviewResolveResult.Found found)
            return Html(DashboardRenderer.PreviewAuthError("This preview no longer exists."), 404);
        if (!OperatorMayAccess(principal, new Docket.Core.TeamId(found.Mapping.TeamId)))
            return Html(DashboardRenderer.PreviewAuthError("Your session cannot access this preview's Team."), 403);

        // Open-redirect guard: the return must be exactly the label's preview origin.
        if (!ReturnIsPreviewOrigin(ret, PreviewUrlBase(config), label))
            return Html(DashboardRenderer.PreviewAuthError("This preview link points somewhere unexpected."), 400);

        var code = previewAuth.MintCode(label);
        var joiner = ret.Contains('?') ? '&' : '?';
        return Results.Redirect($"{ret}{joiner}docket_preview_code={Uri.EscapeDataString(code)}");
    }

    /// <summary>True iff <paramref name="ret"/> is an absolute URL whose origin is exactly the label's preview origin.</summary>
    private static bool ReturnIsPreviewOrigin(string ret, string previewBase, string label)
    {
        if (!Uri.TryCreate(ret, UriKind.Absolute, out var r))
            return false;
        var expected = new Uri(PreviewMint.Url(previewBase, label));
        return r.Scheme == expected.Scheme && string.Equals(r.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && r.Port == expected.Port;
    }

    /// <summary>
    /// Runs <paramref name="body"/> only for an authenticated human/Lead; otherwise
    /// a JSON caller gets 401 and a browser is redirected to the login page. The
    /// resolved principal is handed to the body because some views render differently
    /// for a human than for a Lead (§9.9's budget form); views that do not care ignore it.
    /// </summary>
    private static async Task<IResult> Gated(
        HttpContext http, TokenService tokens, CancellationToken ct, Func<Principal, Task<IResult>> body)
    {
        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        if (principal is null)
            return WantsJson(http)
                ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                : Results.Redirect("/dashboard/login");
        return await body(principal);
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
