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
/// issue an enrollment token, and claim a Lead on a Team. GET is any dashboard
/// principal (a Lead may need the recipe). The POSTs are human-only — a Lead
/// already holds one Team, and a machine belongs to no Team.
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
        },
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
    /// POST /dashboard/connect/claim — claim (or take over) the Lead of a Team
    /// from this human session. Empty <c>teamId</c> starts a new Team. The Lead
    /// token is shown once. Same-origin, human-only.
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
        "issuing an enrollment token or claiming a Lead is a human-operator action; "
        + "a Lead session already holds one Team";

    private const string CrossOriginReason =
        "this form is same-origin only: the request carried no Origin from the dashboard's own host";
}

internal sealed record ClaimLeadBody(string? TeamId, bool? Takeover);
