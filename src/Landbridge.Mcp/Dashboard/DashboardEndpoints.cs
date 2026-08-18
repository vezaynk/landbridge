using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp.Dashboard.Components.Pages;
using Landbridge.Web;
using Microsoft.Extensions.Configuration;
using static Landbridge.Mcp.Dashboard.DashboardHosting;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// The §12 dashboard's HTTP surface: the three views plus the event log, each
/// served as Blazor Server HTML and — from the same query layer — as a JSON twin
/// (§4/§12: consumable as structured data by a Lead). Routes are gated by
/// <see cref="DashboardAuth"/>'s own bearer-or-cookie resolution rather than
/// <c>.RequireAuthorization()</c>, so the browser path redirects to a login page
/// instead of tripping the MCP challenge. The one static asset (the stylesheet) and
/// the login/logout endpoints are deliberately open.
///
/// A thin transport shell, in the house style of <see cref="EnrollmentEndpoints"/>:
/// all reads live in <see cref="DashboardQueries"/>, HTML is Blazor Server,
/// and JSON is <see cref="DashboardJsonReads"/>.
///
/// <para>Two rules run across the whole surface rather than route by route. Reads are scoped to
/// what the resolved principal may see — a human operator reads the instance, a Lead reads its
/// own Team (<see cref="Gated"/>). Mutating POSTs must come from this dashboard's own origin
/// (<see cref="CrossOriginRefusal"/>), because the session cookie is the only thing they need
/// and a browser will attach it to anyone's form.</para>
/// </summary>
public static class DashboardEndpoints
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = DashboardNegotiate.Json;

    /// <summary>Fixed delay added to a wrong-passphrase login, mirroring
    /// <c>/oauth/authorize</c>'s brute-force friction (§5). Kept in lockstep with
    /// that endpoint's <c>FailedAttemptDelay</c>.</summary>
    private static readonly TimeSpan WrongPassphraseDelay = TimeSpan.FromMilliseconds(500);

    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder app)
    {
        if (app is WebApplication web)
        {
            web.UseAntiforgery();
            web.Use(async (ctx, next) =>
            {
                if (await DashboardJsonReads.TryWriteAsync(ctx))
                    return;
                await next();
            });
        }

        // Open: the single stylesheet, and the login/logout seam.
        app.MapGet("/dashboard/dashboard.css", () =>
            Results.Text(DashboardCss.Content, DashboardCss.ContentType));

        // Order beats the Blazor @page on the same path so the form POST
        // stays the cookie/redirect handler rather than a circuit POST.
        app.MapPost("/dashboard/login", HandleLoginAsync).DisableAntiforgery().WithOrder(-100);

        // §8.4 gated-browser-flow confirm: an operator with a landbridge_session confirms
        // access to a preview's Team and the plane mints a one-time code, sent back
        // to the preview origin. Open like login (it establishes, not consumes, auth).
        app.MapGet("/dashboard/preview-auth", HandlePreviewAuthAsync);
        app.MapPost("/dashboard/logout", (HttpContext http, IConfiguration config) =>
        {
            if (CrossOriginRefusal(http, config) is { } refusal)
                return refusal;
            DashboardAuth.ClearSessionCookie(http);
            return Results.Redirect("/dashboard/login");
        }).DisableAntiforgery().WithOrder(-100);

        // Gated views. "/dashboard" lands on the Machine Group view.
        app.MapGet("/dashboard", () => Results.Redirect("/dashboard/machines"));

        // HTML GETs are Blazor pages (MapDashboardUi). JSON twins are served by
        // DashboardJsonReads before the router. These POSTs stay HTTP so cookie
        // writes, redirects, and same-origin checks stay on the request.

        // §12 preview mint: 'Create preview' from the Team's registered-services view.
        app.MapPost("/dashboard/preview", HandleCreatePreviewAsync).DisableAntiforgery().WithOrder(-100);

        // §11 permission bridge: the human's answer to a pending permission request, from
        // the inbox. This one has an MCP twin (the Lead's answer_permission_request) — the
        // point of the bridge is that both answerers reach the same request, with the human
        // able to answer any of them.
        app.MapPost("/dashboard/permission", HandleDecidePermissionAsync).DisableAntiforgery().WithOrder(-100);

        // §5/§13 un-trust a machine, from the Machine Group view. Human-only and with no
        // Lead twin, deliberately: this is the incident-response action, and the §5
        // requirement it serves is that it takes seconds.
        app.MapPost("/dashboard/machines/revoke", HandleRevokeMachineAsync).DisableAntiforgery().WithOrder(-100);

        app.MapConformance();

        if (app is WebApplication host)
            host.MapDashboardUi();

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
    /// freshly-minted human session (§5) dropped as the <c>landbridge_session</c>
    /// cookie;</item>
    /// <item>a pasted token (the secondary door) — for a Lead token or a
    /// headless-minted human session; validated through the same
    /// <see cref="TokenService.ValidateAsync"/> as every other §5 path and set as
    /// the cookie only if it resolves to a human or a live Lead.</item>
    /// </list>
    /// <para>Same-origin only, like every other mutating POST here
    /// (<see cref="CrossOriginRefusal"/>). This one sets no <em>existing</em> session, so the
    /// exposure is the inverse of the usual: a cross-site POST here would log the operator into
    /// a session the attacker minted, and everything they then did on this dashboard would be
    /// happening in it.</para>
    /// </summary>
    private static async Task<IResult> HandleLoginAsync(
        HttpContext http, IOperatorVerifier verifier, TokenService tokens, IConfiguration config,
        CancellationToken ct)
    {
        if (CrossOriginRefusal(http, config) is { } refusal)
            return refusal;

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
                return RazorPage<LoginResult>(new { Error = "Enter the operator passphrase.", Next = next }, 400);

            var principal = await tokens.ValidateAsync(token, ct);
            if (principal is not (Principal.Human or Principal.Lead))
                return RazorPage<LoginResult>(new { Error = "That token is not a valid human or Lead session.", Next = next }, 401);

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
            return RazorPage<LoginResult>(new
            {
                Error = "No operator passphrase is configured. Set Landbridge:Operator:PassphraseHash "
                    + "to the SHA-256 hex of the passphrase (docs/RUNNING.md). "
                    + "The Aspire / Development host uses the passphrase 'dev'.",
                Next = next,
            }, StatusCodes.Status503ServiceUnavailable);

        if (!verifier.Verify(passphrase))
        {
            await Task.Delay(WrongPassphraseDelay, ct); // brute-force friction (§5)
            return RazorPage<LoginResult>(new { Error = "Incorrect operator passphrase.", Next = next }, 401);
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
        ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PREVIEW_URL_BASE")
        ?? "http://preview.localhost";

    private static bool OperatorMayAccess(Principal principal, Landbridge.Core.TeamId team) => principal switch
    {
        Principal.Human => true,
        Principal.Lead l => l.Team == team,
        _ => false,
    };

    /// <summary>
    /// POST /dashboard/preview — mint a shareable preview for a registered service
    /// (§12 button, §8.4). Operator-gated; a Lead may mint only for its own Team. The
    /// service field is <c>{sessionId}:{name}</c> from the Team view so the mapping binds
    /// the exact owning task. Returns the URL (HTML result page, or JSON twin).
    /// </summary>
    private static async Task<IResult> HandleCreatePreviewAsync(
        HttpContext http, TokenService tokens,
        [Microsoft.AspNetCore.Mvc.FromServices] PreviewMappingService previews,
        IConfiguration config, CancellationToken ct)
    {
        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        if (principal is null)
            return DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                : Results.Redirect("/dashboard/login");

        var form = await http.Request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["teamId"].ToString(), out var teamId))
            return Results.BadRequest(new { error = "invalid team id" });
        if (!OperatorMayAccess(principal, new Landbridge.Core.TeamId(teamId)))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // "{sessionId}:{name}" — the option value the Team view emits per service.
        var service = form["service"].ToString();
        var sep = service.IndexOf(':');
        if (sep <= 0 || !Guid.TryParse(service[..sep], out var sessionId))
            return Results.BadRequest(new { error = "invalid service selection" });
        var serviceName = service[(sep + 1)..];

        var isPublic = string.Equals(form["auth"].ToString(), "public", StringComparison.OrdinalIgnoreCase);
        var policy = isPublic ? Landbridge.Core.PreviewAuthPolicy.Public : Landbridge.Core.PreviewAuthPolicy.Gated;
        var ttl = PreviewMint.ResolveTtl(policy, int.TryParse(form["ttl"].ToString(), out var m) ? m : null);

        var mint = await previews.CreateAsync(
            new Landbridge.Core.TeamId(teamId), new Landbridge.Core.SessionId(sessionId), serviceName, policy, ttl, ct);
        var url = PreviewMint.Url(PreviewUrlBase(config), mint.Label);

        return DashboardNegotiate.WantsJson(http)
            ? Results.Json(new { url, auth = policy.ToString().ToLowerInvariant(), expiresAt = mint.Mapping.ExpiresAt }, Json)
            : RazorPage<PreviewCreatedPage>(new { Url = url, Policy = policy, ExpiresAt = mint.Mapping.ExpiresAt, TeamId = teamId });
    }

    /// <summary>
    /// POST /dashboard/permission — a human decides a pending permission request (§11/§12).
    ///
    /// <para>Human-only, like every other §12 write, and for the plainer of the two reasons
    /// this time: the Lead already has its own tool for this, so the form exists precisely
    /// for the answerer the Lead can hand a request to. A human may decide <em>any</em>
    /// pending request, escalated or not — escalation removes the Lead's authority, it does
    /// not create the human's — so this handler does not check the escalation state at all.
    /// The engine does the rest, including refusing a Lead who reached this path with a
    /// pasted token.</para>
    ///
    /// <para>The refusals here are the ordinary ones for a form on an auto-refreshing page:
    /// between the render and the submit the worker may have given up, the wait-TTL sweeper
    /// may have parked the task, or another answerer may have decided it. Each surfaces the
    /// engine's own reason rather than a generic failure, because "someone else already
    /// allowed this" and "this one expired" call for different next moves.</para>
    /// </summary>
    private static async Task<IResult> HandleDecidePermissionAsync(
        HttpContext http, TokenService tokens,
        [Microsoft.AspNetCore.Mvc.FromServices] SessionStore store,
        IConfiguration config,
        CancellationToken ct)
    {
        // Before the session is even resolved: this POST needs only a task id and a verdict, so
        // whether it came from this dashboard is a question about the request, not the caller.
        if (CrossOriginRefusal(http, config) is { } refusal)
            return refusal;

        var principal = await DashboardAuth.ResolveAsync(http, tokens, ct);
        switch (principal)
        {
            case Principal.Human:
                break;
            case Principal.Lead:
                return DashboardNegotiate.WantsJson(http)
                    ? Results.Json(
                        new { error = "answering from the dashboard is human-only; a Lead uses answer_permission_request" },
                        Json, statusCode: StatusCodes.Status403Forbidden)
                    : RazorPage<PermissionRefusedPage>(
                        new { Reason = "Answering from the dashboard is human-only. A Lead answers with "
                            + "answer_permission_request, and cannot answer a request it escalated." },
                        StatusCodes.Status403Forbidden);
            default:
                return DashboardNegotiate.WantsJson(http)
                    ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                    : Results.Redirect("/dashboard/login");
        }

        var form = await http.Request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["sessionId"].ToString(), out var sessionId))
            return Results.BadRequest(new { error = "invalid task id" });
        if (!Enum.TryParse<Landbridge.Core.PermissionVerdict>(
                form["verdict"].ToString(), ignoreCase: true, out var verdict))
            return Results.BadRequest(new { error = "verdict must be 'allow' or 'deny'" });

        var message = form["message"].ToString();
        var id = new Landbridge.Core.SessionId(sessionId);
        var result = await store.AnswerPermissionAsync(
            new Landbridge.Core.HumanSession(), id, verdict,
            string.IsNullOrWhiteSpace(message) ? null : message, ct);

        if (result is not StoreResult.Applied)
        {
            var reason = result switch
            {
                StoreResult.Rejected r => $"{r.Reason} ({r.Rule})",
                StoreResult.NotFound n => n.Reason,
                StoreResult.Conflict c => c.Reason,
                _ => "unknown store result",
            };
            return DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = reason }, Json, statusCode: StatusCodes.Status409Conflict)
                : RazorPage<PermissionRefusedPage>(new { Reason = reason }, StatusCodes.Status409Conflict);
        }

        // Re-read so the confirmation reports what actually landed, not what was posted.
        var decided = await store.GetPermissionRequestAsync(id, ct);
        return decided is null
            ? Results.NotFound(new { error = "task disappeared" })
            : Negotiated(http, decided, () => RazorPage<PermissionDecidedPage>(new { Request = decided }));
    }

    /// <summary>
    /// POST /dashboard/machines/revoke — un-trust an enrolled machine (§5, §13).
    ///
    /// <para>The whole revoke, through <see cref="MachineRevocationService"/>: credentials,
    /// the live <c>/runner</c> channel, and the worker tokens on the box. Until this existed
    /// the operation had <em>no</em> surface at all — a compromised machine could only be
    /// un-trusted by editing the database — which is the gap that made §5's
    /// "un-trusting a machine must take seconds" untrue in practice.</para>
    ///
    /// <para><b>Human-only</b>, exactly like the <c>/dashboard/machines</c> view it is a
    /// control on and for the same reason (<see cref="MachinesAreHumanOnly"/>): a machine is
    /// not Team-scoped. A Lead is a Team-scoped harness client, so letting one revoke a
    /// machine would let a Team un-trust infrastructure other Teams are working on — a
    /// strictly worse version of the over-scoped read the view itself was closed against.
    /// There is no MCP twin for the same reason, so the refusal names what a Lead should do
    /// instead of implying a tool exists.</para>
    ///
    /// <para><b>Same-origin, like every other mutating form here</b>
    /// (<see cref="CrossOriginRefusal"/>) — and this one is the most valuable POST on the
    /// surface to forge: it needs nothing but a machine id, the ids are on a page the session
    /// can already read, and a successful forgery evicts a machine mid-flight. Checked before
    /// the session is resolved, because whether the request came from this dashboard is a
    /// question about the request rather than about the caller.</para>
    ///
    /// <para>Idempotent and total: a machine that is offline, unknown, or already revoked
    /// still returns 200 with a report of nothing taken away, because a caller reaching for
    /// this in an incident should not have to establish the machine's state first. The
    /// Machine Group view only lists <em>connected</em> machines, so a JSON caller naming an
    /// offline machine's id directly is the path for one that has already dropped.</para>
    /// </summary>
    private static async Task<IResult> HandleRevokeMachineAsync(
        HttpContext http, TokenService tokens,
        [Microsoft.AspNetCore.Mvc.FromServices] MachineRevocationService revocations,
        IConfiguration config,
        CancellationToken ct)
    {
        if (CrossOriginRefusal(http, config) is { } refusal)
            return refusal;

        return await Gated(http, tokens, ct, async principal =>
        {
            if (principal is not Principal.Human)
                return Refused(http, RevokingIsHumanOnly);

            var form = await http.Request.ReadFormAsync(ct);
            if (!Guid.TryParse(form["machineId"].ToString(), out var machineId))
                return Results.BadRequest(new { error = "invalid machine id" });

            var revoked = await revocations.RevokeAsync(machineId, ct);
            return DashboardNegotiate.WantsJson(http)
                ? Results.Json(
                    new
                    {
                        machineId,
                        channelClosed = revoked.ChannelClosed,
                        tasksRequeued = revoked.SessionsRequeued,
                        workersRevoked = revoked.WorkersRevoked,
                    }, Json)
                : RazorPage<MachineRevokedPage>(new { MachineId = machineId, Revoked = revoked });
        });
    }

    /// <summary>
    /// GET /dashboard/preview-auth?label=&amp;return= — the gated-browser-flow confirm
    /// (§8.4). An operator with a live <c>landbridge_session</c> (host-scoped to the
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
            return RazorPage<PreviewAuthErrorPage>(new { Message = "This preview link is malformed." }, 400);

        if (await previews.ResolveAsync(label, ct) is not PreviewResolveResult.Found found)
            return RazorPage<PreviewAuthErrorPage>(new { Message = "This preview no longer exists." }, 404);
        if (!OperatorMayAccess(principal, new Landbridge.Core.TeamId(found.Mapping.TeamId)))
            return RazorPage<PreviewAuthErrorPage>(new { Message = "Your session cannot access this preview's Team." }, 403);

        // Open-redirect guard: the return must be exactly the label's preview origin.
        if (!ReturnIsPreviewOrigin(ret, PreviewUrlBase(config), label))
            return RazorPage<PreviewAuthErrorPage>(new { Message = "This preview link points somewhere unexpected." }, 400);

        var code = previewAuth.MintCode(label);
        var joiner = ret.Contains('?') ? '&' : '?';
        return Results.Redirect($"{ret}{joiner}landbridge_preview_code={Uri.EscapeDataString(code)}");
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
    /// Runs <paramref name="body"/> only for an authenticated human/Lead; otherwise a JSON
    /// caller gets 401 and a browser is redirected to the login page.
    ///
    /// <para><b>The resolved principal is handed to the body, and every body has to use it.</b>
    /// These views are not uniform: a human operator reads the instance (§12), while a Lead
    /// holds a credential scoped to one Team (§5) and reads only that Team (§4 reattachment,
    /// §10 as-built — no cross-Team or machine-group views for agents). Authenticating and then
    /// discarding the principal is what turned a Lead token into an instance-wide reader, so the
    /// signature no longer allows it: <see cref="TeamScope"/> for a scoped read,
    /// <see cref="OperatorMayAccess"/> for a named Team, <see cref="Refused"/> for the views
    /// with no Lead-scoped answer.</para>
    /// </summary>
    private static async Task<IResult> Gated(
        HttpContext http, TokenService tokens, CancellationToken ct, Func<Principal, Task<IResult>> body)
    {
        if (await DashboardAuth.ResolveAsync(http, tokens, ct) is not { } principal)
            return DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                : Results.Redirect("/dashboard/login");
        return await body(principal);
    }

    /// <summary>
    /// The single Team a caller's multi-Team reads are confined to, or null for the instance-wide
    /// view a human operator gets. The inverse of <see cref="OperatorMayAccess"/>, for the routes
    /// that name no Team: rather than refusing a Lead outright they answer with its own Team,
    /// which is the §4 reattachment surface it is entitled to.
    /// </summary>
    private static Guid? TeamScope(Principal principal) => principal switch
    {
        Principal.Lead l => l.Team.Value,
        Principal.Human => null,
        // Unreachable — DashboardAuth.ResolveAsync admits only the two above. Written as a
        // Team that owns nothing rather than as null, so if a third principal ever reaches
        // here it reads an empty instance instead of the whole one. The permissive answer is
        // the one this whole seam exists to stop being the default.
        _ => Guid.Empty,
    };

    private const string MachinesAreHumanOnly =
        "the machine group is a human-operator view; a Lead session sees its own Team's tasks "
        + "on /dashboard/teams and through get_team_state";

    private const string RevokingIsHumanOnly =
        "revoking a machine is a human-operator action; a machine belongs to no Team, so a Lead "
        + "session cannot un-trust one — ask your operator";

    private const string NotYourTeam =
        "this session may only read its own Team";

    /// <summary>
    /// A 403 for a caller whose credential does not reach what it asked for: the JSON twin gets
    /// the reason as data (a Lead consuming the twin should be able to tell a scope refusal from
    /// an expired token), a browser gets the page that names it.
    /// </summary>
    private static IResult Refused(HttpContext http, string reason) =>
        DashboardNegotiate.WantsJson(http)
            ? Results.Json(new { error = reason }, Json, statusCode: StatusCodes.Status403Forbidden)
            : RazorPage<ScopeRefusedPage>(new { Reason = reason }, StatusCodes.Status403Forbidden);

    /// <summary>
    /// The origin a mutating dashboard POST must have come from. The dashboard is a same-origin
    /// surface on the plane itself (§12), so its origin is the plane's public URL — the same
    /// value §5's OAuth identity derives from — and a deployment configuring none falls back to
    /// the origin the request was addressed to (see <see cref="OriginGuard.IsSameOrigin"/>).
    /// </summary>
    private static string? DashboardOrigin(IConfiguration config) =>
        config["Landbridge:PublicMcpUrl"] ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PUBLIC_MCP_URL");

    /// <summary>
    /// The refusal for a mutating POST that did not come from the dashboard's own origin, or
    /// null to proceed. Applied to every POST that changes something a session is authority for
    /// — the permission verdict (§11) and the two session writes — because the cookie is
    /// <c>SameSite=Lax</c> and Lax is a <em>site</em> control: a §8.4 preview page shares the
    /// dashboard's registrable domain by design, so a worker's own preview can otherwise POST
    /// its own pending permission request and have the operator's session allow it.
    ///
    /// <para><c>/dashboard/preview</c> is deliberately not here: its worst case is minting a
    /// preview URL the forging page cannot read back, and it is the one POST with a JSON twin an
    /// agent legitimately drives.</para>
    /// </summary>
    private static IResult? CrossOriginRefusal(HttpContext http, IConfiguration config) =>
        OriginGuard.IsSameOrigin(http.Request, DashboardOrigin(config))
            ? null
            : Refused(http, CrossOriginReason);

    private const string CrossOriginReason =
        "this form is same-origin only: the request carried no Origin from the dashboard's own host";

    /// <summary>Serve the model as JSON, or the rendered HTML, per content negotiation.</summary>
    private static IResult Negotiated<T>(HttpContext http, T model, Func<IResult> html) =>
        DashboardNegotiate.WantsJson(http) ? Results.Json(model, Json) : html();
}
