using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Landbridge.Mcp.Dashboard.Components.Pages;
using Landbridge.Web;
using Microsoft.Extensions.Configuration;
using static Landbridge.Mcp.Dashboard.DashboardHosting;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Operator how-to plus the two credential writes that have no other surface:
/// issue an enrollment token, and claim a Lead factory (optionally assigning a
/// Team). GET is any dashboard principal (a Lead may need the recipe). The POSTs
/// are human-only — a Lead already holds a factory token, and a machine belongs
/// to no Team.
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

    public static object Guide(string mcpUrl) => new
    {
        mcpUrl,
        oauthAuthorize = $"{mcpUrl}/oauth/authorize",
        oauthToken = $"{mcpUrl}/oauth/token",
        protectedResource = $"{mcpUrl}/.well-known/oauth-protected-resource",
        authorizationServer = $"{mcpUrl}/.well-known/oauth-authorization-server",
        enroll = $"{mcpUrl}/enroll",
        enrollmentTtlMinutes = (int)TokenService.EnrollmentTtl.TotalMinutes,
        leadSkill = "landbridge://skills/lead",
        enrollSkill = "landbridge://skills/enroll",
        runnerConfigSkill = "landbridge://skills/runner-config",
        posts = new
        {
            enrollToken = "/dashboard/connect/enroll-token",
            claimLead = "/dashboard/connect/claim",
            setupLink = "/dashboard/connect/setup-link",
        },
        setupPath = "/setup/{code}",
    };

    /// <summary>
    /// POST /dashboard/connect/enroll-token — mint a single-use 15-minute
    /// enrollment token and show it once. Same-origin, human-only.
    /// </summary>
    private static async Task<IResult> HandleIssueEnrollmentAsync(
        HttpContext http, TokenService tokens, IConfiguration config, CancellationToken ct)
    {
        if (CrossOriginRefusal(http, config) is { } refusal)
            return refusal;

        return await GatedHuman(http, tokens, ct, async _ =>
        {
            var issued = await tokens.IssueEnrollmentTokenAsync(ct);
            var controlUrl = ResolveMcpUrl(config, http);
            var model = new
            {
                token = issued.Token,
                expiresAt = issued.ExpiresAt,
                controlUrl,
            };
            return DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
                ? Results.Json(model, Json, statusCode: StatusCodes.Status201Created)
                : RazorPage<EnrollmentTokenIssuedPage>(new
                {
                    Token = issued.Token,
                    ExpiresAt = issued.ExpiresAt ?? default,
                    ControlUrl = controlUrl,
                });
        });
    }

    /// <summary>
    /// POST /dashboard/connect/claim — claim a Lead factory from this human
    /// session and assign a Team (empty <c>teamId</c> starts a new one). The
    /// factory token is shown once. Same-origin, human-only.
    /// </summary>
    private static async Task<IResult> HandleClaimLeadAsync(
        HttpContext http, TokenService tokens, IConfiguration config, CancellationToken ct)
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
            else if (!Guid.TryParse(teamIdText, out var parsed))
                return Results.Json(new { error = "invalid team id" }, Json,
                    statusCode: StatusCodes.Status400BadRequest);
            else
                team = new TeamId(parsed);

            var result = await tokens.ClaimLeadAsync(humanToken, team, takeover, ct);
            var mcpUrl = ResolveMcpUrl(config, http);
            return result switch
            {
                LeadClaimResult.Claimed claimed =>
                    DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
                        ? Results.Json(new
                        {
                            token = claimed.Token.Token,
                            teamId = claimed.Team.Value,
                            mcpUrl,
                        }, Json, statusCode: StatusCodes.Status201Created)
                        : RazorPage<LeadClaimedPage>(new
                        {
                            Token = claimed.Token.Token,
                            TeamId = claimed.Team.Value,
                            McpUrl = mcpUrl,
                        }),
                LeadClaimResult.Refused refused =>
                    RefusedClaim(http,
                        $"team {team.Value:D} is already led by human {ShortId(refused.HeldByHuman)} since {refused.HeldSince:u}; check takeover to evict them"),
                LeadClaimResult.NoHumanSession =>
                    RefusedClaim(http,
                        "claiming a Lead requires a human session (the operator passphrase door), not a pasted Lead token"),
                _ => RefusedClaim(http, "lead claim failed"),
            };
        });
    }

    /// <summary>
    /// POST /dashboard/connect/setup-link — claim a Lead and mint a one-time
    /// URL whose first GET is markdown that contains the bearer. Same-origin,
    /// human-only. The URL is a capability, not the token.
    /// </summary>
    private static async Task<IResult> HandleMintSetupLinkAsync(
        HttpContext http, TokenService tokens, LeadSetupLinkStore links,
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
            else if (!Guid.TryParse(teamIdText, out var parsed))
                return Results.Json(new { error = "invalid team id" }, Json,
                    statusCode: StatusCodes.Status400BadRequest);
            else
                team = new TeamId(parsed);

            var result = await tokens.ClaimLeadAsync(humanToken, team, takeover, ct);
            if (result is not LeadClaimResult.Claimed claimed)
            {
                return result switch
                {
                    LeadClaimResult.Refused refused =>
                        RefusedClaim(http,
                            $"team {team.Value:D} is already led by human {ShortId(refused.HeldByHuman)} since {refused.HeldSince:u}; check takeover to evict them"),
                    LeadClaimResult.NoHumanSession =>
                        RefusedClaim(http,
                            "claiming a Lead requires a human session (the operator passphrase door), not a pasted Lead token"),
                    _ => RefusedClaim(http, "lead claim failed"),
                };
            }

            var mcpUrl = ResolveMcpUrl(config, http);
            var issued = links.Mint(claimed.Token.Token, claimed.Team.Value, mcpUrl);
            // The redeem URL is this request's origin so a test host (or any
            // off-port bind) is reachable. The markdown still names mcpUrl,
            // which is what the harness should POST.
            var url = $"{http.Request.Scheme}://{http.Request.Host}/setup/{issued.Code}";
            var expiresAt = issued.ExpiresAt;
            return DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
                ? Results.Json(new { url, expiresAt, teamId = claimed.Team.Value }, Json,
                    statusCode: StatusCodes.Status201Created)
                : RazorPage<SetupLinkIssuedPage>(new
                {
                    Url = url,
                    ExpiresAt = expiresAt,
                    TeamId = claimed.Team.Value,
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

    private static IResult RefusedClaim(HttpContext http, string reason) =>
        DashboardNegotiate.WantsJson(http) || http.Request.HasJsonContentType()
            ? Results.Json(new { error = reason }, Json, statusCode: StatusCodes.Status409Conflict)
            : RazorPage<LeadClaimRefusedPage>(new { Reason = reason }, StatusCodes.Status409Conflict);

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
        "issuing an enrollment token, claiming a Lead, or minting a setup link is a "
        + "human-operator action; a Lead factory already has a token — call create_team for another Team";

    private const string CrossOriginReason =
        "this form is same-origin only: the request carried no Origin from the dashboard's own host";
}

internal sealed record ClaimLeadBody(string? TeamId, bool? Takeover);
