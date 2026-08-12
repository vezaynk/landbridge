using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    public async Task Login_form_renders_operator_passphrase_and_serves_its_css()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane(OperatorPassphraseHash);
        await app.StartAsync(ct);
        using var client = Client(app);

        var login = await client.GetAsync("/dashboard/login", ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadAsStringAsync(ct);
        Assert.Contains("Operator passphrase", body, StringComparison.Ordinal);       // the primary door
        Assert.Contains("name=\"passphrase\"", body, StringComparison.Ordinal);
        Assert.Contains("or paste a token", body, StringComparison.Ordinal);          // the secondary door
        Assert.Contains("name=\"token\"", body, StringComparison.Ordinal);
        Assert.Contains("OAuth 2.1", body, StringComparison.Ordinal);                 // third-party clients note

        // The one static asset every page links must be served, unauthenticated.
        var css = await client.GetAsync("/dashboard/dashboard.css", ct);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Correct_passphrase_mints_a_session_sets_the_cookie_and_the_views_work()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane(OperatorPassphraseHash);
        await app.StartAsync(ct);
        using var client = Client(app);

        using var form = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["passphrase"] = OperatorPassphrase });
        var res = await client.PostAsync("/dashboard/login", form, ct);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/dashboard/machines", res.Headers.Location!.ToString(), StringComparison.Ordinal);
        var setCookie = Assert.Single(res.Headers.GetValues("Set-Cookie"));
        Assert.Contains(DashboardAuth.CookieName, setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);

        // The minted cookie is a real human session: it opens the gated views.
        var token = SessionCookieValue(res);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/dashboard/machines");
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={token}");
        var view = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Contains("Machine Group", await view.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Wrong_passphrase_re_renders_the_form_sets_no_cookie_and_applies_the_delay()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane(OperatorPassphraseHash);
        await app.StartAsync(ct);
        using var client = Client(app);

        using var form = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["passphrase"] = "not-the-passphrase" });

        var sw = Stopwatch.StartNew();
        var res = await client.PostAsync("/dashboard/login", form, ct);
        sw.Stop();

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
        Assert.Contains("Incorrect operator passphrase", await res.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        // The brute-force friction delay (~500ms) was applied (allow timer slack).
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(400), $"expected the delay, took {sw.ElapsedMilliseconds}ms");

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Unconfigured_verifier_fails_closed_with_503_on_a_passphrase_login()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane(operatorPassphraseHash: null); // no operator credential
        await app.StartAsync(ct);
        using var client = Client(app);

        using var form = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["passphrase"] = "anything" });
        var res = await client.PostAsync("/dashboard/login", form, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Token_paste_door_sets_the_cookie_for_a_valid_token_and_rejects_an_invalid_one()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // Unconfigured verifier on purpose: the paste door is the headless/Lead
        // path and must work even when no operator passphrase is configured.
        await using var app = BuildPlane(operatorPassphraseHash: null);
        await app.StartAsync(ct);
        using var client = Client(app);

        // Valid: a real human-session token → cookie set, land on the dashboard.
        var humanToken = await IssueHumanTokenAsync(ct);
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = humanToken }))
        {
            var ok = await client.PostAsync("/dashboard/login", form, ct);
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            Assert.Contains("/dashboard/machines", ok.Headers.Location!.ToString(), StringComparison.Ordinal);
            var setCookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"));
            Assert.Contains(DashboardAuth.CookieName, setCookie, StringComparison.Ordinal);
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        }

        // Invalid: a garbage token → re-rendered form, no cookie.
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "dkt_h_not-a-real-token" }))
        {
            var bad = await client.PostAsync("/dashboard/login", form, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
            Assert.False(bad.Headers.Contains("Set-Cookie"));
            Assert.Contains("not a valid human or Lead session", await bad.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

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
        var (taskId, ns) = await SeedWorkingTaskAsync(team, CompletionMode.Lead, ct);

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

    // ── (b1b) Machine Group view: operator-declared services (§10, §12) ────────

    [SkippableFact]
    public async Task Machine_view_shows_the_services_a_machine_reports()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        // §10/§12: services reach the plane on the heartbeat and nowhere else. The plane
        // stores the reported list against the connection and renders it — no
        // persistence, no interpretation, no opinion about health.
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register("svc-box", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        var started = DateTimeOffset.UtcNow.AddMinutes(-20);
        registry.ApplyHeartbeat("svc-box", new MachineHeartbeat(
            "svc-box", Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningTasks: 0, ["default"], DateTimeOffset.UtcNow,
            Services:
            [
                new ServiceStatus("web-dev", ServiceState.Running, 5173, started),
                new ServiceStatus("flaky-api", ServiceState.Failed, 9001, null,
                    Restarts: 4, LastExitCode: 3, LastFailureAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
                // A service that never started has no exit code. It must not render as 0 —
                // that would read as a clean exit and make a broken command look healthy.
                new ServiceStatus("never-ran", ServiceState.Failed, 9002, null,
                    Restarts: 1, LastExitCode: null, LastFailureAt: DateTimeOffset.UtcNow.AddMinutes(-2)),
            ]));

        var html = await GetAuthedAsync(app, "/dashboard/machines", ct);
        Assert.Contains("web-dev", html, StringComparison.Ordinal);
        Assert.Contains("running", html, StringComparison.Ordinal);
        Assert.Contains("5173", html, StringComparison.Ordinal);
        // The failure facts an operator asked for: state, restart count, exit code.
        Assert.Contains("flaky-api", html, StringComparison.Ordinal);
        Assert.Contains("failed", html, StringComparison.Ordinal);
        // Honest about what this view deliberately does not serve.
        Assert.Contains("not served here", html, StringComparison.Ordinal);

        // JSON twin carries the same facts, with the state as a readable string.
        var json = await GetAuthedAsync(app, "/dashboard/machines?format=json", ct);
        using var doc = JsonDocument.Parse(json);
        var machine = doc.RootElement.EnumerateArray()
            .Single(m => m.GetProperty("machineId").GetString() == "svc-box");
        var services = machine.GetProperty("services");
        Assert.Equal(3, services.GetArrayLength());
        var failed = services.EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "flaky-api");
        Assert.Equal("Failed", failed.GetProperty("state").GetString());
        Assert.Equal(4, failed.GetProperty("restarts").GetInt32());
        Assert.Equal(3, failed.GetProperty("lastExitCode").GetInt32());

        // An unknown exit code stays unknown across the JSON twin too — absent or null,
        // never coerced to a number that would read as a clean exit.
        var neverRan = services.EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "never-ran");
        Assert.True(
            !neverRan.TryGetProperty("lastExitCode", out var code)
            || code.ValueKind == JsonValueKind.Null,
            "a null exit code must not be rendered as a number");

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_heartbeat_without_services_does_not_blank_what_a_machine_reported()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        // Null means "this heartbeat says nothing about services" — which is what an
        // older runner sends. Treating it as "no services" would make the view flicker
        // against a mixed-version fleet, so the last reported list stands.
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register("mixed-box", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("mixed-box", new MachineHeartbeat(
            "mixed-box", Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningTasks: 0, ["default"], DateTimeOffset.UtcNow,
            Services: [new ServiceStatus("web-dev", ServiceState.Running, 5173)]));
        registry.ApplyHeartbeat("mixed-box", new MachineHeartbeat(
            "mixed-box", Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningTasks: 0, ["default"], DateTimeOffset.UtcNow));

        var html = await GetAuthedAsync(app, "/dashboard/machines", ct);
        Assert.Contains("web-dev", html, StringComparison.Ordinal);

        await app.StopAsync(ct);
    }

    // ── (b2) Machine Group view: a back-pressured, zero-task machine is visible ─

    [SkippableFact]
    public async Task Machine_view_shows_a_backpressured_idle_machine_with_no_tasks()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        // A connected machine that is under back-pressure (so not ready) and holds
        // no dispatched task: invisible to ReadyMachines() and AllTracked() alike,
        // and only surfaced by the registry's full MachineIds() enumeration.
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register("idle-box", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("idle-box", new MachineHeartbeat(
            "idle-box", Ready: false, UnderBackPressure: true, new SystemLoad(0, 0, 0),
            RunningTasks: 0, ["default"], DateTimeOffset.UtcNow));

        // HTML: the machine and its back-pressure badge appear, with an empty task list.
        var html = await GetAuthedAsync(app, "/dashboard/machines", ct);
        Assert.Contains("idle-box", html, StringComparison.Ordinal);
        Assert.Contains("back-pressure", html, StringComparison.Ordinal);
        Assert.Contains("No tasks running on this machine.", html, StringComparison.Ordinal);

        // JSON twin: same machine, flagged back-pressured, not ready, zero tasks.
        var json = await GetAuthedAsync(app, "/dashboard/machines?format=json", ct);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains(doc.RootElement.EnumerateArray(), m =>
            m.GetProperty("machineId").GetString() == "idle-box"
            && m.GetProperty("underBackPressure").GetBoolean()
            && !m.GetProperty("ready").GetBoolean()
            && m.GetProperty("runningTasks").GetArrayLength() == 0);

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
        var (_, workingNs, workingCaller) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
            await store.RegisterServiceAsync(workingCaller, "api", 8080, ct));

        // A blocked task (open input request), carrying the question its worker asked —
        // this page is where a human answers, so the text has to be legible here (§12).
        var (blockedId, blockedNs, blockedCaller) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
            await store.ApplyAsync(blockedId,
                new RequestInput(blockedCaller, InputRequestKind.AuthHelp, BlockedQuestion), ct));

        // A parked task: block it, then let its wait TTL expire (one park).
        var (parkedId, parkedNs, parkedCaller) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
        {
            await store.ApplyAsync(parkedId, new RequestInput(parkedCaller, InputRequestKind.Question), ct);
            await store.ApplyAsync(parkedId, new WaitTtlExpired(new ParkRecord("box-1")), ct);
        });

        // A completed lead-mode task, adjudicated by the Lead (§9 check 4 provenance),
        // carrying an in-band worker report (§10).
        const string workerReport = "ran the suite (green); proposes profile gpu for follow-ups";
        var (completedId, completedNs, completedCaller) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
        {
            await store.ApplyAsync(completedId, new ReportResult(completedCaller, "git:done", workerReport), ct);
            await store.ApplyAsync(completedId, new VerdictAccept(new LeadClaim(team)), ct);
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
        Assert.Contains(completedNs, html, StringComparison.Ordinal);
        Assert.Contains("accepted by lead session", html, StringComparison.Ordinal); // §9.4 provenance rendered
        Assert.Contains(workerReport, html, StringComparison.Ordinal);               // §10 in-band report rendered
        // §8.1 (#81): the result reference the worker handed over — the artifact pointer a
        // human adjudicating in review mode rules on — is rendered, as escaped text rather
        // than a link (the plane never dereferences it).
        Assert.Contains("git:done", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"git:done\"", html, StringComparison.Ordinal);
        // §11: the question itself, plus the typed kind that says who can answer it.
        // Both the task row's Q&A disclosure and the open-input-requests table show it.
        Assert.Contains(BlockedQuestion, html, StringComparison.Ordinal);
        Assert.Contains("authhelp", html, StringComparison.Ordinal);
        Assert.Contains("Not yet answered", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not tracked", html, StringComparison.Ordinal); // the kind IS tracked now

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
            r => r.GetProperty("namespace").GetString() == blockedNs
                 // §11: the JSON twin carries the question and kind verbatim, so a tool
                 // reading the twin sees exactly what the page shows.
                 && r.GetProperty("question").GetString() == BlockedQuestion
                 && r.GetProperty("kind").GetString() == "AuthHelp");

        var tasks = root.GetProperty("tasks");
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("namespace").GetString() == parkedNs && t.GetProperty("parks").GetInt32() >= 1);
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("state").GetString() == "Parked");
        // §9 check 4: completion provenance rides the JSON twin as a structured field.
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("namespace").GetString() == completedNs
                 && t.GetProperty("completionProvenance").GetString() == "LeadSession");
        // §10: the in-band report rides the JSON twin verbatim.
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("namespace").GetString() == completedNs
                 && t.GetProperty("report").GetString() == workerReport);
        // §8.1 (#81): so does the result reference — the JSON twin was one of the surfaces
        // that read this column nowhere, so a tool reading it now sees what the page shows.
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("namespace").GetString() == completedNs
                 && t.GetProperty("resultReference").GetString() == "git:done");
        // A task no worker has reported on carries none, rather than an empty string.
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("namespace").GetString() == workingNs
                 && t.GetProperty("resultReference").ValueKind == JsonValueKind.Null);
        // §11: so does the input exchange, unanswered here.
        Assert.Contains(tasks.EnumerateArray(),
            t => t.GetProperty("namespace").GetString() == blockedNs
                 && t.GetProperty("question").GetString() == BlockedQuestion
                 && t.GetProperty("answer").ValueKind == JsonValueKind.Null);

        await app.StopAsync(ct);
    }

    /// <summary>The question a seeded blocked task asks (§11) — distinctive so the HTML
    /// and JSON assertions are unambiguous.</summary>
    private const string BlockedQuestion = "I need a read-only credential for staging-pg; who provisions that?";

    // ── (d) Human inbox: question, review, park, auth failure; empty state ─────

    [SkippableFact]
    public async Task Inbox_shows_a_question_a_review_a_park_an_auth_failure_and_an_honest_empty_state()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        // The question deliberately contains markup: it is worker-authored text on a
        // human page, so it must land escaped, never as live HTML (§12/§13).
        const string question = "should I use <script>alert(1)</script> or the sanitizer helper?";
        var teamA = TeamId.New();
        var (blockedId, blockedNs, blockedCaller) = await SeedWorkingTaskWithCallerAsync(teamA, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
            await store.ApplyAsync(blockedId, new RequestInput(blockedCaller, InputRequestKind.Question, question), ct));

        var teamB = TeamId.New();
        var (reviewId, reviewNs, reviewCaller) = await SeedWorkingTaskWithCallerAsync(teamB, CompletionMode.Review, ct);
        await WithStoreAsync(async store =>
            await store.ApplyAsync(reviewId, new ReportResult(reviewCaller, "ref://result"), ct));

        // A parked task still awaiting an answer keeps its question visible too — a park
        // is a question that outlived its lease, and it is answered from this same page.
        var teamC = TeamId.New();
        const string parkedQuestion = "the migration is destructive; confirm before I run it?";
        var (parkedId, parkedNs, parkedCaller) = await SeedWorkingTaskWithCallerAsync(teamC, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
        {
            await store.ApplyAsync(parkedId,
                new RequestInput(parkedCaller, InputRequestKind.Question, parkedQuestion), ct);
            await store.ApplyAsync(parkedId, new WaitTtlExpired(new ParkRecord("box-9")), ct);
        });

        // §11/§12 (#50): an auth failure on a live task is something only a person can
        // clear — a scope is granted, never retried into existence — so the inbox joins it.
        // Reported twice, because a retrying worker reports every attempt and the panel is
        // supposed to collapse them into one entry instead of repeating itself.
        var teamD = TeamId.New();
        const string authTarget = "github.com/acme/private-infra";
        var (authTaskId, authNs, _) = await SeedWorkingTaskWithCallerAsync(teamD, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
        {
            await store.RecordAuthFailureAsync(authTaskId, "clone", authTarget, "insufficient_scope", "repo", ct);
            await store.RecordAuthFailureAsync(authTaskId, "clone", authTarget, "insufficient_scope", "repo", ct);
        });

        // The same failure on a task that has since gone terminal is history, not an inbox
        // item: no scope a person grants changes a canceled task. The event log keeps it.
        var teamE = TeamId.New();
        const string staleTarget = "github.com/acme/abandoned";
        var (staleTaskId, _, _) = await SeedWorkingTaskWithCallerAsync(teamE, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
        {
            await store.RecordAuthFailureAsync(staleTaskId, "push", staleTarget, "forbidden", null, ct);
            await store.ApplyAsync(staleTaskId, new Cancel(new HumanSession(), CancelDisposition.Preserve), ct);
        });

        var body = await GetAuthedAsync(app, "/dashboard/inbox", ct);
        Assert.Contains("Open questions", body, StringComparison.Ordinal);
        Assert.Contains(blockedNs, body, StringComparison.Ordinal);
        Assert.Contains("Awaiting review", body, StringComparison.Ordinal);
        Assert.Contains(reviewNs, body, StringComparison.Ordinal);
        // §11/§12: the question a person is being asked to answer is on the page they
        // answer from — escaped, so the markup in it is inert.
        Assert.Contains("&lt;script&gt;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", body, StringComparison.Ordinal);
        Assert.Contains(parkedNs, body, StringComparison.Ordinal);
        Assert.Contains(parkedQuestion, body, StringComparison.Ordinal);
        // §11 auth failures are joined, not disclaimed (#80): the runner's own facts land on
        // the page, and the "not recorded" copy they were hidden behind is gone.
        Assert.Contains("Auth failures", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Not recorded", body, StringComparison.Ordinal);
        Assert.Contains(authNs, body, StringComparison.Ordinal);
        Assert.Contains(authTarget, body, StringComparison.Ordinal);
        Assert.Contains("insufficient_scope", body, StringComparison.Ordinal);
        // As its own cell, not as a substring of the page's markup: "repo" hides inside
        // class="report" on this very page, so the assertion has to name the rendered fact.
        Assert.Contains("<code>repo</code>", body, StringComparison.Ordinal);     // the missing scope
        // The scope rule: live tasks only. The canceled task's failure is absent from the
        // inbox but still on the event log, which is the full history.
        Assert.DoesNotContain(staleTarget, body, StringComparison.Ordinal);
        var eventLog = await GetAuthedAsync(app, "/dashboard/events", ct);
        Assert.Contains(staleTarget, eventLog, StringComparison.Ordinal);
        // §11's permission bridge gave this section a source, so its empty state is now the
        // ordinary kind — "nothing pending" rather than "not implemented". The populated
        // section and its answer form are covered by PermissionBridgeDashboardTests.
        Assert.Contains("Permission requests", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Not built yet", body, StringComparison.Ordinal);
        Assert.Contains("No pending permission requests", body, StringComparison.Ordinal);

        // JSON twin carries the same items, question text included.
        var json = await GetAuthedAsync(app, "/dashboard/inbox?format=json", ct);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains(doc.RootElement.GetProperty("questions").EnumerateArray(),
            q => q.GetProperty("namespace").GetString() == blockedNs
                 && q.GetProperty("question").GetString() == question
                 && q.GetProperty("kind").GetString() == "Question");
        Assert.Contains(doc.RootElement.GetProperty("awaitingReview").EnumerateArray(),
            r => r.GetProperty("namespace").GetString() == reviewNs);
        Assert.Contains(doc.RootElement.GetProperty("parked").EnumerateArray(),
            p => p.GetProperty("namespace").GetString() == parkedNs
                 && p.GetProperty("question").GetString() == parkedQuestion);

        // The auth failure travels as structured fields, the two reports collapsed into the
        // one problem they describe — and the canceled task's failure is not here either.
        var failures = doc.RootElement.GetProperty("authFailures").EnumerateArray().ToList();
        var authEntries = failures
            .Where(f => f.GetProperty("namespace").GetString() == authNs)
            .ToList();
        var failure = Assert.Single(authEntries);        // both reports, collapsed into one entry
        Assert.Equal("clone", failure.GetProperty("operation").GetString());
        Assert.Equal(authTarget, failure.GetProperty("target").GetString());
        Assert.Equal("insufficient_scope", failure.GetProperty("errorCode").GetString());
        Assert.Equal("repo", failure.GetProperty("missingScope").GetString());
        Assert.Equal(2, failure.GetProperty("occurrences").GetInt32());
        Assert.Equal("Working", failure.GetProperty("state").GetString());
        Assert.DoesNotContain(failures, f => f.GetProperty("target").GetString() == staleTarget);

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
        await SeedWorkingTaskAsync(team, CompletionMode.Lead, ct); // created + Dispatch task_events

        var body = await GetAuthedAsync(app, "/dashboard/events", ct);
        Assert.Contains("Claimed", body, StringComparison.Ordinal);   // lead event
        Assert.Contains("Dispatch", body, StringComparison.Ordinal);  // a task transition
        Assert.Contains("Working", body, StringComparison.Ordinal);   // its target state badge

        await app.StopAsync(ct);
    }

    // ── (e2) Event log: derived-telemetry kinds render in HTML and JSON (#50) ──

    [SkippableFact]
    public async Task Event_log_renders_auth_failure_subagent_and_input_kind_in_html_and_json()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);

        var team = TeamId.New();

        // A task that hits an auth failure and spawns a subagent (both out-of-band,
        // no transition), and a task that blocks with a typed input-request kind.
        var (authTaskId, _, _) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
        {
            await store.RecordAuthFailureAsync(
                authTaskId, "clone", "github.com/acme/repo", "insufficient_scope", "repo", ct);
            await store.RecordSubagentSpawnAsync(authTaskId, "sub-1", "root", ct);
        });

        var (blockedId, _, blockedCaller) = await SeedWorkingTaskWithCallerAsync(team, CompletionMode.Lead, ct);
        await WithStoreAsync(async store =>
            await store.ApplyAsync(blockedId, new RequestInput(blockedCaller, InputRequestKind.AuthHelp), ct));

        // ── HTML: each kind's detail renders on the events page ──────────────
        var html = await GetAuthedAsync(app, "/dashboard/events", ct);
        Assert.Contains("auth-failed", html, StringComparison.Ordinal);
        Assert.Contains("insufficient_scope", html, StringComparison.Ordinal);   // the auth error code
        Assert.Contains("subagent-spawned", html, StringComparison.Ordinal);
        Assert.Contains("sub-1", html, StringComparison.Ordinal);                // the subagent id
        Assert.Contains("AuthHelp", html, StringComparison.Ordinal);             // the typed input-request kind

        // ── JSON twin: the same facts as structured fields ──────────────────
        var json = await GetAuthedAsync(app, "/dashboard/events?format=json", ct);
        using var doc = JsonDocument.Parse(json);
        var events = doc.RootElement.EnumerateArray().ToList();

        Assert.Contains(events, e =>
            e.GetProperty("kind").GetString() == "auth-failed"
            && e.GetProperty("authOperation").GetString() == "clone"
            && e.GetProperty("authErrorCode").GetString() == "insufficient_scope"
            && e.GetProperty("authMissingScope").GetString() == "repo");
        Assert.Contains(events, e =>
            e.GetProperty("kind").GetString() == "subagent-spawned"
            && e.GetProperty("subagentId").GetString() == "sub-1"
            && e.GetProperty("subagentParentId").GetString() == "root");
        Assert.Contains(events, e =>
            e.TryGetProperty("inputKind", out var k) && k.GetString() == "AuthHelp");

        await app.StopAsync(ct);
    }

    // ── Host + seeding helpers ────────────────────────────────────────────────

    /// <summary>The operator passphrase a configured verifier accepts, and its SHA-256 hex.</summary>
    private const string OperatorPassphrase = "correct-operator-passphrase";
    private static string OperatorPassphraseHash =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatorPassphrase)));

    /// <param name="operatorPassphraseHash">The SHA-256 hex the operator verifier
    /// compares against; null leaves the verifier unconfigured (fail-closed 503).</param>
    private WebApplication BuildPlane(string? operatorPassphraseHash = null)
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
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        builder.Services.AddSingleton<IOperatorVerifier>(new ConfiguredOperatorVerifier(operatorPassphraseHash));
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

    /// <summary>Extracts the <c>docket_session</c> value from a response's Set-Cookie.</summary>
    private static string SessionCookieValue(HttpResponseMessage res)
    {
        var prefix = DashboardAuth.CookieName + "=";
        var header = res.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(prefix, StringComparison.Ordinal));
        return header.Split(';')[0][prefix.Length..];
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
