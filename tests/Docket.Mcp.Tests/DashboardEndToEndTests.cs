using System.Net;
using System.Text.Json;
using Docket.Contracts;
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
/// The §12 dashboard over real HTTP against the ephemeral Postgres fixture: the
/// three views plus the event log, their cookie/bearer auth gate, and the JSON
/// twin. Seeds state through the real <see cref="TaskStore"/> / <see cref="TokenService"/>
/// and the in-memory <see cref="RunnerConnectionRegistry"/>, then drives the pages
/// with an <see cref="HttpClient"/> — no browser, no MCP client.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DashboardEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly MachineSnapshot AnyMachine =
        new("box-1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── (a) Auth gate: cookie or bearer, else redirect ────────────────────────

    [SkippableFact]
    public async Task Unauthenticated_and_garbage_cookie_redirect_to_login_valid_cookie_renders()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        using var client = Client(app);

        // No credential → redirect to the login page.
        var anon = await client.GetAsync("/dashboard/machines", ct);
        Assert.Equal(HttpStatusCode.Redirect, anon.StatusCode);
        Assert.Contains("/dashboard/login", anon.Headers.Location!.ToString(), StringComparison.Ordinal);

        // A garbage cookie is not a live token → still redirected.
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/dashboard/machines"))
        {
            req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}=not-a-real-token");
            var garbage = await client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.Redirect, garbage.StatusCode);
            Assert.Contains("/dashboard/login", garbage.Headers.Location!.ToString(), StringComparison.Ordinal);
        }

        // A JSON caller without a credential gets 401, not a redirect.
        var anonJson = await client.GetAsync("/dashboard/machines?format=json", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonJson.StatusCode);

        // A live human-session token in the cookie → 200 and the real page.
        var humanToken = await IssueHumanTokenAsync(ct);
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/dashboard/machines"))
        {
            req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={humanToken}");
            var ok = await client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var body = await ok.Content.ReadAsStringAsync(ct);
            Assert.Contains("Machine Group", body, StringComparison.Ordinal);
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Login_post_sets_session_cookie_and_lands_on_dashboard()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        using var client = Client(app);

        var humanToken = await IssueHumanTokenAsync(ct);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = humanToken });
        var res = await client.PostAsync("/dashboard/login", form, ct);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/dashboard/machines", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        var setCookie = Assert.Single(res.Headers.GetValues("Set-Cookie"));
        Assert.Contains(DashboardAuth.CookieName, setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);

        // The one static asset every page links must be served, unauthenticated.
        var css = await client.GetAsync("/dashboard/dashboard.css", ct);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);

        await app.StopAsync(ct);
    }

    // ── (b) Machine Group view: a machine, its running task, and its Team ──────

    [SkippableFact]
    public async Task Machine_view_shows_a_registered_machine_with_its_running_task_and_team()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var team = TeamId.New();
        var (taskId, ns) = await SeedWorkingTaskAsync(team, CompletionMode.Automated, ct);

        // Register the machine into the live registry and track the dispatched task.
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register("box-1", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("box-1", new MachineHeartbeat(
            "box-1", Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningTasks: 1, ["default"], DateTimeOffset.UtcNow));
        registry.TrackDispatch("box-1", taskId);

        var body = await GetAuthedAsync(app, "/dashboard/machines", ct);
        Assert.Contains("box-1", body, StringComparison.Ordinal);
        Assert.Contains("ready", body, StringComparison.Ordinal);
        Assert.Contains(ns, body, StringComparison.Ordinal);                       // the running task
        Assert.Contains(ShortId(team.Value), body, StringComparison.Ordinal);      // its owning Team
        Assert.Contains("no subagents reported", body, StringComparison.Ordinal);  // honest empty tree

        await app.StopAsync(ct);
    }

    // ── (c) Team view: parks, service, lead, input request — HTML and JSON ─────

    [SkippableFact]
    public async Task Team_view_surfaces_parks_service_lead_and_input_request_in_html_and_json()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var team = TeamId.New();

        // A working task with a registered service.
        var (_, workingNs, workingCaller) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Automated, ct);
        await WithStoreAsync(async store =>
            await store.RegisterServiceAsync(workingCaller, "api", 8080, ct));

        // A blocked task (open input request).
        var (blockedId, blockedNs, blockedCaller) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Automated, ct);
        await WithStoreAsync(async store =>
            await store.ApplyAsync(blockedId, new RequestInput(blockedCaller, InputRequestKind.Question), ct));

        // A parked task: block it, then let its wait TTL expire (one park).
        var (parkedId, parkedNs, parkedCaller) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Automated, ct);
        await WithStoreAsync(async store =>
        {
            await store.ApplyAsync(parkedId, new RequestInput(parkedCaller, InputRequestKind.Question), ct);
            await store.ApplyAsync(parkedId, new WaitTtlExpired(new ParkRecord("box-1", null, null, 1)), ct);
        });

        // A Lead claims the Team.
        var leadHumanShort = await ClaimLeadAsync(team, ct);

        // ── HTML ────────────────────────────────────────────────────────────
        var html = await GetAuthedAsync(app, $"/dashboard/teams/{team.Value}", ct);
        Assert.Contains("api", html, StringComparison.Ordinal);            // registered service
        Assert.Contains("attached", html, StringComparison.Ordinal);       // Lead attached
        Assert.Contains(leadHumanShort, html, StringComparison.Ordinal);   // and who
        Assert.Contains(blockedNs, html, StringComparison.Ordinal);        // open input request
        Assert.Contains("Open input requests", html, StringComparison.Ordinal);
        Assert.Contains("parks total", html, StringComparison.Ordinal);    // the §12 parks slot
        Assert.Contains(parkedNs, html, StringComparison.Ordinal);
        Assert.Contains(workingNs, html, StringComparison.Ordinal);

        // ── JSON twin: same fields, machine-readable ─────────────────────────
        var json = await GetAuthedAsync(app, $"/dashboard/teams/{team.Value}?format=json", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var services = root.GetProperty("services");
        Assert.Contains(services.EnumerateArray(),
            s => s.GetProperty("name").GetString() == "api" && s.GetProperty("port").GetInt32() == 8080);

        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("leadHumanId").ValueKind);

        var openRequests = root.GetProperty("openInputRequests");
        Assert.Contains(openRequests.EnumerateArray(),
            r => r.GetProperty("namespace").GetString() == blockedNs);

        var tasks = root.GetProperty("tasks");
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("namespace").GetString() == parkedNs && t.GetProperty("parks").GetInt32() >= 1);
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("state").GetString() == "Parked");

        await app.StopAsync(ct);
    }

    // ── (d) Human inbox: question + review appear; empty states render ─────────

    [SkippableFact]
    public async Task Inbox_shows_a_question_and_a_review_task_and_renders_empty_states()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var teamA = TeamId.New();
        var (blockedId, blockedNs, blockedCaller) = await SeedWorkingTaskWithCallerAsync(teamA, CompletionMode.Automated, ct);
        await WithStoreAsync(async store =>
            await store.ApplyAsync(blockedId, new RequestInput(blockedCaller, InputRequestKind.Question), ct));

        var teamB = TeamId.New();
        var (reviewId, reviewNs, reviewCaller) = await SeedWorkingTaskWithCallerAsync(teamB, CompletionMode.Review, ct);
        await WithStoreAsync(async store =>
            await store.ApplyAsync(reviewId, new ReportResult(reviewCaller, "ref://result"), ct));

        var body = await GetAuthedAsync(app, "/dashboard/inbox", ct);
        Assert.Contains("Open questions", body, StringComparison.Ordinal);
        Assert.Contains(blockedNs, body, StringComparison.Ordinal);
        Assert.Contains("Awaiting review", body, StringComparison.Ordinal);
        Assert.Contains(reviewNs, body, StringComparison.Ordinal);
        // Honest empty states for the §12 rows with no data source yet.
        Assert.Contains("Auth failures", body, StringComparison.Ordinal);
        Assert.Contains("Not recorded", body, StringComparison.Ordinal);
        Assert.Contains("Permission requests", body, StringComparison.Ordinal);
        Assert.Contains("Not built yet", body, StringComparison.Ordinal);

        // JSON twin carries the same two items.
        var json = await GetAuthedAsync(app, "/dashboard/inbox?format=json", ct);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains(doc.RootElement.GetProperty("questions").EnumerateArray(),
            q => q.GetProperty("namespace").GetString() == blockedNs);
        Assert.Contains(doc.RootElement.GetProperty("awaitingReview").EnumerateArray(),
            r => r.GetProperty("namespace").GetString() == reviewNs);

        await app.StopAsync(ct);
    }

    // ── (e) Event log: a Lead claim and a task transition both appear ──────────

    [SkippableFact]
    public async Task Event_log_shows_a_lead_claim_and_a_task_transition()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var team = TeamId.New();
        await ClaimLeadAsync(team, ct);                    // → a lead_events "Claimed" row
        await SeedWorkingTaskAsync(team, CompletionMode.Automated, ct); // created + Dispatch task_events

        var body = await GetAuthedAsync(app, "/dashboard/events", ct);
        Assert.Contains("Claimed", body, StringComparison.Ordinal);   // lead event
        Assert.Contains("Dispatch", body, StringComparison.Ordinal);  // a task transition
        Assert.Contains("Working", body, StringComparison.Ordinal);   // its target state badge

        await app.StopAsync(ct);
    }

    // ── Host + seeding helpers ────────────────────────────────────────────────

    private WebApplication BuildPlane()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TaskStore>();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<DashboardQueries>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
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

    private static HttpClient Client(WebApplication app)
    {
        var baseUrl = app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(baseUrl),
        };
    }

    /// <summary>GETs a gated page with a freshly-issued human-session cookie; asserts 200.</summary>
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

    private async Task<string> IssueHumanTokenAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        return (await tokens.IssueHumanSessionAsync(ct)).Token;
    }

    /// <summary>Claims a Lead for the Team and returns the claiming human's short id.</summary>
    private async Task<string> ClaimLeadAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
        return ShortId(human.CredentialId);
    }

    /// <summary>Creates one task and dispatches it to working; returns its id and namespace.</summary>
    private async Task<(TaskId Id, string Namespace)> SeedWorkingTaskAsync(
        TeamId team, CompletionMode mode, CancellationToken ct)
    {
        var (id, ns, _) = await SeedWorkingTaskWithCallerAsync(team, mode, ct);
        return (id, ns);
    }

    /// <summary>As above, also returning the incumbent worker caller for further transitions.</summary>
    private async Task<(TaskId Id, string Namespace, WorkerCaller Caller)> SeedWorkingTaskWithCallerAsync(
        TeamId team, CompletionMode mode, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", mode, null, TeamBudgetRemains: true), ct);
        var instance = WorkerInstanceId.New();
        // Deterministic: this is the only submitted task at the moment it dispatches.
        await store.DispatchNextAsync(AnyMachine, instance, ct);
        return (created.Task.Id, created.Task.Namespace, new WorkerCaller(team, created.Task.Id, instance));
    }

    private async Task WithStoreAsync(Func<TaskStore, Task> action)
    {
        await using var db = pg.NewContext();
        await action(new TaskStore(db, TimeProvider.System));
    }

    private static string ShortId(Guid value)
    {
        var s = value.ToString();
        var dash = s.IndexOf('-');
        return dash > 0 ? s[..dash] : s;
    }
}
