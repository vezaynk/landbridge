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
/// The two rules that run across the whole §12 dashboard rather than route by route, over real
/// HTTP against the ephemeral Postgres fixture.
///
/// <para><b>Who may read what.</b> Every view here accepts a Lead token, because §12 requires a
/// structured twin a reattaching Lead can consume (§4). What it may consume is its own Team: a
/// Lead's credential is Team-scoped (§5) and an agent gets no cross-Team or machine-group view
/// (§10 as-built, §12 "human operator session only"). The suite therefore seeds <b>two</b> Teams
/// for every read and asserts the other one is absent — a scoping bug is invisible against a
/// single-Team fixture, which is how the instance-wide behaviour survived as long as it did.</para>
///
/// <para><b>Where a write may come from.</b> The session lives in a cookie and the forms carry no
/// token, so a mutating POST has to prove it came from the dashboard's own origin. The load-bearing
/// case is a §8.4 preview host: same <em>site</em> as the dashboard by design, so
/// <c>SameSite=Lax</c> attaches <c>landbridge_session</c> to its POSTs and only an origin check
/// refuses them.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DashboardScopeAndOriginTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly MachineSnapshot AnyMachine =
        new("box-1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Reads: a Lead sees its own Team, a human sees the instance ─────────────

    /// <summary>
    /// The worst of the reads, because the route takes an arbitrary Team id and the page it
    /// serves carries the Team's prose — the worker's report, the question it asked, the answer
    /// it got, the artifact pointer it handed over. A Lead reaches its own and nothing else.
    /// </summary>
    [SkippableFact]
    public async Task A_lead_reads_its_own_team_page_and_is_refused_another_teams()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var mine = TeamId.New();
        var theirs = TeamId.New();
        await SeedReportedTaskAsync(mine, "my own worker said this", ct);
        var (_, theirNamespace) = await SeedReportedTaskAsync(theirs, TheirSecret, ct);
        var lead = await IssueLeadTokenAsync(mine, ct);

        using var client = Client(app);

        // Its own Team: the full page, prose and all.
        var own = await GetAsync(client, $"/dashboard/teams/{mine.Value}", lead, ct);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Contains("my own worker said this", await own.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // Another Team, by id: refused before the read happens, on both representations, and
        // the refusal carries none of what it was protecting.
        var otherHtml = await GetAsync(client, $"/dashboard/teams/{theirs.Value}", lead, ct);
        var otherJson = await GetAsync(client, $"/dashboard/teams/{theirs.Value}?format=json", lead, ct);
        Assert.Equal(HttpStatusCode.Forbidden, otherHtml.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherJson.StatusCode);
        var refusedBody = await otherHtml.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(TheirSecret, refusedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(theirNamespace, refusedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(TheirSecret, await otherJson.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // A human operator reads either — the refusal is about the credential's scope, not
        // about the page being unavailable.
        var human = await IssueHumanTokenAsync(ct);
        var asHuman = await GetAsync(client, $"/dashboard/teams/{theirs.Value}", human, ct);
        Assert.Equal(HttpStatusCode.OK, asHuman.StatusCode);
        Assert.Contains(TheirSecret, await asHuman.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // An id that is not a Team id is still a bad request, not a scope refusal: the parse
        // happens first, so the two failures stay distinguishable.
        var garbage = await GetAsync(client, "/dashboard/teams/not-a-guid", lead, ct);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

        await app.StopAsync(ct);
    }

    /// <summary>
    /// The routes that name no Team answer a Lead with its own rather than refusing: the Team
    /// list, the inbox, and the event log are all reattachment surfaces (§4). Each is asserted
    /// on both halves — its own Team present, the other Team absent — because a filter applied
    /// to one query and forgotten on the next is the shape this bug had.
    /// </summary>
    [SkippableFact]
    public async Task The_team_list_inbox_and_event_log_carry_only_the_leads_own_team()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var mine = TeamId.New();
        var theirs = TeamId.New();
        var (myBlocked, myNamespace) = await SeedBlockedTaskAsync(mine, "what should I do here?", ct);
        var (_, theirNamespace) = await SeedBlockedTaskAsync(theirs, TheirSecret, ct);
        // A Lead claim on the other Team, so its takeover event is in the log too.
        await ClaimLeadAsync(theirs, ct);
        var lead = await IssueLeadTokenAsync(mine, ct);

        using var client = Client(app);

        // ── The Team list: exactly one row, and it is the Lead's own.
        using var teams = await GetJsonAsync(client, "/dashboard/teams?format=json", lead, ct);
        var only = Assert.Single(teams.RootElement.EnumerateArray());
        Assert.Equal(mine.Value, only.GetProperty("teamId").GetGuid());

        // ── The inbox: its own open question, not the other Team's.
        using var inbox = await GetJsonAsync(client, "/dashboard/inbox?format=json", lead, ct);
        var question = Assert.Single(inbox.RootElement.GetProperty("questions").EnumerateArray());
        Assert.Equal(myBlocked.Value, question.GetProperty("sessionId").GetGuid());
        // The other Team's question text is the thing a human answers from here, so its absence
        // is asserted against the whole document rather than one array.
        Assert.DoesNotContain(TheirSecret, inbox.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(theirNamespace, inbox.RootElement.GetRawText(), StringComparison.Ordinal);

        // ── The event log: its own transitions and its own Team's lead events only.
        using var events = await GetJsonAsync(client, "/dashboard/events?format=json", lead, ct);
        var rows = events.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, e => Assert.Equal(mine.Value, e.GetProperty("teamId").GetGuid()));
        Assert.Contains(rows, e => e.GetProperty("namespace").GetString() == myNamespace);
        // The log interleaves two sources, and a filter applied to task_events alone would still
        // pass the check above only because lead_events happened to be absent. Both Teams have a
        // lead claim, so exactly one of them reaching this log is what says the second source is
        // scoped too.
        var claim = Assert.Single(rows, e => e.GetProperty("source").GetString() == "lead");
        Assert.Equal("Claimed", claim.GetProperty("kind").GetString());
        Assert.Equal(mine.Value, claim.GetProperty("teamId").GetGuid());

        // ── The human's instance-wide view is unchanged: both Teams, both questions.
        var human = await IssueHumanTokenAsync(ct);
        using var allTeams = await GetJsonAsync(client, "/dashboard/teams?format=json", human, ct);
        Assert.Equal(2, allTeams.RootElement.GetArrayLength());
        using var allInbox = await GetJsonAsync(client, "/dashboard/inbox?format=json", human, ct);
        Assert.Equal(2, allInbox.RootElement.GetProperty("questions").GetArrayLength());
        Assert.Contains(TheirSecret, allInbox.RootElement.GetRawText(), StringComparison.Ordinal);
        using var allEvents = await GetJsonAsync(client, "/dashboard/events?format=json", human, ct);
        Assert.Contains(allEvents.RootElement.EnumerateArray(), e => e.GetProperty("kind").GetString() == "Claimed");

        await app.StopAsync(ct);
    }

    /// <summary>
    /// The machine group is the one read with no Lead-scoped answer to give: a machine is not
    /// Team-scoped, it runs whatever the plane dispatched to it, and machine enumeration is a
    /// permanent non-goal for agents (§10 as-built, §12). So a Lead is refused rather than
    /// served a filtered fleet — and the refusal must not leak the machine ids it withheld.
    /// </summary>
    [SkippableFact]
    public async Task The_machine_group_refuses_a_lead_and_names_no_machine_while_doing_it()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var team = TeamId.New();
        var (sessionId, _) = await SeedWorkingTaskAsync(team, ct);
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register("box-1", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("box-1", new MachineHeartbeat(
            "box-1", Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningSessions: 1, ["default"], DateTimeOffset.UtcNow));
        registry.TrackDispatch("box-1", sessionId);

        using var client = Client(app);
        var lead = await IssueLeadTokenAsync(team, ct);

        // Even the Lead of the Team whose task is running on this machine: the view is not for it.
        var html = await GetAsync(client, "/dashboard/machines", lead, ct);
        var json = await GetAsync(client, "/dashboard/machines?format=json", lead, ct);
        Assert.Equal(HttpStatusCode.Forbidden, html.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, json.StatusCode);
        Assert.DoesNotContain("box-1", await html.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.DoesNotContain("box-1", await json.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // The human operator's fleet view still works — this is a credential rule, not a
        // removal of the view.
        var human = await IssueHumanTokenAsync(ct);
        var asHuman = await GetAsync(client, "/dashboard/machines", human, ct);
        Assert.Equal(HttpStatusCode.OK, asHuman.StatusCode);
        Assert.Contains("box-1", await asHuman.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        await app.StopAsync(ct);
    }

    // ── Writes: the origin a mutating POST came from ───────────────────────────

    /// <summary>
    /// The §11 exploit, as a test. A permission POST needs only a task id and a verdict, and the
    /// operator's cookie rides along on any same-site POST — so a worker's own §8.4 preview page,
    /// which shares the dashboard's registrable domain by design, could auto-submit its own
    /// pending request and have the operator's session allow it. The origin check is what
    /// refuses that; the request must still be pending afterwards.
    /// </summary>
    [SkippableFact]
    public async Task A_permission_verdict_posted_from_a_preview_host_is_refused_and_stays_pending()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var team = TeamId.New();
        var task = await SeedPermissionRequestAsync(team, ct);
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);

        // Same site, different origin: the worker's own preview page. Lax does not stop this.
        var forged = await PostAsync(
            client, "/dashboard/permission", human, "http://abcd1234.preview.localhost", ct,
            [("sessionId", task.Value.ToString()), ("verdict", "allow")]);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        // Nothing was decided: the worker is still blocked and still waiting on a person.
        await using (var db = pg.NewContext())
        {
            var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == task.Value, ct);
            Assert.Null(row.PermissionVerdict);
            Assert.Equal(SessionState.BlockedOnInput, row.State);
        }

        // The same verdict from the dashboard's own page goes through, so the check is a rule
        // about where the POST came from and not a removal of the §11 human path.
        var honest = await PostAsync(
            client, "/dashboard/permission", human, SelfOrigin(app), ct,
            [("sessionId", task.Value.ToString()), ("verdict", "allow")]);
        Assert.Equal(HttpStatusCode.OK, honest.StatusCode);

        await using (var db = pg.NewContext())
        {
            var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == task.Value, ct);
            Assert.Equal(PermissionVerdict.Allow, row.PermissionVerdict);
        }

        await app.StopAsync(ct);
    }

    /// <summary>
    /// The session writes, both directions. A cross-origin login would sign the operator into a
    /// session somebody else minted; a cross-origin logout would sign them out of their own. Both
    /// are refused, and the honest form of each still works.
    /// </summary>
    [SkippableFact]
    public async Task The_session_writes_are_same_origin_only()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        using var client = Client(app);

        // Login by pasted token (no operator passphrase configured on this plane, which is the
        // headless door and works regardless).
        var token = await IssueHumanTokenAsync(ct);

        var forgedLogin = await PostAsync(
            client, "/dashboard/login", session: null, "https://evil.example", ct, [("token", token)]);
        Assert.Equal(HttpStatusCode.Forbidden, forgedLogin.StatusCode);
        Assert.False(forgedLogin.Headers.Contains("Set-Cookie"));

        var honestLogin = await PostAsync(
            client, "/dashboard/login", session: null, SelfOrigin(app), ct, [("token", token)]);
        Assert.Equal(HttpStatusCode.Redirect, honestLogin.StatusCode);
        Assert.Contains(DashboardAuth.CookieName, Assert.Single(honestLogin.Headers.GetValues("Set-Cookie")),
            StringComparison.Ordinal);

        var forgedLogout = await PostAsync(client, "/dashboard/logout", token, "https://evil.example", ct);
        Assert.Equal(HttpStatusCode.Forbidden, forgedLogout.StatusCode);
        Assert.False(forgedLogout.Headers.Contains("Set-Cookie"));

        var honestLogout = await PostAsync(client, "/dashboard/logout", token, SelfOrigin(app), ct);
        Assert.Equal(HttpStatusCode.Redirect, honestLogout.StatusCode);

        await app.StopAsync(ct);
    }

    /// <summary>
    /// A POST whose <c>Origin</c> an intermediary stripped is accepted only on the browser's own
    /// word that it was first-party. <c>Sec-Fetch-Site</c> is that word — a document cannot set
    /// it — so <c>same-origin</c> passes and <c>cross-site</c>, or neither header at all, does
    /// not. Fail-closed on purpose: a scripted client posting to these forms sends its Origin.
    /// </summary>
    [SkippableFact]
    public async Task Fetch_metadata_stands_in_for_a_stripped_origin_but_only_when_it_says_same_origin()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        using var client = Client(app);
        var token = await IssueHumanTokenAsync(ct);

        foreach (var (fetchSite, expected) in new (string?, HttpStatusCode)[]
                 {
                     ("same-origin", HttpStatusCode.Redirect),
                     ("cross-site", HttpStatusCode.Forbidden),
                     ("same-site", HttpStatusCode.Forbidden),
                     (null, HttpStatusCode.Forbidden),
                 })
        {
            var res = await PostAsync(
                client, "/dashboard/logout", token, origin: null, ct, fetchSite: fetchSite);
            Assert.Equal(expected, res.StatusCode);
        }

        await app.StopAsync(ct);
    }

    /// <summary>
    /// The §13 machine revoke is the most valuable POST on this surface to forge, and the
    /// cheapest: it needs nothing but a machine id, and the ids are rendered on a page the
    /// operator's own session can read — so a same-site page that has seen one (a §8.4 preview,
    /// which shares the registrable domain by design) could evict a machine mid-flight, taking
    /// its command channel, its worker tokens and its running work with it. Denial of service
    /// against the fleet, spendable once per machine id and requiring no credential of its own.
    ///
    /// <para>Asserted on the machine row rather than the status code alone: a 403 that had
    /// already revoked would be the same hole with a tidier response. The honest POST then goes
    /// through, so this is a rule about where the request came from and not a
    /// human-only surface that refuses everyone.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_machine_revoke_posted_from_a_preview_host_is_refused_and_the_machine_keeps_running()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        Guid machineId;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var creds = await tokens.ExchangeEnrollmentAsync(
                (await tokens.IssueEnrollmentTokenAsync(ct)).Token,
                new MachineDeclaration("box-1", "test", "macos", "standard"), ct);
            machineId = creds!.MachineId;
        }

        // The machine is dialed in and holding its channel, so a successful forgery would cost
        // something rather than flipping a row on an absent box.
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register(
            machineId.ToString(), new HashSet<string>(StringComparer.Ordinal) { "default" },
            (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(
            machineId.ToString(),
            new MachineHeartbeat(machineId.ToString(), Ready: true, UnderBackPressure: false,
                new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], DateTimeOffset.UtcNow));

        var human = await IssueHumanTokenAsync(ct);
        using var client = Client(app);

        var forged = await PostAsync(
            client, "/dashboard/machines/revoke", human, "http://abcd1234.preview.localhost", ct,
            [("machineId", machineId.ToString())]);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        // Nothing was taken away: the machine is still trusted and still connected.
        await using (var db = pg.NewContext())
            Assert.False((await db.Set<MachineRow>().AsNoTracking()
                .SingleAsync(m => m.Id == machineId, ct)).Revoked);
        Assert.NotNull(registry.SnapshotFor(machineId.ToString()));

        // The same POST from the dashboard's own page revokes, so the refusal above was about
        // the origin and not about the action being unreachable.
        var honest = await PostAsync(
            client, "/dashboard/machines/revoke", human, SelfOrigin(app), ct,
            [("machineId", machineId.ToString())]);
        Assert.Equal(HttpStatusCode.OK, honest.StatusCode);

        await using (var db = pg.NewContext())
            Assert.True((await db.Set<MachineRow>().AsNoTracking()
                .SingleAsync(m => m.Id == machineId, ct)).Revoked);
        Assert.Null(registry.SnapshotFor(machineId.ToString()));

        await app.StopAsync(ct);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    /// <summary>The other Team's prose — distinctive, so its absence can be asserted against a
    /// whole document rather than one field.</summary>
    private const string TheirSecret = "rotate the staging-pg credential before Friday";

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
        // §13 revoke, the fourth origin-guarded write on this surface: the service plus the
        // sink it requeues a revoked machine's held work through.
        builder.Services.AddScoped<MachineRevocationService>();
        builder.Services.AddLandbridgeForwarding();
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

    private static string BaseUrl(WebApplication app) =>
        app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

    /// <summary>The origin the plane is answering on — what a browser on its own page sends.</summary>
    private static string SelfOrigin(WebApplication app) =>
        new Uri(BaseUrl(app)).GetLeftPart(UriPartial.Authority);

    private static HttpClient Client(WebApplication app) =>
        new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl(app)) };

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string path, string session, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={session}");
        return await client.SendAsync(req, ct);
    }

    private static async Task<JsonDocument> GetJsonAsync(
        HttpClient client, string path, string session, CancellationToken ct)
    {
        var res = await GetAsync(client, path, session, ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// A form POST with an explicit <c>Origin</c> — the header every mutating dashboard POST is
    /// now judged on. Null omits it, leaving <paramref name="fetchSite"/> as the only evidence.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, string? session, string? origin, CancellationToken ct,
        (string Key, string Value)[]? fields = null, string? fetchSite = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(
                (fields ?? []).Select(f => new KeyValuePair<string, string>(f.Key, f.Value))),
        };
        if (session is not null)
            req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={session}");
        if (origin is not null)
            req.Headers.Add("Origin", origin);
        if (fetchSite is not null)
            req.Headers.Add("Sec-Fetch-Site", fetchSite);
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

    private async Task ClaimLeadAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
    }

    /// <summary>One task created and dispatched to working; returns its id and namespace.</summary>
    private async Task<(SessionId Id, string Namespace)> SeedWorkingTaskAsync(TeamId team, CancellationToken ct)
    {
        var (id, ns, _) = await SeedWorkingTaskWithCallerAsync(team, ct);
        return (id, ns);
    }

    private async Task<(SessionId Id, string Namespace, WorkerCaller Caller)> SeedWorkingTaskWithCallerAsync(
        TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", "default"), ct);
        var instance = WorkerInstanceId.New();
        // Deterministic: this is the only submitted task at the moment it dispatches.
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(AnyMachine, instance, ct));
        return (created.Session.Id, created.Session.Namespace, new WorkerCaller(team, created.Session.Id, instance));
    }

    /// <summary>A task whose worker reported <paramref name="report"/> — prose on the Team page.</summary>
    private async Task<(SessionId Id, string Namespace)> SeedReportedTaskAsync(
        TeamId team, string report, CancellationToken ct)
    {
        var (id, ns, caller) = await SeedWorkingTaskWithCallerAsync(team, ct);
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        Assert.IsType<StoreResult.Applied>(
            await store.ApplyAsync(id, new ReportResult(caller, "git:done", report), ct));
        return (id, ns);
    }

    /// <summary>A task blocked on an open question — an inbox row carrying prose.</summary>
    private async Task<(SessionId Id, string Namespace)> SeedBlockedTaskAsync(
        TeamId team, string question, CancellationToken ct)
    {
        var (id, ns, caller) = await SeedWorkingTaskWithCallerAsync(team, ct);
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            id, new RequestInput(caller, InputRequestKind.Question, question), ct));
        return (id, ns);
    }

    /// <summary>A task whose worker is blocked asking permission for a tool call (§11).</summary>
    private async Task<SessionId> SeedPermissionRequestAsync(TeamId team, CancellationToken ct)
    {
        var (id, _, caller) = await SeedWorkingTaskWithCallerAsync(team, ct);
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            id, new RequestInput(caller, InputRequestKind.Permission, """{"command":"rm -rf /"}""", "Bash"), ct));
        return id;
    }
}
