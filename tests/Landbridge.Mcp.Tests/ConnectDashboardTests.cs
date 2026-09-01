using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The dashboard Connect page: how-to HTML, JSON twin, and the two human-only
/// credential writes (enrollment token, Lead claim). Same-origin on the writes.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConnectDashboardTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Unauthenticated_json_is_401_and_html_redirects_to_login()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var app = BuildPlane();
        await app.StartAsync(cts.Token);
        using var client = Client(app);

        var html = await client.GetAsync("/dashboard/connect", cts.Token);
        Assert.Equal(HttpStatusCode.Redirect, html.StatusCode);
        Assert.Contains("/dashboard/login", html.Headers.Location!.ToString(), StringComparison.Ordinal);

        var json = await client.GetAsync("/dashboard/connect?format=json", cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, json.StatusCode);
        await app.StopAsync(cts.Token);
    }

    [SkippableFact]
    public async Task Human_html_documents_both_flows_and_json_twin_names_the_urls()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var html = await GetAuthedAsync(app, "/dashboard/connect", ct);
        Assert.Contains("Connect as a Lead", html, StringComparison.Ordinal);
        Assert.Contains("Enroll a machine", html, StringComparison.Ordinal);
        Assert.Contains("/dashboard/connect/claim", html, StringComparison.Ordinal);
        Assert.Contains("/dashboard/connect/enroll-token", html, StringComparison.Ordinal);
        Assert.Contains("landbridge://skills/lead", html, StringComparison.Ordinal);
        Assert.Contains("landbridge://skills/enroll", html, StringComparison.Ordinal);
        Assert.Contains("landbridged --enroll", html, StringComparison.Ordinal);
        Assert.Contains("Never put", html, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.landbridge]", html, StringComparison.Ordinal);
        Assert.Contains("Authorization = \"Bearer", html, StringComparison.Ordinal);
        Assert.Contains("/dashboard/connect/setup-link", html, StringComparison.Ordinal);

        var json = await GetAuthedAsync(app, "/dashboard/connect?format=json", ct);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("/dashboard/connect/enroll-token",
            doc.RootElement.GetProperty("posts").GetProperty("enrollToken").GetString());
        Assert.Equal("/dashboard/connect/setup-link",
            doc.RootElement.GetProperty("posts").GetProperty("setupLink").GetString());
        Assert.Equal("landbridge://skills/lead",
            doc.RootElement.GetProperty("leadSkill").GetString());
        Assert.Equal(15, doc.RootElement.GetProperty("enrollmentTtlMinutes").GetInt32());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_lead_can_read_the_guide_but_cannot_mint()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var lead = await IssueLeadTokenAsync(TeamId.New(), ct);
        using var client = Client(app);

        using (var req = new HttpRequestMessage(HttpMethod.Get, "/dashboard/connect?format=json"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lead);
            var res = await client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        using (var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/connect/enroll-token")
        {
            Content = JsonContent.Create(new { }),
        })
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lead);
            req.Headers.Add("Origin", new Uri(BaseUrl(app)).GetLeftPart(UriPartial.Authority));
            var res = await client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Post_without_this_origin_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/connect/enroll-token")
        {
            Content = JsonContent.Create(new { }),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={human}");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Human_issues_an_enrollment_token_once()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var res = await PostAsync(app, "/dashboard/connect/enroll-token", new { }, ct);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var token = doc.RootElement.GetProperty("token").GetString();
        Assert.StartsWith("lbr_e_", token);
        Assert.True(doc.RootElement.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);

        await using var db = pg.NewContext();
        var exchanged = await new TokenService(db, TimeProvider.System)
            .ExchangeEnrollmentAsync(token!, new MachineDeclaration("box", "test", "linux", "standard"), ct);
        Assert.NotNull(exchanged);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Human_claims_a_new_team_and_refuses_a_second_claimant_without_takeover()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var first = await PostAsync(app, "/dashboard/connect/claim", new { }, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var claimed = JsonDocument.Parse(await first.Content.ReadAsStringAsync(ct));
        var leadToken = claimed.RootElement.GetProperty("token").GetString();
        var teamId = claimed.RootElement.GetProperty("teamId").GetGuid();
        Assert.StartsWith("lbr_l_", leadToken);

        var second = await PostAsync(app, "/dashboard/connect/claim", new { teamId }, ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already led", await second.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        var takeover = await PostAsync(app, "/dashboard/connect/claim", new { teamId, takeover = true }, ct);
        Assert.Equal(HttpStatusCode.Created, takeover.StatusCode);
        using var took = JsonDocument.Parse(await takeover.Content.ReadAsStringAsync(ct));
        Assert.StartsWith("lbr_l_", took.RootElement.GetProperty("token").GetString());
        Assert.Equal(teamId, took.RootElement.GetProperty("teamId").GetGuid());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Setup_link_redeems_markdown_once_and_then_404s()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var minted = await PostAsync(app, "/dashboard/connect/setup-link", new { }, ct);
        Assert.Equal(HttpStatusCode.Created, minted.StatusCode);
        using var doc = JsonDocument.Parse(await minted.Content.ReadAsStringAsync(ct));
        var url = doc.RootElement.GetProperty("url").GetString();
        Assert.Contains("/setup/lbr_s_", url, StringComparison.Ordinal);
        Assert.DoesNotContain("lbr_l_", url, StringComparison.Ordinal);

        using var client = Client(app);
        var first = await client.GetAsync(new Uri(url!), ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("text/markdown", first.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", first.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var markdown = await first.Content.ReadAsStringAsync(ct);
        Assert.Contains("lbr_l_", markdown, StringComparison.Ordinal);
        Assert.Contains("[mcp_servers.landbridge]", markdown, StringComparison.Ordinal);
        Assert.Contains("create_team", markdown, StringComparison.Ordinal);
        Assert.Contains("Authorization = \"Bearer lbr_l_", markdown, StringComparison.Ordinal);

        var second = await client.GetAsync(new Uri(url!), ct);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.DoesNotContain("lbr_l_", await second.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        var unknown = await client.GetAsync("/setup/lbr_s_" + new string('0', 64), ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await app.StopAsync(ct);
    }

    private async Task<HttpResponseMessage> PostAsync(WebApplication app, string path, object body, CancellationToken ct)
    {
        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={human}");
        req.Headers.Add("Origin", new Uri(BaseUrl(app)).GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(req, ct);
    }

    private async Task<string> GetAuthedAsync(WebApplication app, string path, CancellationToken ct)
    {
        using var client = Client(app);
        var token = await IssueHumanTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={token}");
        var res = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }

    private static string BaseUrl(WebApplication app) =>
        app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

    private static HttpClient Client(WebApplication app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl(app)) };

    private async Task<string> IssueHumanTokenAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        return (await tokens.IssueHumanSessionAsync(ct)).Token;
    }

    private async Task<string> IssueLeadTokenAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
        return Assert.IsType<LeadClaimResult.Claimed>(claim).Token.Token;
    }

    private WebApplication BuildPlane()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddDashboard();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        builder.Services.AddSingleton<IOperatorVerifier>(new ConfiguredOperatorVerifier((string?)null));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication(LandbridgeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LandbridgeAuthenticationHandler>(
                LandbridgeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDashboard();
        return app;
    }
}
