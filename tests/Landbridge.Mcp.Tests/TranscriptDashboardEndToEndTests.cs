using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp;
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
/// The §12 transcript surface over real HTTP: the human-only dashboard credential, a live
/// session's snapshot and tail, the verbatim stream with its warning, and the clean answers
/// for an offline machine. A fake machine answers <c>read-transcript</c> through the real
/// <see cref="RunnerEventSink"/>, so the whole request path — endpoint, relay, rendezvous,
/// sink — runs exactly as in production.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TranscriptDashboardEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly Guid MachineId = TestMachineIds.For("box-1");

    private static readonly MachineSnapshot Snapshot =
        new(MachineId, Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    /// <summary>The transcript a fake machine serves, credential and all — verbatim serving
    /// is the documented behavior, so the test asserts it rather than avoiding it.</summary>
    private const string TranscriptText =
        """
        {"type":"system","subtype":"init","session_id":"s1"}
        {"type":"assistant","text":"exporting TOKEN=lbr_w_deadbeefcafe1234"}
        {"type":"result","subtype":"success"}
        """;

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── The human-only rule ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_lead_token_is_refused_while_the_same_token_still_reads_its_own_team()
    {
        // The one dashboard route family that refuses a Lead credential outright. Every other
        // §12 view must keep answering one (§12 requires a Lead-consumable twin) — but with the
        // Lead's owned Teams, not the instance (§4 reattachment; §10 as-built gives an agent no
        // cross-Team view). So this asserts all three halves: transcripts refused, the Team list
        // served, and another Team's row absent from it. A regression in any one of them —
        // transcripts widened, the twin removed, or the twin widened back to instance-wide —
        // would otherwise look like a pass.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();
        var somebodyElse = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var task = await SeedCompletedTaskAsync(team, ct);
        await SeedWorkingTaskAsync(somebodyElse, ct);
        var leadToken = await IssueLeadTokenAsync(team, ct);

        using var client = Client(app);
        var transcripts = await GetAsync(client, $"/dashboard/sessions/{task.Value}/transcripts", leadToken, ct);
        var stream = await GetAsync(
            client, $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            leadToken, ct);
        var teamsView = await GetAsync(client, "/dashboard/teams?format=json", leadToken, ct);

        Assert.Equal(HttpStatusCode.Forbidden, transcripts.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stream.StatusCode);
        Assert.Contains("human operator", await transcripts.Content.ReadAsStringAsync(ct));

        // Same credential, the reattachment view: served, and scoped to the Team the credential
        // is for. The other Team exists and is busy; this Lead has no business seeing that.
        Assert.Equal(HttpStatusCode.OK, teamsView.StatusCode);
        using var teams = JsonDocument.Parse(await teamsView.Content.ReadAsStringAsync(ct));
        var only = Assert.Single(teams.RootElement.EnumerateArray());
        Assert.Equal(team.Value, only.GetProperty("teamId").GetGuid());
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

        var res = await client.GetAsync($"/dashboard/sessions/{Guid.NewGuid()}/transcripts", ct);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("/dashboard/login", res.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    // ── A live session is readable ────────────────────────────────────────────

    [SkippableFact]
    public async Task A_working_task_streams_a_snapshot_and_is_linked_on_the_team_view()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var (task, _) = await SeedWorkingTaskAsync(team, ct);
        RegisterFakeMachine(app, "still running\n", rangeBytes: 256);
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);
        var res = await GetAsync(
            client, $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            human, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        var teamPage = await GetAsync(client, $"/dashboard/teams/{team.Value}", human, ct);
        var teamHtml = await teamPage.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("still running", body);
        Assert.DoesNotContain("interrupted", body);
        await using var db = pg.NewContext();
        var slug = await db.Sessions.AsNoTracking().Where(s => s.Id == task.Value).Select(s => s.Slug).SingleAsync(ct);
        Assert.Contains($"/dashboard/sessions/{slug}/transcripts", teamHtml);
        Assert.DoesNotContain("readable once the task reaches a terminal state", teamHtml);
    }

    [SkippableFact]
    public async Task Follow_on_a_running_task_picks_up_bytes_written_while_it_waits()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var (task, caller) = await SeedWorkingTaskAsync(team, ct);
        var held = new TranscriptBytes("hello");
        RegisterFakeMachine(app, held, rangeBytes: 256);
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);
        client.Timeout = TimeSpan.FromSeconds(30);
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout&follow=1");
        req.Headers.Add("Cookie", $"{DashboardAuth.CookieName}={human}");
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buf = new byte[1024];
        var body = new StringBuilder();
        var appended = false;
        while (true)
        {
            var n = await stream.ReadAsync(buf, ct);
            if (n == 0)
                break;
            body.Append(Encoding.UTF8.GetString(buf, 0, n));
            if (!appended && body.ToString().Contains("hello", StringComparison.Ordinal))
            {
                // The first range has been flushed. The server is in its one-second wait
                // before the next read, and the session is still non-terminal. Append, then
                // accept, so the next read sees the new bytes and the confirm read can stop.
                appended = true;
                held.Append("world");
                await AcceptAsync(task, caller, team, ct);
            }
        }

        var text = body.ToString();
        Assert.True(appended, text);
        Assert.Contains("hello", text);
        Assert.Contains("world", text);
        Assert.DoesNotContain("interrupted", text);
    }

    [SkippableFact]
    public async Task Follow_on_a_completed_task_finishes_without_polling()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();

        await using var app = BuildPlane();
        await app.StartAsync(ct);
        var task = await SeedCompletedTaskAsync(team, ct);
        RegisterFakeMachine(app, TranscriptText, rangeBytes: 16);
        var human = await IssueHumanTokenAsync(ct);

        using var client = Client(app);
        client.Timeout = TimeSpan.FromSeconds(20);
        var res = await GetAsync(
            client,
            $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout&follow=1",
            human, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("lbr_w_deadbeefcafe1234", body);
        Assert.DoesNotContain("interrupted", body);
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
            client, $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            human, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/plain", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", res.Headers.CacheControl!.ToString());
        Assert.Contains("nosniff", res.Headers.GetValues("X-Content-Type-Options").First());

        // The warning leads the BODY, not just the HTML chrome, so it survives a copy-paste.
        Assert.StartsWith("[landbridge] Raw harness output, served verbatim.", body);
        Assert.Contains("does not redact", body);
        // Verbatim: the planted credential is present, byte for byte, reassembled across ranges.
        Assert.Contains(TranscriptText, body);
        Assert.Contains("lbr_w_deadbeefcafe1234", body);
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
        var res = await GetAsync(client, $"/dashboard/sessions/{task.Value}/transcripts", human, ct);
        var html = await res.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(MachineId.ToString(), html);
        Assert.Contains("machine connected", html);
        Assert.Contains($"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&amp;ordinal=1", html);
        Assert.Contains("follow=1", html);
        // A 5s meta-refresh here would re-ask every machine for an inventory over the control
        // channel every five seconds — every other §12 view has one; this must not.
        Assert.DoesNotContain("http-equiv=\"refresh\"", html);
        // The page links to the raw stream and never renders transcript bytes itself.
        Assert.DoesNotContain("lbr_w_deadbeefcafe1234", html);
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
        var index = await GetAsync(client, $"/dashboard/sessions/{task.Value}/transcripts", human, ct);
        var indexHtml = await index.Content.ReadAsStringAsync(ct);
        var stream = await GetAsync(
            client, $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
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
        var noMachine = await GetAsync(client, $"/dashboard/sessions/{task.Value}/transcript?ordinal=1", human, ct);
        var badOrdinal = await GetAsync(
            client, $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=0", human, ct);
        var badStream = await GetAsync(
            client, $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=../etc/passwd",
            human, ct);

        Assert.Equal(HttpStatusCode.BadRequest, noMachine.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badOrdinal.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badStream.StatusCode);
        Assert.Empty(sent);
    }

    [SkippableFact]
    public async Task A_dashboard_with_core_url_reads_the_transcript_only_core_can_reach()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        var team = TeamId.New();
        var other = TeamId.New();

        await using var core = BuildCore();
        await core.StartAsync(ct);
        var coreUrl = core.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

        await using var dash = BuildPlane(coreUrl);
        await dash.StartAsync(ct);
        var task = await SeedCompletedTaskAsync(team, ct);
        await SeedSpokeAsync(ct);
        RegisterFakeMachine(core, TranscriptText, rangeBytes: 256);
        var human = await IssueHumanTokenAsync(ct);
        var lead = await IssueLeadTokenAsync(team, ct);
        var stranger = await IssueLeadTokenAsync(other, ct);

        using var client = Client(dash);
        var index = await GetAsync(client, $"/dashboard/sessions/{task.Value}/transcripts", human, ct);
        var html = await index.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("machine connected", html);
        Assert.Contains($"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}", html);

        var stream = await GetAsync(
            client, $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            human, ct);
        var body = await stream.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Contains("lbr_w_deadbeefcafe1234", body);

        var leadOnDash = await GetAsync(
            client, $"/dashboard/sessions/{task.Value}/transcript?machine={MachineId}&ordinal=1&stream=stdout",
            lead, ct);
        Assert.Equal(HttpStatusCode.Forbidden, leadOnDash.StatusCode);

        using var coreClient = Client(core);
        var path = $"/core/v1/sessions/{task.Value}/transcript?machine={MachineId:D}&ordinal=1&stream=stdout&offset=0&maxBytes=4096";
        var owned = await GetBearerAsync(coreClient, path, lead, ct);
        Assert.Equal(HttpStatusCode.OK, owned.StatusCode);
        Assert.Contains("lbr_w_deadbeefcafe1234", await owned.Content.ReadAsStringAsync(ct));
        var denied = await GetBearerAsync(coreClient, path, stranger, ct);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a machine that serves <paramref name="content"/> from memory, answering each
    /// <c>read-transcript</c> through the real event sink. Returns the commands it received.
    /// </summary>
    private static List<ReadTranscriptCommand> RegisterFakeMachine(
        WebApplication app, string content, int rangeBytes) =>
        RegisterFakeMachine(app, new TranscriptBytes(content), rangeBytes);

    private static List<ReadTranscriptCommand> RegisterFakeMachine(
        WebApplication app, TranscriptBytes held, int rangeBytes)
    {
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = app.Services.GetRequiredService<RunnerEventSink>();
        var sent = new List<ReadTranscriptCommand>();

        registry.Register(MachineId, new HashSet<string> { "default" }, async (command, ct) =>
        {
            if (command is not ReadTranscriptCommand read)
                return;
            sent.Add(read);
            var bytes = held.Snapshot();

            TranscriptChunkEvent reply;
            if (read.Ordinal == 0)
            {
                reply = new TranscriptChunkEvent(
                    read.Session, read.RequestId, Eof: true,
                    Instances: [new TranscriptInstance(1, bytes.Length, 0, DateTimeOffset.UtcNow)]);
            }
            else
            {
                var offset = (int)Math.Min(read.Offset, bytes.Length);
                var take = Math.Min(rangeBytes, bytes.Length - offset);
                reply = new TranscriptChunkEvent(
                    read.Session, read.RequestId,
                    Text: Encoding.UTF8.GetString(bytes, offset, take),
                    NextOffset: offset + take,
                    Eof: offset + take >= bytes.Length);
            }
            await sink.HandleAsync(reply, ct);
        });
        return sent;
    }

    /// <summary>Bytes the fake machine serves. <see cref="Append"/> publishes a new array;
    /// a read in flight keeps the array it already snapshotted.</summary>
    private sealed class TranscriptBytes
    {
        private byte[] _bytes;
        public TranscriptBytes(string text) => _bytes = Encoding.UTF8.GetBytes(text);
        public void Append(string text)
        {
            var extra = Encoding.UTF8.GetBytes(text);
            var next = new byte[_bytes.Length + extra.Length];
            Buffer.BlockCopy(_bytes, 0, next, 0, _bytes.Length);
            Buffer.BlockCopy(extra, 0, next, _bytes.Length, extra.Length);
            Volatile.Write(ref _bytes, next);
        }
        public byte[] Snapshot() => Volatile.Read(ref _bytes);
    }

    private WebApplication BuildPlane(string? coreUrl = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (!string.IsNullOrEmpty(coreUrl))
        {
            builder.Configuration["Landbridge:CoreUrl"] = coreUrl;
            builder.Services.AddLandbridgeCoreWrite();
        }

        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddDashboard();
        builder.Services.AddSingleton(TimeProvider.System);
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
        app.MapDashboardTranscripts();
        return app;
    }

    private WebApplication BuildCore()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ConnectionStrings:Landbridge"] = pg.ConnectionString;
        builder.Configuration["Landbridge:PublicMcpUrl"] = "https://mcp.example.com";
        builder.Configuration["Landbridge:AuthUrl"] = "https://auth.example.com";
        builder.AddPlane();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCoreWrites();
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

    private static async Task<HttpResponseMessage> GetBearerAsync(
        HttpClient client, string path, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private async Task<(SessionId Id, WorkerCaller Caller)> SeedWorkingTaskAsync(
        TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(new CreateSession(new LeadClaim(team), team, "criteria", Profile: "default"));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Snapshot, instance, ct);
        return (created.Session.Id, new WorkerCaller(team, created.Session.Id, instance));
    }

    /// <summary>Create → dispatch → report → accept, so the instance row records the machine
    /// and the task is terminal — the state a transcript is readable in.</summary>
    private async Task<SessionId> SeedCompletedTaskAsync(TeamId team, CancellationToken ct)
    {
        var (id, caller) = await SeedWorkingTaskAsync(team, ct);
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        await store.ApplyAsync(id, new ReportResult(caller, "result-ref"), ct);
        await store.ApplyAsync(id, new VerdictAccept(new LeadClaim(team)), ct);
        return id;
    }

    private async Task AcceptAsync(SessionId id, WorkerCaller caller, TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        await store.ApplyAsync(id, new ReportResult(caller, "result-ref"), ct);
        await store.ApplyAsync(id, new VerdictAccept(new LeadClaim(team)), ct);
    }

    /// <summary>
    /// A machine row the dashboard can see as connected without a local runner registry.
    /// <see cref="MachineRow.TranscriptsServable"/> is true so Core asks it instead of
    /// refusing from the heartbeat column.
    /// </summary>
    private async Task SeedSpokeAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        db.Machines.Add(new MachineRow
        {
            Id = MachineId,
            Name = "box-1",
            Os = "linux",
            Slug = $"box-{Guid.NewGuid():N}"[..20],
            EnrolledAt = DateTimeOffset.UtcNow,
            LastSpokeAt = DateTimeOffset.UtcNow,
            TranscriptsServable = true,
        });
        await db.SaveChangesAsync(ct);
    }
}
