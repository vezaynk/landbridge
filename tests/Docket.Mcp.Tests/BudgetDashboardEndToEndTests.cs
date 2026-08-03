using System.Net;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
using Docket.Mcp.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Docket.Mcp.Tests;

/// <summary>
/// The §9.9 budget surface over real HTTP: the read that a Lead is allowed and the write that
/// only a human is.
///
/// <para>The asymmetry is the point being pinned. Every other Team-scoped dashboard action lets
/// a Lead act within its own Team; this one refuses it, because a budget is the control that
/// bounds the Lead itself — a Lead able to raise its own ceiling is enforcement living exactly
/// where a model can reason past it (§2 principle 3). There is deliberately no MCP tool for the
/// write, so this endpoint is the whole write path and these tests are the whole of its
/// enforcement.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BudgetDashboardEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly MachineSnapshot Snapshot =
        new("box-1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── The write: human only ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_human_sets_the_ceiling_and_the_team_view_reports_it()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        await SeedTaskAsync(team, ct);
        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);

        var post = await PostAsync(client, "/dashboard/budget", human, ct,
            ("teamId", team.Value.ToString()), ("ceilingUsd", "50"), ("perTaskUsd", "5"));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("Budget updated", await post.Content.ReadAsStringAsync(ct));

        // The service actually holds it, and the JSON twin carries the same numbers the HTML shows.
        await using var db = pg.NewContext();
        var stored = await new TeamBudgetService(db, TimeProvider.System).ReadAsync(team, ct);
        Assert.Equal(50m, stored.CeilingUsd);
        Assert.Equal(5m, stored.PerTaskUsd);

        var json = await GetAsync(client, $"/dashboard/teams/{team.Value}?format=json", human, ct);
        var body = await json.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"ceilingUsd\":50", body);
        Assert.Contains("\"perTaskUsd\":5", body);
        Assert.Contains("\"remaining\":50", body);
        Assert.Contains("\"exhausted\":false", body);
    }

    [SkippableFact]
    public async Task A_lead_token_is_refused_the_write_while_the_same_token_still_reads_the_budget()
    {
        // Both halves, because a regression that let a Lead write would look like a pass if
        // only the refusal were asserted — and one that blinded a Lead to its own ceiling would
        // look like a pass if only the write were. §12 requires the Lead-consumable twin; §9.9
        // requires the write to be human.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        await SeedTaskAsync(team, ct);
        await SetLimitsAsync(team, 40m, 4m, ct);
        var lead = await IssueLeadTokenAsync(team, ct);
        using var client = Client(app);

        var post = await PostAsync(client, "/dashboard/budget", lead, ct,
            ("teamId", team.Value.ToString()), ("ceilingUsd", "999999"), ("perTaskUsd", "5"));
        var read = await GetAsync(client, $"/dashboard/teams/{team.Value}?format=json", lead, ct);

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Contains("human-only", await post.Content.ReadAsStringAsync(ct));
        // The ceiling did not move.
        await using var db = pg.NewContext();
        Assert.Equal(40m, (await new TeamBudgetService(db, TimeProvider.System).ReadAsync(team, ct)).CeilingUsd);
        // Same credential, reading the very same budget: fine.
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Contains("\"ceilingUsd\":40", await read.Content.ReadAsStringAsync(ct));
    }

    [SkippableFact]
    public async Task A_lead_is_not_even_offered_the_form_a_human_is()
    {
        // The affordance follows the rule. A Lead that is shown a control it will be refused
        // for using learns the rule by failing; better to say so on the page.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        await SeedTaskAsync(team, ct);
        var human = await IssueHumanTokenAsync(ct);
        var lead = await IssueLeadTokenAsync(team, ct);
        using var client = Client(app);

        var humanHtml = await (await GetAsync(client, $"/dashboard/teams/{team.Value}", human, ct))
            .Content.ReadAsStringAsync(ct);
        var leadHtml = await (await GetAsync(client, $"/dashboard/teams/{team.Value}", lead, ct))
            .Content.ReadAsStringAsync(ct);

        Assert.Contains("action=\"/dashboard/budget\"", humanHtml);
        Assert.DoesNotContain("action=\"/dashboard/budget\"", leadHtml);
        Assert.Contains("Read-only for this session", leadHtml);
    }

    [SkippableFact]
    public async Task An_unauthenticated_write_is_sent_to_the_login_page()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        using var client = Client(app);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/budget")
        {
            Content = new FormUrlEncodedContent([new("teamId", Guid.NewGuid().ToString())]),
        };
        var res = await client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/dashboard/login", res.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // ── What the form accepts ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_blank_field_clears_that_limit_and_a_non_number_is_refused()
    {
        // Blank means "no limit", not zero — zero is not a coherent ceiling. But a typo must not
        // be read as blank, or a fat-fingered digit would silently unbound a Team.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        await SetLimitsAsync(team, 30m, 3m, ct);
        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);

        var garbage = await PostAsync(client, "/dashboard/budget", human, ct,
            ("teamId", team.Value.ToString()), ("ceilingUsd", "lots"), ("perTaskUsd", "3"));
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        await using (var db = pg.NewContext())
            Assert.Equal(30m, (await new TeamBudgetService(db, TimeProvider.System).ReadAsync(team, ct)).CeilingUsd);

        var zero = await PostAsync(client, "/dashboard/budget", human, ct,
            ("teamId", team.Value.ToString()), ("ceilingUsd", "0"), ("perTaskUsd", "3"));
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);

        var cleared = await PostAsync(client, "/dashboard/budget", human, ct,
            ("teamId", team.Value.ToString()), ("ceilingUsd", ""), ("perTaskUsd", ""));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        await using (var db = pg.NewContext())
        {
            var view = await new TeamBudgetService(db, TimeProvider.System).ReadAsync(team, ct);
            Assert.Null(view.CeilingUsd);
            Assert.Null(view.Remaining);   // unbounded, not zero
        }
    }

    [SkippableFact]
    public async Task Raising_a_ceiling_never_resets_what_was_already_committed()
    {
        // The escape valve a human reaches for on an exhausted Team. It must give room without
        // erasing the record of prior authorization — otherwise the ceiling is a lie a human can
        // launder by re-saving the same number.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        await SetLimitsAsync(team, 5m, 5m, ct);
        await DispatchOneAsync(team, ct);   // commits 5 of 5: exhausted
        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);

        var before = await GetAsync(client, $"/dashboard/teams/{team.Value}?format=json", human, ct);
        Assert.Contains("\"exhausted\":true", await before.Content.ReadAsStringAsync(ct));

        var raised = await PostAsync(client, "/dashboard/budget", human, ct,
            ("teamId", team.Value.ToString()), ("ceilingUsd", "25"), ("perTaskUsd", "5"));
        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);

        var after = await GetAsync(client, $"/dashboard/teams/{team.Value}?format=json", human, ct);
        var body = await after.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"committedUsd\":5", body);   // history intact
        Assert.Contains("\"remaining\":20", body);
        Assert.Contains("\"exhausted\":false", body);
    }

    [SkippableFact]
    public async Task A_team_that_exists_only_as_a_budget_still_appears_in_the_teams_list()
    {
        // Setting a ceiling before the Lead is provisioned is the intended order, so a Team with
        // a budget and no tasks must be visible rather than appearing once it has already spent.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);

        await PostAsync(client, "/dashboard/budget", human, ct,
            ("teamId", team.Value.ToString()), ("ceilingUsd", "12"), ("perTaskUsd", "2"));

        var teams = await GetAsync(client, "/dashboard/teams?format=json", human, ct);
        Assert.Contains(team.Value.ToString(), await teams.Content.ReadAsStringAsync(ct));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private WebApplication BuildPlane()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddDocketStore();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<DashboardQueries>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDocketForwarding();
        builder.Services.AddSingleton<IOperatorVerifier>(new ConfiguredOperatorVerifier((string?)null));
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(DocketAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DocketAuthenticationHandler>(
                DocketAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDashboard();
        return app;
    }

    private static HttpClient Client(WebApplication app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))),
        };

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string path, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={token}");
        return await client.SendAsync(req, ct);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, string token, CancellationToken ct,
        params (string Key, string Value)[] fields)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(
                fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={token}");
        return await client.SendAsync(req, ct);
    }

    private async Task<string> IssueHumanTokenAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await new TokenService(db, TimeProvider.System).IssueHumanSessionAsync(ct)).Token;
    }

    private async Task<string> IssueLeadTokenAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
        return ((LeadClaimResult.Claimed)claim).Token.Token;
    }

    private async Task SetLimitsAsync(TeamId team, decimal? ceiling, decimal? perTask, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        await new TeamBudgetService(db, TimeProvider.System).SetLimitsAsync(team, ceiling, perTask, ct);
    }

    private async Task SeedTaskAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        await new TaskStore(db, TimeProvider.System).CreateAsync(new CreateTask(
            new LeadClaim(team), team, "criteria", CompletionMode.Lead, Profile: null,
            TeamBudgetRemains: true), ct);
    }

    private async Task DispatchOneAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var store = new TaskStore(db, TimeProvider.System, budgets);
        await store.CreateAsync(new CreateTask(
            new LeadClaim(team), team, "criteria", CompletionMode.Lead, Profile: null,
            TeamBudgetRemains: true), ct);
        await store.DispatchNextAsync(Snapshot, WorkerInstanceId.New(), ct);
    }
}
