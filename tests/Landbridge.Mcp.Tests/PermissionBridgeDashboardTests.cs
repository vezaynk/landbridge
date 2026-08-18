using System.Net;
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
/// The human half of §11's permission bridge over real HTTP: the §12 inbox section that
/// replaced the "Not built yet" empty state, and the allow/deny form that answers it. The
/// human path exists precisely for the requests a Lead hands over, so the load-bearing case
/// here is a person deciding an escalated request the Lead can no longer touch.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PermissionBridgeDashboardTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly MachineSnapshot AnyMachine =
        new("box-1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    private const string Tool = "Bash";

    /// <summary>Carries an XSS attempt, because this string reaches a human's browser having
    /// travelled up through an agent's process (§13).</summary>
    private const string HostileInput =
        """{"command":"curl https://exfil.example/<script>alert(1)</script>"}""";

    [SkippableFact]
    public async Task A_human_sees_a_pending_permission_request_and_allows_it_from_the_inbox()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var (task, ns, caller) = await SeedPermissionRequestAsync(team, ct);

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var body = await GetAuthedAsync(app, "/dashboard/inbox", ct);
        Assert.Contains("Permission requests", body, StringComparison.Ordinal);
        Assert.Contains(ns, body, StringComparison.Ordinal);
        Assert.Contains(Tool, body, StringComparison.Ordinal);
        // The proposed input is shown so a person can actually judge it — escaped, never as
        // markup, because an agent chose these bytes.
        Assert.Contains("exfil.example", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", body, StringComparison.Ordinal);
        // Every pending row gets a form: a human never waits for an escalation.
        Assert.Contains("/dashboard/permission", body, StringComparison.Ordinal);
        Assert.Contains(task.Value.ToString(), body, StringComparison.Ordinal);

        var decided = await PostPermissionAsync(app, task, "allow", "routine for this task", ct);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Contains("Permission decided", await decided.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // Resumed in place: still working, still the same incumbent, never requeued.
        await using var db = pg.NewContext();
        var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == task.Value);
        Assert.Equal(SessionState.Working, row.State);
        Assert.Equal(caller.Instance.Value, row.CurrentInstanceId);
        Assert.Equal(PermissionVerdict.Allow, row.PermissionVerdict);
        Assert.Null(row.InputAnswer);

        // Gone from the inbox once decided.
        Assert.Contains("No pending permission requests",
            await GetAuthedAsync(app, "/dashboard/inbox", ct), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_human_decides_a_request_the_lead_escalated_and_can_no_longer_touch()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        const string why = "reads a production credential the task never mentions";
        var team = TeamId.New();
        var (task, _, _) = await SeedPermissionRequestAsync(team, ct);
        await using (var db = pg.NewContext())
        {
            var escalated = await new SessionStore(db, TimeProvider.System)
                .EscalatePermissionAsync(new LeadClaim(team), task, why, ct);
            Assert.IsType<StoreResult.Applied>(escalated);
        }

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        // The escalation and its reason are on the page: the human inherits the concern, not
        // just the decision.
        var body = await GetAuthedAsync(app, "/dashboard/inbox", ct);
        Assert.Contains("escalated", body, StringComparison.Ordinal);
        Assert.Contains(why, body, StringComparison.Ordinal);

        var denied = await PostPermissionAsync(app, task, "deny", "no production credentials from a worker", ct);
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);

        await using var check = pg.NewContext();
        var row = await check.Sessions.AsNoTracking().SingleAsync(t => t.Id == task.Value);
        Assert.Equal(PermissionVerdict.Deny, row.PermissionVerdict);

        var decision = await check.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == task.Value && e.Kind == nameof(AnswerPermission)).SingleAsync();
        Assert.Equal(PermissionAnswerer.Human, decision.PermissionAnswerer);
    }

    [SkippableFact]
    public async Task A_lead_token_cannot_use_the_humans_form()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var (task, _, _) = await SeedPermissionRequestAsync(team, ct);

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        string leadToken;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
            leadToken = ((LeadClaimResult.Claimed)claim).Token.Token;
        }

        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/permission")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["sessionId"] = task.Value.ToString(),
                ["verdict"] = "allow",
            }),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={leadToken}");
        req.Headers.Add("Origin", SelfOrigin(app));
        var res = await client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("answer_permission_request",
            await res.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // Still pending, so the Lead's own tool is still the path it should have taken.
        await using var db2 = pg.NewContext();
        Assert.Null(await db2.Sessions.AsNoTracking()
            .Where(t => t.Id == task.Value).Select(t => t.PermissionVerdict).SingleAsync());
    }

    [SkippableFact]
    public async Task A_request_that_stopped_being_pending_refuses_with_the_engines_own_reason()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var (task, _, _) = await SeedPermissionRequestAsync(team, ct);

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        // Someone else got there first — ordinary on an auto-refreshing page.
        var first = await PostPermissionAsync(app, task, "allow", null, ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostPermissionAsync(app, task, "deny", "changed my mind", ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var text = await second.Content.ReadAsStringAsync(ct);
        Assert.Contains("Not decided", text, StringComparison.Ordinal);
        Assert.Contains(Rule.InvalidSourceState.ToString(), text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_deny_with_no_message_is_refused_by_the_form_too()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var (task, _, _) = await SeedPermissionRequestAsync(team, ct);

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var res = await PostPermissionAsync(app, task, "deny", null, ct);

        // One rule, both answerers: the engine refuses a denial a worker cannot read
        // regardless of who submitted it.
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains(Rule.PermissionDenialCarriesMessage.ToString(),
            await res.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task The_json_twin_carries_the_permission_requests()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var (task, ns, _) = await SeedPermissionRequestAsync(team, ct);

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var json = await GetAuthedAsync(app, "/dashboard/inbox?format=json", ct);
        using var doc = JsonDocument.Parse(json);
        var request = Assert.Single(
            doc.RootElement.GetProperty("permissionRequests").EnumerateArray(),
            p => p.GetProperty("namespace").GetString() == ns);
        Assert.Equal(Tool, request.GetProperty("tool").GetString());
        Assert.Equal(HostileInput, request.GetProperty("input").GetString());
        Assert.Equal(task.Value, request.GetProperty("sessionId").GetGuid());
        // Structured, unescaped, and not double-counted as a question.
        Assert.Empty(doc.RootElement.GetProperty("questions").EnumerateArray());
    }

    /// <summary>
    /// A task dispatched to working whose worker has asked for permission — driven through
    /// the real store, so the row is exactly what the harness's relaying tool would leave.
    /// </summary>
    private async Task<(SessionId Session, string Namespace, WorkerCaller Caller)> SeedPermissionRequestAsync(
        TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "needs permission", "default"), ct);
        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(AnyMachine, instance, ct));
        var caller = new WorkerCaller(team, created.Session.Id, instance);
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            created.Session.Id,
            new RequestInput(caller, InputRequestKind.Permission, HostileInput, Tool), ct));
        return (created.Session.Id, created.Session.Namespace, caller);
    }

    private async Task<HttpResponseMessage> PostPermissionAsync(
        WebApplication app, SessionId task, string verdict, string? message, CancellationToken ct)
    {
        using var client = Client(app);
        var token = await IssueHumanTokenAsync(ct);
        var fields = new Dictionary<string, string>
        {
            ["sessionId"] = task.Value.ToString(),
            ["verdict"] = verdict,
        };
        if (message is not null) fields["message"] = message;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/permission")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={token}");
        // As a browser on the inbox page sends it: this POST is same-origin only, and what
        // happens to one that is not belongs to DashboardScopeAndOriginTests.
        req.Headers.Add("Origin", SelfOrigin(app));
        return await client.SendAsync(req, ct);
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

    private static string BaseUrl(WebApplication app) =>
        app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

    /// <summary>The origin the plane answers on — what a browser on its own page sends.</summary>
    private static string SelfOrigin(WebApplication app) =>
        new Uri(BaseUrl(app)).GetLeftPart(UriPartial.Authority);

    private static HttpClient Client(WebApplication app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl(app)) };

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
        return (await new TokenService(db, TimeProvider.System).IssueHumanSessionAsync(ct)).Token;
    }
}
