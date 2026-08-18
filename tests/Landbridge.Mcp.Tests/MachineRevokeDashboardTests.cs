using System.Net;
using System.Text.Json;
using Landbridge.Contracts;
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
/// The human surface for un-trusting a machine (§5, §13), over real HTTP.
///
/// <para>The operation existed with <b>no</b> surface at all: nothing in the dashboard, no
/// endpoint, no tool — the only caller was a test. §5's "un-trusting a machine must take
/// seconds" was therefore a claim about a method nobody could reach, and an operator whose
/// box was compromised had the database and nothing else. So what is asserted here is
/// mostly that the door exists, is reachable by the person who would use it, and is closed
/// to everyone else.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MachineRevokeDashboardTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly MachineDeclaration Decl = new("box-1", "test", "macos", "standard");

    [SkippableFact]
    public async Task A_human_revokes_a_connected_machine_from_the_machine_group_view()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var machineId = await EnrollAsync(ct);
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        // The machine is dialed in, so it shows on the view — with the control.
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register(machineId.ToString(), Profiles(), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(machineId.ToString(), Ready(machineId.ToString()));

        var view = await GetAuthedAsync(app, "/dashboard/machines", ct);
        Assert.Contains("/dashboard/machines/revoke", view, StringComparison.Ordinal);
        Assert.Contains(machineId.ToString(), view, StringComparison.Ordinal);

        var revoked = await PostRevokeAsync(app, machineId, await IssueHumanTokenAsync(ct), ct);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Contains("Machine revoked", await revoked.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // Not a cosmetic confirmation: the machine is out of the registry and its row is
        // revoked, so it is undispatchable and cannot bind, refresh, or reconnect.
        Assert.Null(registry.SnapshotFor(machineId.ToString()));
        await using var db = pg.NewContext();
        Assert.True((await db.Set<MachineRow>().AsNoTracking().SingleAsync(m => m.Id == machineId, ct)).Revoked);
    }

    /// <summary>
    /// A machine that has already dropped is the ordinary case for a revoke — the operator
    /// pulled the plug, or the box stopped answering, and the credentials on it are still
    /// live. It is not on the view (which lists connected machines), so the JSON twin taking
    /// the id directly is the path, and it must not need the machine to be there.
    /// </summary>
    [SkippableFact]
    public async Task An_offline_machine_revokes_through_the_json_twin_and_reports_what_it_took()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var machineId = await EnrollAsync(ct);
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/machines/revoke?format=json")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["machineId"] = machineId.ToString() }),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={await IssueHumanTokenAsync(ct)}");
        req.Headers.Add("Origin", SelfOrigin(app));
        using var res = await client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        Assert.False(body.GetProperty("channelClosed").GetBoolean());
        Assert.Equal(0, body.GetProperty("tasksRequeued").GetInt32());

        await using var db = pg.NewContext();
        Assert.True((await db.Set<MachineRow>().AsNoTracking().SingleAsync(m => m.Id == machineId, ct)).Revoked);
    }

    /// <summary>
    /// Human-only, and for a reason about authority rather than duplication: a machine
    /// belongs to no Team — it runs whatever the fleet dispatches to it — so a Lead revoking
    /// one would be a Team un-trusting infrastructure other Teams are working on. There is no
    /// MCP twin for the same reason, so the refusal has to say what the Lead should do
    /// instead of implying a tool exists.
    /// </summary>
    [SkippableFact]
    public async Task A_lead_cannot_revoke_a_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var machineId = await EnrollAsync(ct);
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        string leadToken;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = await tokens.ClaimLeadAsync(human.Token, TeamId.New(), ct: ct);
            leadToken = ((LeadClaimResult.Claimed)claim).Token.Token;
        }

        var refused = await PostRevokeAsync(app, machineId, leadToken, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        // Through the same scope-refusal page every other over-reaching read gets (#154), and it
        // names the reason plus the only route a Lead has, rather than a bare 403 an agent would
        // have to invent an explanation for.
        var body = await refused.Content.ReadAsStringAsync(ct);
        Assert.Contains("human-operator action", body, StringComparison.Ordinal);
        Assert.Contains("ask your operator", body, StringComparison.Ordinal);

        // Refused means untouched — a Lead that reached this path changed nothing.
        await using var check = pg.NewContext();
        Assert.False((await check.Set<MachineRow>().AsNoTracking().SingleAsync(m => m.Id == machineId, ct)).Revoked);
    }

    /// <summary>
    /// A sessionless revoke from the dashboard's own page is bounced to login, not silently
    /// ignored. The <c>Origin</c> is supplied deliberately: the origin check runs <em>before</em>
    /// the session is resolved (#154), so without one this would be refused as cross-origin and
    /// the auth branch under test would never run.
    /// </summary>
    [SkippableFact]
    public async Task An_anonymous_revoke_is_bounced_to_login()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var machineId = await EnrollAsync(ct);
        await using var app = BuildPlane();
        await app.StartAsync(ct);

        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/machines/revoke")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["machineId"] = machineId.ToString() }),
        };
        req.Headers.Add("Origin", SelfOrigin(app));
        using var res = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/dashboard/login", res.Headers.Location?.ToString());

        await using var db = pg.NewContext();
        Assert.False((await db.Set<MachineRow>().AsNoTracking().SingleAsync(m => m.Id == machineId, ct)).Revoked);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task<Guid> EnrollAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var creds = await tokens.ExchangeEnrollmentAsync(
            (await tokens.IssueEnrollmentTokenAsync(ct)).Token, Decl, ct);
        return creds!.MachineId;
    }

    /// <summary>
    /// A revoke POST as the dashboard's own page makes it. The <c>Origin</c> is not incidental:
    /// this is a mutating form, so it is judged on where it came from (<c>OriginGuard</c>, #154)
    /// before the session is even resolved, and a POST without one is refused. The
    /// cross-origin half of that rule is covered where the rest of it lives, in
    /// <c>DashboardScopeAndOriginTests</c>.
    /// </summary>
    private static async Task<HttpResponseMessage> PostRevokeAsync(
        WebApplication app, Guid machineId, string token, CancellationToken ct)
    {
        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/dashboard/machines/revoke")
        {
            Content = new FormUrlEncodedContent(
                new Dictionary<string, string> { ["machineId"] = machineId.ToString() }),
        };
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={token}");
        req.Headers.Add("Origin", SelfOrigin(app));
        return await client.SendAsync(req, ct);
    }

    private static string SelfOrigin(WebApplication app) =>
        new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal)))
            .GetLeftPart(UriPartial.Authority);

    private static IReadOnlySet<string> Profiles() => new HashSet<string>(StringComparer.Ordinal) { "default" };

    private static MachineHeartbeat Ready(string machineId) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], DateTimeOffset.UtcNow);

    private WebApplication BuildPlane()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<MachineRevocationService>();
        builder.Services.AddDashboard();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        builder.Services.AddLandbridgeForwarding(); // the sink the revoke requeues through
        builder.Services.AddSingleton<RunnerEventSink>();
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

    private static HttpClient Client(WebApplication app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))),
        };

    private async Task<string> GetAuthedAsync(WebApplication app, string path, CancellationToken ct)
    {
        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={await IssueHumanTokenAsync(ct)}");
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
