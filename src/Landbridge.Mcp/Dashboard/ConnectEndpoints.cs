using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Dashboard.Components.Pages;
using Landbridge.Mcp.Skills;
using Landbridge.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Landbridge.Mcp.Dashboard.DashboardHosting;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Operator how-to plus the two credential writes that have no other surface:
/// issue an enrollment token, and issue a Lead factory token (optionally assigning
/// a first Team). GET is any dashboard principal (a Lead may need the recipe). The
/// POSTs are human-only — a Lead already holds a factory token, and a machine
/// belongs to no Team.
/// </summary>
internal static class ConnectEndpoints
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = DashboardNegotiate.Json;

    public static IEndpointRouteBuilder MapConnect(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dashboard/connect/enroll-token", HandleIssueEnrollmentAsync)
            .DisableAntiforgery().WithOrder(-100);
        app.MapPost("/dashboard/connect/claim", HandleClaimLeadAsync)
            .DisableAntiforgery().WithOrder(-100);
        app.MapPost("/dashboard/connect/setup-link", HandleMintSetupLinkAsync)
            .DisableAntiforgery().WithOrder(-100);
        // Unauthenticated on purpose: the capability in the path is the secret.
        // First GET burns it. Outside /dashboard so the session cookie is not
        // required and does not ride a paste to an agent.
        app.MapGet("/setup/{code}", HandleRedeemSetupLink);
        return app;
    }

    public static string ResolveMcpUrl(IConfiguration config, HttpContext? http)
    {
        var configured = config["Landbridge:PublicMcpUrl"]
            ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PUBLIC_MCP_URL");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');
        if (http is not null)
            return $"{http.Request.Scheme}://{http.Request.Host}";
        return DispatchService.DefaultPublicMcpUrl;
    }

    /// <summary>
    /// Where <c>landbridged --enroll --auth-url</c> exchanges a token.
    /// The authorization server when this host was told one, otherwise the
    /// same origin as <see cref="ResolveMcpUrl"/> (a single-host deployment).
    /// </summary>
    public static string ResolveAuthUrl(IConfiguration config, HttpContext? http)
    {
        var oauth = http?.RequestServices.GetService<OAuthServerConfig>();
        if (oauth is not null)
            return oauth.Issuer;
        var configured = config["Landbridge:AuthUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return OAuthServerConfig.Normalize(configured);
        return ResolveMcpUrl(config, http);
    }

    public static object Guide(string mcpUrl, HttpContext? http = null, OAuthServerConfig? oauth = null)
    {
        var plane = http is not null
            ? $"{http.Request.Scheme}://{http.Request.Host}".TrimEnd('/')
            : mcpUrl;
        var issuer = oauth?.Issuer ?? plane;
        return new
        {
            mcpUrl,
            oauthAuthorize = $"{issuer}/oauth/authorize",
            oauthToken = $"{issuer}/oauth/token",
            protectedResource = oauth?.ResourceMetadataUri ?? $"{mcpUrl}/.well-known/oauth-protected-resource",
            authorizationServer = oauth?.AuthorizationServerMetadataUri
                ?? $"{issuer}/.well-known/oauth-authorization-server",
            authUrl = issuer,
            enroll = $"{issuer}/enroll",
            enrollmentTtlMinutes = (int)TokenService.EnrollmentTtl.TotalMinutes,
            leadSkill = "landbridge://skills/lead",
            enrollSkill = "landbridge://skills/enroll",
            runnerConfigSkill = "landbridge://skills/runner-config",
            leadTokenIsFactory = true,
            createTeam = "create_team",
            teamIdRequiredExcept = new[] { "create_team", "list_profiles" },
            doNotWriteTeamId = true,
            posts = new
            {
                enrollToken = "/dashboard/connect/enroll-token",
                claimLead = "/dashboard/connect/claim",
                setupLink = "/dashboard/connect/setup-link",
            },
            setupPath = "/setup/{code}",
        };
    }

    /// <summary>
    /// POST /dashboard/connect/enroll-token — mint a single-use 15-minute
    /// enrollment token and show a prompt to paste into a harness on the
    /// machine. Same-origin, human-only. The prompt is the one-time guide:
    /// the token rides in it, and so does the enrollment skill, because the
    /// box is not an MCP client yet.
    /// </summary>
    private static async Task<IResult> HandleIssueEnrollmentAsync(
        HttpContext http, TokenService tokens, IConfiguration config, CancellationToken ct)
    {
        if (CrossOriginRefusal(http, config) is { } refusal)
            return refusal;

        return await GatedHuman(http, tokens, ct, async _ =>
        {
            var issued = await tokens.IssueEnrollmentTokenAsync(ct);
            var authUrl = ResolveAuthUrl(config, http);
            var prompt = RenderEnrollmentPrompt(issued.Token, issued.ExpiresAt, authUrl);
            http.Response.Headers.CacheControl = "no-store";
            return DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
                ? Results.Json(new
                {
                    token = issued.Token,
                    expiresAt = issued.ExpiresAt,
                    authUrl,
                    prompt,
                }, Json, statusCode: StatusCodes.Status201Created)
                : RazorPage<EnrollmentTokenIssuedPage>(new
                {
                    Prompt = prompt,
                    ExpiresAt = issued.ExpiresAt ?? default,
                });
        });
    }

    /// <summary>
    /// The paste-into-a-harness guide for one enrollment token. Same shape as
    /// <see cref="RenderSetupMarkdown"/>: the secret is in the text, the skill
    /// is the procedure. The skill and the runner-config reference are inlined
    /// because a machine that is not enrolled cannot read
    /// <c>landbridge://skills/enroll</c>.
    /// </summary>
    internal static string RenderEnrollmentPrompt(string token, DateTimeOffset? expiresAt, string authUrl)
    {
        var expires = expiresAt?.ToString("u") ?? "shortly";
        return $"""
             # Enroll this machine into Landbridge

             This prompt is a one-time delivery of an enrollment token, the same kind of guide a Lead setup link returns. Paste it into a harness on the machine you are enrolling. Do not commit it.

             You are on that machine. A human is at this terminal. Follow the enrollment skill included below (`landbridge-enroll`, `landbridge://skills/enroll`). There is no `/landbridge-enroll` command. This box is not a Landbridge MCP client yet, so the skill is copied here — do not look for a slash command or an MCP resource you cannot read. A machine credential is not an MCP client either. This prompt is the copy.

             Steps that need sudo are the human's. Prepare the unit or plist, say what it does, and have them run the install.

             ## Token

             Single-use. Expires {expires}. Fifteen minutes.

             Never put the token on argv. It would land in shell history and `/proc/<pid>/cmdline`. Write it to a `0600` file, exchange it, then delete the file. Do not echo the token back.

             `--auth-url` is the authorization server, where `POST /enroll` lives: `{authUrl}`.

             `--name` is the Machine Group label (the short hostname if you omit it), not a runner profile. After you probe, name profiles `<harness>-<hostname>-<os>`.

             Ask which state dir to use before you exchange. Omit `--state-dir` to use the default: `$LANDBRIDGE_STATE_DIR`, else `$XDG_STATE_HOME/landbridge`, else `~/.landbridge`. Pass `--state-dir /var/lib/landbridged` only when the human is installing a system service and will start the daemon with that same path. The path you enroll with is the path you start with.

             Exchange the token first. Then follow the skill. The runner-config reference the skill cites is the last section of this prompt — use it when you write the config, not before the exchange.

             ```bash
             umask 077
             cat > ./enroll.token <<'EOF'
             {token}
             EOF
             chmod 600 ./enroll.token
             landbridged --enroll \
               --auth-url {authUrl} \
               --enroll-token-file ./enroll.token \
               --name "$(hostname -s)"
             rm -f ./enroll.token
             ```

             That writes `credentials.json` (`0600` in a `0700` directory). The exchange answers with the plane's control URL, so the daemon does not need that URL on the command line. If `landbridged` is not installed, install it before the exchange. The binary is what runs `--enroll`.

             ## Enrollment skill

             {SkillBundle.Enroll.TrimEnd()}

             ## Runner config reference

             The enrollment skill calls this `references/runner-config.md`. It is not a file on this machine. Use this copy when you write the config.

             {SkillBundle.RunnerConfig.TrimEnd()}
             """;
    }

    /// <summary>
    /// POST /dashboard/connect/claim — issue a Lead factory token from this human
    /// session and assign a Team (empty <c>teamId</c> starts a new one). The
    /// factory token is shown once. Same-origin, human-only.
    /// </summary>
    private static async Task<IResult> HandleClaimLeadAsync(
        HttpContext http, TokenService tokens, FriendlyIds ids, IConfiguration config, CancellationToken ct)
    {
        if (CrossOriginRefusal(http, config) is { } refusal)
            return refusal;

        return await GatedHuman(http, tokens, ct, async _ =>
        {
            var humanToken = DashboardAuth.ReadToken(http);
            if (string.IsNullOrWhiteSpace(humanToken))
                return Results.Redirect("/dashboard/login");

            var (teamIdText, takeover) = await ReadClaimAsync(http, ct);
            TeamId team;
            if (string.IsNullOrWhiteSpace(teamIdText))
                team = TeamId.New();
            else
            {
                var resolved = await ids.TryTeamAsync(teamIdText, ct);
                if (resolved is null && Guid.TryParse(teamIdText, out var parsed))
                    resolved = new TeamId(parsed);
                if (resolved is null)
                    return InvalidTeam(http);
                team = resolved.Value;
            }

            var result = await tokens.ClaimLeadAsync(humanToken, team, takeover, ct);
            var mcpUrl = ResolveMcpUrl(config, http);
            if (result is LeadClaimResult.Claimed claimed)
                return await WriteClaimedAsync(http, tokens, claimed, mcpUrl, ct);
            return result switch
            {
                LeadClaimResult.Refused refused =>
                    RefusedClaim(http,
                        $"team {team.Value:D} is already led by human {ShortId(refused.HeldByHuman)} since {refused.HeldSince:u}; check takeover to move this Team to a new token",
                        takeoverHint: true),
                LeadClaimResult.NoHumanSession =>
                    RefusedClaim(http,
                        "issuing a Lead token requires a human session (the operator passphrase door), not a pasted Lead token"),
                _ => RefusedClaim(http, "issuing a Lead token failed"),
            };
        });
    }

    /// <summary>
    /// POST /dashboard/connect/setup-link — issue a Lead token and mint a one-time
    /// URL whose first GET is markdown that contains the bearer. Same-origin,
    /// human-only. The URL is a capability, not the token.
    /// </summary>
    private static async Task<IResult> HandleMintSetupLinkAsync(
        HttpContext http, TokenService tokens, LeadSetupLinkStore links, FriendlyIds ids,
        IConfiguration config, CancellationToken ct)
    {
        if (CrossOriginRefusal(http, config) is { } refusal)
            return refusal;

        return await GatedHuman(http, tokens, ct, async _ =>
        {
            var humanToken = DashboardAuth.ReadToken(http);
            if (string.IsNullOrWhiteSpace(humanToken))
                return Results.Redirect("/dashboard/login");

            var (teamIdText, takeover) = await ReadClaimAsync(http, ct);
            TeamId team;
            if (string.IsNullOrWhiteSpace(teamIdText))
                team = TeamId.New();
            else
            {
                var resolved = await ids.TryTeamAsync(teamIdText, ct);
                if (resolved is null && Guid.TryParse(teamIdText, out var parsed))
                    resolved = new TeamId(parsed);
                if (resolved is null)
                    return InvalidTeam(http);
                team = resolved.Value;
            }

            var result = await tokens.ClaimLeadAsync(humanToken, team, takeover, ct);
            if (result is not LeadClaimResult.Claimed claimed)
            {
                return result switch
                {
                    LeadClaimResult.Refused refused =>
                        RefusedClaim(http,
                            $"team {team.Value:D} is already led by human {ShortId(refused.HeldByHuman)} since {refused.HeldSince:u}; check takeover to move this Team to a new token",
                            takeoverHint: true),
                    LeadClaimResult.NoHumanSession =>
                        RefusedClaim(http,
                            "issuing a Lead token requires a human session (the operator passphrase door), not a pasted Lead token"),
                    _ => RefusedClaim(http, "issuing a Lead token failed"),
                };
            }

            var mcpUrl = ResolveMcpUrl(config, http);
            var issued = links.Mint(claimed.Token.Token, claimed.Team.Value, mcpUrl);
            // The redeem URL is this request's origin so a test host (or any
            // off-port bind) is reachable. The markdown still names mcpUrl,
            // which is what the harness should POST.
            var url = $"{http.Request.Scheme}://{http.Request.Host}/setup/{issued.Code}";
            var expiresAt = issued.ExpiresAt;
            var teamSlug = await tokens.FindTeamSlugAsync(claimed.Team.Value, ct);
            return DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
                ? Results.Json(new { url, expiresAt, teamId = teamSlug ?? claimed.Team.Value.ToString("D") }, Json,
                    statusCode: StatusCodes.Status201Created)
                : RazorPage<SetupLinkIssuedPage>(new
                {
                    Url = url,
                    ExpiresAt = expiresAt,
                    TeamId = claimed.Team.Value,
                    TeamSlug = teamSlug,
                });
        });
    }

    /// <summary>
    /// GET /setup/{code} — redeem once. The bearer is in the markdown body, never
    /// in this URL. Unknown / used / expired are one generic 404.
    /// </summary>
    private static IResult HandleRedeemSetupLink(string code, HttpContext http, LeadSetupLinkStore links)
    {
        http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        var instructions = links.Redeem(code);
        if (instructions is null)
            return Results.Text("", "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);

        return Results.Text(
            RenderSetupMarkdown(instructions),
            "text/markdown; charset=utf-8");
    }

    internal static string RenderSetupMarkdown(LeadSetupLinkStore.Instructions i) =>
        $"""
         # Connect to this Landbridge plane as a Lead

         This page is a one-time delivery of a Lead bearer. Reloading it 404s.

         MCP is `POST {i.McpUrl}` (the origin, not `/mcp`). This token is a Lead
         factory — it can create Teams. It is not a human session.

         First Team: `{i.TeamId:D}`. Pass that as `teamId` on Lead tools, or call
         `create_team` for a new Team (keep the id in this conversation; do not
         write it into the project — parallel agents sharing this token stay
         apart by not knowing each other's Team id). There is no list of Teams.

         ## Grok

         Add to `~/.grok/config.toml`:

         ```toml
         [mcp_servers.landbridge]
         url = "{i.McpUrl}"
         enabled = true

         [mcp_servers.landbridge.headers]
         Authorization = "Bearer {i.LeadToken}"
         ```

         Then refresh `/mcps` or restart Grok. `grok mcp doctor landbridge` should
         handshake.

         ## Any other MCP client

         Send `Authorization: Bearer {i.LeadToken}` on every request to `{i.McpUrl}`.

         Read `landbridge://skills/lead`. If you were not given a team id, call
         `create_team`. Call `list_profiles`, then `create_session` with an exact
         profile name that came back and that team id. There is no reserved `default`.
         """;

    private static async Task<IResult> WriteClaimedAsync(
        HttpContext http, TokenService tokens, LeadClaimResult.Claimed claimed, string mcpUrl,
        CancellationToken ct)
    {
        var teamSlug = await tokens.FindTeamSlugAsync(claimed.Team.Value, ct);
        return DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
            ? Results.Json(new
            {
                token = claimed.Token.Token,
                teamId = teamSlug ?? claimed.Team.Value.ToString("D"),
                mcpUrl,
            }, Json, statusCode: StatusCodes.Status201Created)
            : RazorPage<LeadClaimedPage>(new
            {
                Token = claimed.Token.Token,
                TeamId = claimed.Team.Value,
                TeamSlug = teamSlug,
                McpUrl = mcpUrl,
            });
    }

    private static async Task<(string TeamId, bool Takeover)> ReadClaimAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.HasJsonContentType())
        {
            var body = await http.Request.ReadFromJsonAsync<ClaimLeadBody>(Json, ct);
            return (body?.TeamId ?? "", body?.Takeover == true);
        }

        var form = await http.Request.ReadFormAsync(ct);
        return (form["teamId"].ToString(),
            string.Equals(form["takeover"].ToString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static IResult InvalidTeam(HttpContext http)
    {
        const string reason = "that team id is not a team on this plane";
        return DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
            ? Results.Json(new { error = reason }, Json, statusCode: StatusCodes.Status400BadRequest)
            : RazorPage<LeadClaimRefusedPage>(new { Reason = reason }, StatusCodes.Status400BadRequest);
    }

    private static IResult RefusedClaim(HttpContext http, string reason, bool takeoverHint = false) =>
        DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
            ? Results.Json(new { error = reason }, Json, statusCode: StatusCodes.Status409Conflict)
            : RazorPage<LeadClaimRefusedPage>(
                new { Reason = reason, ShowTakeoverHint = takeoverHint },
                StatusCodes.Status409Conflict);

    private static async Task<IResult> GatedHuman(
        HttpContext http, TokenService tokens, CancellationToken ct,
        Func<Principal.Human, Task<IResult>> body)
    {
        if (await DashboardAuth.ResolveAsync(http, tokens, ct) is not { } principal)
            return DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = "unauthorized" }, Json, statusCode: 401)
                : Results.Redirect("/dashboard/login");
        if (principal is not Principal.Human human)
            return DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = HumanOnly }, Json, statusCode: StatusCodes.Status403Forbidden)
                : RazorPage<ScopeRefusedPage>(new { Reason = HumanOnly }, StatusCodes.Status403Forbidden);
        return await body(human);
    }

    private static IResult? CrossOriginRefusal(HttpContext http, IConfiguration config)
    {
        var origin = config["Landbridge:PublicMcpUrl"]
            ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PUBLIC_MCP_URL");
        return OriginGuard.IsSameOrigin(http.Request, origin)
            ? null
            : DashboardNegotiate.WantsJson(http)
                ? Results.Json(new { error = CrossOriginReason }, Json, statusCode: StatusCodes.Status403Forbidden)
                : RazorPage<ScopeRefusedPage>(new { Reason = CrossOriginReason }, StatusCodes.Status403Forbidden);
    }

    private static string ShortId(Guid value)
    {
        var s = value.ToString();
        var dash = s.IndexOf('-');
        return dash > 0 ? s[..dash] : s;
    }

    private const string HumanOnly =
        "issuing an enrollment token, issuing a Lead token, or minting a setup link is a "
        + "human-operator action; a Lead factory already has a token — call create_team for another Team";

    private const string CrossOriginReason =
        "this form is same-origin only: the request carried no Origin from the dashboard's own host";
}

internal sealed record ClaimLeadBody(string? TeamId, bool? Takeover);
