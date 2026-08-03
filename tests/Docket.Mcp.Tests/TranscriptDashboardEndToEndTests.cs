using System.Net;
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
/// The §12 transcript surface over real HTTP: the human-only credential rule, the
/// terminal-state gate as the operator meets it, the verbatim stream with its warning, and
/// the clean answers for an offline machine. A fake machine answers <c>read-transcript</c>
/// through the real <see cref="RunnerEventSink"/>, so the whole request path — endpoint,
/// relay, rendezvous, sink — runs exactly as in production.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TranscriptDashboardEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string MachineId = "box-1";

    private static readonly MachineSnapshot Snapshot =
        new(MachineId, Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    /// <summary>The transcript a fake machine serves, credential and all — verbatim serving
    /// is the documented behavior, so the test asserts it rather than avoiding it.</summary>
    private const string TranscriptText =
        """
        {"type":"system","subtype":"init","session_id":"s1"}
        {"type":"assistant","text":"exporting TOKEN=dkt_w_deadbeefcafe1234"}
        {"type":"result","subtype":"success"}
        """;

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── The human-only rule ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_lead_token_is_refused_while_the_same_token_still_reads_other_views()
    {
        // The one dashboard route family that refuses a Lead credential. Every other §12 view
        // must keep accepting one (§12 requires a Lead-consumable twin), so this asserts BOTH
        // halves — a regression that widened transcripts would otherwise look like a pass.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var task = await SeedCompletedTaskAsync(team, ct);
        var leadToken = await IssueLeadTokenAsync(team, ct);

        using var client = Client(app);
        var transcripts = await GetAsync(client, $"/dashboard/tasks/{task.Value}/transcripts", leadToken, ct);
        var stream = await GetAsync(
            client, $"/dashboard/tasks/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            leadToken, ct);
        var teamsView = await GetAsync(client, "/dashboard/teams?format=json", leadToken, ct);

        Assert.Equal(HttpStatusCode.Forbidden, transcripts.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stream.StatusCode);
        Assert.Contains("human operator", await transcripts.Content.ReadAsStringAsync(ct));
        // Same credential, other view: still fine.
        Assert.Equal(HttpStatusCode.OK, teamsView.StatusCode);
    }

    [SkippableFact]
    public async Task An_unauthenticated_request_is_sent_to_the_login_page()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        using var client = Client(app);

        var res = await client.GetAsync($"/dashboard/tasks/{Guid.NewGuid()}/transcripts", ct);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/dashboard/login", res.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // ── The terminal gate, as the operator meets it ────────────────────────────

    [SkippableFact]
    public async Task A_working_task_is_refused_with_the_reason_and_is_not_linked_on_the_team_view()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var (task, _) = await SeedWorkingTaskAsync(team, ct);
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);
        var res = await GetAsync(
            client, $"/dashboard/tasks/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            human, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        var teamPage = await GetAsync(client, $"/dashboard/teams/{team.Value}", human, ct);
        var teamHtml = await teamPage.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("terminal state", body);
        // And the Team view does not offer a link it would then refuse.
        Assert.DoesNotContain($"/dashboard/tasks/{task.Value}/transcripts", teamHtml);
        Assert.Contains("not yet", teamHtml);
    }

    // ── Serving ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_completed_task_streams_verbatim_behind_the_warning()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var task = await SeedCompletedTaskAsync(team, ct);
        // Small ranges so the endpoint's cursor loop runs many times, as it would on a real
        // multi-megabyte transcript.
        RegisterFakeMachine(app, TranscriptText, rangeBytes: 16);
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);
        var res = await GetAsync(
            client, $"/dashboard/tasks/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            human, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/plain", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", res.Headers.CacheControl!.ToString());
        Assert.Contains("nosniff", res.Headers.GetValues("X-Content-Type-Options").First());

        // The warning leads the BODY, not just the HTML chrome, so it survives a copy-paste.
        Assert.StartsWith("[docket] Raw harness output, served verbatim.", body);
        Assert.Contains("does not redact", body);
        // Verbatim: the planted credential is present, byte for byte, reassembled across ranges.
        Assert.Contains(TranscriptText, body);
        Assert.Contains("dkt_w_deadbeefcafe1234", body);
        Assert.DoesNotContain("interrupted", body);
    }

    [SkippableFact]
    public async Task The_index_lists_what_the_machine_holds_and_never_auto_refreshes()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var task = await SeedCompletedTaskAsync(team, ct);
        RegisterFakeMachine(app, TranscriptText, rangeBytes: 256);
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);
        var res = await GetAsync(client, $"/dashboard/tasks/{task.Value}/transcripts", human, ct);
        var html = await res.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(MachineId, html);
        Assert.Contains("machine connected", html);
        Assert.Contains($"/dashboard/tasks/{task.Value}/transcript?machine={MachineId}&amp;ordinal=1", html);
        // A 5s meta-refresh here would re-ask every machine for an inventory over the control
        // channel every five seconds — every other §12 view has one; this must not.
        Assert.DoesNotContain("http-equiv=\"refresh\"", html);
        // The page links to the raw stream and never renders transcript bytes itself.
        Assert.DoesNotContain("dkt_w_deadbeefcafe1234", html);
    }

    [SkippableFact]
    public async Task An_offline_machine_gives_a_clean_answer_on_both_surfaces()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var task = await SeedCompletedTaskAsync(team, ct);
        // No machine registered: the dispatch is recorded, the machine is gone.
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);
        var index = await GetAsync(client, $"/dashboard/tasks/{task.Value}/transcripts", human, ct);
        var indexHtml = await index.Content.ReadAsStringAsync(ct);
        var stream = await GetAsync(
            client, $"/dashboard/tasks/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            human, ct);

        // The index still renders (the dispatch history is plane-side) and says why.
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("machine offline", indexHtml);
        // The stream is a clear 503, never a hang.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stream.StatusCode);
        Assert.Contains("not connected", await stream.Content.ReadAsStringAsync(ct));
    }

    [SkippableFact]
    public async Task A_malformed_stream_request_is_refused_before_any_machine_is_asked()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var task = await SeedCompletedTaskAsync(team, ct);
        var sent = RegisterFakeMachine(app, TranscriptText, rangeBytes: 256);
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);
        var noMachine = await GetAsync(client, $"/dashboard/tasks/{task.Value}/transcript?ordinal=1", human, ct);
        var badOrdinal = await GetAsync(
            client, $"/dashboard/tasks/{task.Value}/transcript?machine={MachineId}&ordinal=0", human, ct);
        var badStream = await GetAsync(
            client, $"/dashboard/tasks/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=../etc/passwd",
            human, ct);

        Assert.Equal(HttpStatusCode.BadRequest, noMachine.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badOrdinal.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badStream.StatusCode);
        Assert.Empty(sent);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a machine that serves <paramref name="content"/> from memory, answering each
    /// <c>read-transcript</c> through the real event sink. Returns the commands it received.
    /// </summary>
    private static List<ReadTranscriptCommand> RegisterFakeMachine(
        WebApplication app, string content, int rangeBytes)
    {
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = app.Services.GetRequiredService<RunnerEventSink>();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var sent = new List<ReadTranscriptCommand>();

        registry.Register(MachineId, new HashSet<string> { "default" }, async (command, ct) =>
        {
            if (command is not ReadTranscriptCommand read)
                return;
            sent.Add(read);

            TranscriptChunkEvent reply;
            if (read.Ordinal == 0)
            {
                reply = new TranscriptChunkEvent(
                    read.Task, read.RequestId, Eof: true,
                    Instances: [new TranscriptInstance(1, bytes.Length, 0, DateTimeOffset.UtcNow)]);
            }
            else
            {
                var offset = (int)Math.Min(read.Offset, bytes.Length);
                var take = Math.Min(rangeBytes, bytes.Length - offset);
                reply = new TranscriptChunkEvent(
                    read.Task, read.RequestId,
                    Text: System.Text.Encoding.UTF8.GetString(bytes, offset, take),
                    NextOffset: offset + take,
                    Eof: offset + take >= bytes.Length);
            }
            await sink.HandleAsync(reply, ct);
        });
        registry.ApplyHeartbeat(MachineId, new MachineHeartbeat(
            MachineId, Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningTasks: 0, Profiles: ["default"], At: DateTimeOffset.UtcNow, TranscriptsServable: true));
        return sent;
    }

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
        builder.Services.AddDocketForwarding();
        builder.Services.AddSingleton<RunnerEventSink>();
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
        app.MapDashboardTranscripts();
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
        return ((LeadClaimResult.Claimed)claim).Token.Token;
    }

    private async Task<(TaskId Id, WorkerCaller Caller)> SeedWorkingTaskAsync(
        TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(new CreateTask(
            new LeadClaim(team), team, "criteria", CompletionMode.Lead, Profile: null, TeamBudgetRemains: true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Snapshot, instance, ct);
        return (created.Task.Id, new WorkerCaller(team, created.Task.Id, instance));
    }

    /// <summary>Create → dispatch → report → accept, so the instance row records the machine
    /// and the task is terminal — the state a transcript is readable in.</summary>
    private async Task<TaskId> SeedCompletedTaskAsync(TeamId team, CancellationToken ct)
    {
        var (id, caller) = await SeedWorkingTaskAsync(team, ct);
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        await store.ApplyAsync(id, new ReportResult(caller, "result-ref"), ct);
        await store.ApplyAsync(id, new VerdictAccept(new LeadClaim(team)), ct);
        return id;
    }
}
