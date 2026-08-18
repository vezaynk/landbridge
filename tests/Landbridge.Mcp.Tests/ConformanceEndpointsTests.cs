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
/// Operator dummy-session mint + progress for a named profile
/// (<c>POST /dashboard/conformance</c>, <c>GET /dashboard/conformance/{runId}</c>).
/// Human-only, same-origin on the write, and the plane does not judge the answers.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConformanceEndpointsTests(PostgresFixture pg) : IAsyncLifetime
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

        var html = await client.GetAsync("/dashboard/conformance", cts.Token);
        Assert.Equal(HttpStatusCode.Redirect, html.StatusCode);
        Assert.Contains("/dashboard/login", html.Headers.Location!.ToString(), StringComparison.Ordinal);

        var json = await client.GetAsync("/dashboard/conformance?format=json", cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, json.StatusCode);
        await app.StopAsync(cts.Token);
    }

    [SkippableFact]
    public async Task A_lead_token_is_refused_the_profile_check()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var lead = await IssueLeadTokenAsync(TeamId.New(), ct);
        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/dashboard/conformance?format=json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lead);
        var res = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
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
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/conformance")
        {
            Content = JsonContent.Create(new { profile = "box_macos" }),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={human}");
        req.Headers.Add("Origin", "https://evil.example");
        var res = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Post_mints_the_three_dummy_tasks_against_default_and_get_reports_their_states()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var created = await PostConformanceAsync(app, new { }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct));
        var root = createdDoc.RootElement;
        Assert.Equal(MachineSnapshot.DefaultProfile, root.GetProperty("profile").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("pending").GetInt32());
        Assert.False(root.GetProperty("workerDone").GetBoolean());
        Assert.Empty(root.GetProperty("machinesDeclaring").EnumerateArray());

        var kinds = root.GetProperty("sessions").EnumerateArray()
            .Select(t => t.GetProperty("kind").GetString()!)
            .ToArray();
        Assert.Equal(new[] { "identity", "write", "shell" }, kinds);

        var runId = root.GetProperty("runId").GetGuid();
        var progressUrl = root.GetProperty("progressUrl").GetString();
        Assert.Equal($"/dashboard/conformance/{runId:D}", progressUrl);

        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);
        using var get = new HttpRequestMessage(HttpMethod.Get, $"{progressUrl}?format=json");
        get.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={human}");
        var progress = await client.SendAsync(get, ct);
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        using var progressDoc = JsonDocument.Parse(await progress.Content.ReadAsStringAsync(ct));
        Assert.Equal(runId, progressDoc.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal(3, progressDoc.RootElement.GetProperty("pending").GetInt32());
        Assert.Equal("Submitted", progressDoc.RootElement.GetProperty("sessions")[0].GetProperty("state").GetString());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Post_mints_the_dummy_set_against_the_named_profile()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var created = await PostConformanceAsync(app, new { profile = "goose" }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct));
        Assert.Equal("goose", createdDoc.RootElement.GetProperty("profile").GetString());
        Assert.Equal(3, createdDoc.RootElement.GetProperty("total").GetInt32());

        var runId = createdDoc.RootElement.GetProperty("runId").GetGuid();
        await using var db = pg.NewContext();
        var profiles = await db.Sessions.AsNoTracking()
            .Where(s => s.TeamId == runId)
            .Select(s => s.Profile)
            .ToListAsync(ct);
        Assert.Equal(3, profiles.Count);
        Assert.All(profiles, p => Assert.Equal("goose", p));

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Post_form_field_names_the_profile()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/conformance")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["profile"] = "claude",
            }),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={human}");
        req.Headers.Add("Origin", new Uri(BaseUrl(app)).GetLeftPart(UriPartial.Authority));
        var res = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var location = res.Headers.Location!.ToString();
        Assert.StartsWith("/dashboard/conformance/", location, StringComparison.Ordinal);

        var runId = Guid.Parse(location.Split('/')[^1]);
        await using var db = pg.NewContext();
        Assert.Equal("claude", await db.Sessions.AsNoTracking()
            .Where(s => s.TeamId == runId)
            .Select(s => s.Profile)
            .FirstAsync(ct));

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Get_unknown_run_is_404()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/dashboard/conformance/{Guid.NewGuid():D}?format=json");
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={human}");
        var res = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        await app.StopAsync(ct);
    }

    private async Task<HttpResponseMessage> PostConformanceAsync(WebApplication app, object body, CancellationToken ct)
    {
        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/conformance")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={human}");
        req.Headers.Add("Origin", new Uri(BaseUrl(app)).GetLeftPart(UriPartial.Authority));
        return await client.SendAsync(req, ct);
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
        builder.Services.AddScoped<DashboardQueries>();
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
