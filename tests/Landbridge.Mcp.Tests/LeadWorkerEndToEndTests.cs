using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The payoff (spec §10): a Lead creates a task over a real MCP connection and a
/// dispatched worker acts on it over its own — end to end, over the wire, with
/// the actual opaque-token auth handler in the loop.
///
/// The server is the real <c>landbridge-mcp</c> pipeline (auth scheme, both tool
/// sets, MapMcp().RequireAuthorization()) hosted on a loopback Kestrel port and
/// driven by the MCP C# SDK client. Dispatch itself is invoked directly on the
/// store — that is control-plane-internal by design: workers are dispatched,
/// never claimants, and there is deliberately no claim_task tool (§5, §10).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LeadWorkerEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Lead_creates_a_task_over_mcp_and_a_worker_reports()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var team = TeamId.New();

        // A human session claims the Lead of the Team, through the same seam the OAuth
        // callback drives (§5) — minted directly here so the test stays headless.
        string leadToken;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, team, ct: ct));
            leadToken = claim.Token.Token;
        }

        // ── Lead: create a task over MCP ────────────────────────────────────
        SessionId sessionId;
        await using (var lead = await ConnectAsync(baseUri, leadToken, ct))
        {
            var created = await lead.CallToolAsync("create_session", new Dictionary<string, object?>
            {
                ["description"] = "make the suite pass",
                ["profile"] = "default",
            }, cancellationToken: ct);

            Assert.NotEqual(true, created.IsError);
            var idText = Assert.Single(created.Content.OfType<TextContentBlock>()).Text;
            sessionId = new SessionId(Guid.Parse(idText));
        }

        // ── Control plane: dispatch mints the incumbent worker instance ─────
        WorkerInstanceId instance = WorkerInstanceId.New();
        string workerToken;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, TimeProvider.System);
            var machine = new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });
            var dispatched = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(machine, instance, ct));
            Assert.Equal(sessionId, dispatched.Session.Id);

            var tokens = new TokenService(db, TimeProvider.System);
            workerToken = (await tokens.MintWorkerTokenAsync(team, sessionId, instance, ct)).Token;
        }

        // ── Worker: report the result over its own MCP connection ───────────
        await using (var worker = await ConnectAsync(baseUri, workerToken, ct))
        {
            var reported = await worker.CallToolAsync("report_result", new Dictionary<string, object?>
            {
                ["resultReference"] = "git:branch/done",
            }, cancellationToken: ct);

            Assert.NotEqual(true, reported.IsError);
            Assert.Contains("Working", Assert.Single(reported.Content.OfType<TextContentBlock>()).Text);
        }

        // ── The record moved through the state machine, not around it ───────
        await using (var v = pg.NewContext())
        {
            var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value, ct);
            Assert.Equal(SessionState.Working, row.State);
            Assert.True(row.ReportUnread);
            Assert.Equal(MessageState.Idle, row.MessageState);
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_worker_asks_over_mcp_the_lead_reads_and_answers_and_the_successor_receives_it()
    {
        // §10/§11 over the wire: the whole human-in-the-loop loop through real MCP tool
        // calls — request_input(question) → get_team_state (kind + flag, no prose) →
        // get_session_question (delimited) → answer_input_request(answer) → the
        // redispatched worker's get_session carries the answer. This is the round trip the
        // park/resume machinery existed for and could not previously complete.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var team = TeamId.New();
        const string question = "the schema has two candidate keys; which one should the index use?";
        const string answer = "use (tenant_id, created_at) — the other one is not unique under backfill.";

        string leadToken;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, team, ct: ct));
            leadToken = claim.Token.Token;
        }

        SessionId sessionId;
        await using (var lead = await ConnectAsync(baseUri, leadToken, ct))
        {
            var created = await lead.CallToolAsync("create_session", new Dictionary<string, object?>
            {
                ["description"] = "add the index",
                ["profile"] = "default",
            }, cancellationToken: ct);
            sessionId = new SessionId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
        }

        var machine = new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

        // ── Worker: ask, in words ───────────────────────────────────────────
        string firstWorkerToken;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, TimeProvider.System);
            var instance = WorkerInstanceId.New();
            await store.DispatchNextAsync(machine, instance, ct);
            firstWorkerToken = (await new TokenService(db, TimeProvider.System)
                .MintWorkerTokenAsync(team, sessionId, instance, ct)).Token;
        }
        await using (var worker = await ConnectAsync(baseUri, firstWorkerToken, ct))
        {
            var asked = await worker.CallToolAsync("request_input", new Dictionary<string, object?>
            {
                ["kind"] = "question",
                ["question"] = question,
            }, cancellationToken: ct);

            Assert.NotEqual(true, asked.IsError);
            Assert.Contains("Working", Assert.Single(asked.Content.OfType<TextContentBlock>()).Text);
        }

        // ── Lead: see it in the poll, read it, answer it ─────────────────────
        await using (var lead = await ConnectAsync(baseUri, leadToken, ct))
        {
            var state = await lead.CallToolAsync("get_team_state", new Dictionary<string, object?>(), cancellationToken: ct);
            var stateText = Assert.Single(state.Content.OfType<TextContentBlock>()).Text;
            // §10: the poll shows WHICH task needs attention and WHAT KIND, never the
            // prose — the whole reason the text has its own deliberate read.
            Assert.Contains("hasQuestion", stateText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(question, stateText, StringComparison.Ordinal);

            var read = await lead.CallToolAsync("get_lead_inbox", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId.ToString(),
            }, cancellationToken: ct);
            var readText = Assert.Single(read.Content.OfType<TextContentBlock>()).Text;
            Assert.Contains(question, readText, StringComparison.Ordinal);

            var answered = await lead.CallToolAsync("send_input_response", new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId.ToString(),
                ["answer"] = answer,
            }, cancellationToken: ct);
            Assert.NotEqual(true, answered.IsError);
            Assert.Contains("Working", Assert.Single(answered.Content.OfType<TextContentBlock>()).Text);
        }

        // ── The successor worker: get_session carries the answer ────────────────
        string successorToken;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, TimeProvider.System);
            var successor = WorkerInstanceId.New();
            var dispatched = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(machine, successor, ct));
            Assert.Equal(sessionId, dispatched.Session.Id);
            successorToken = (await new TokenService(db, TimeProvider.System)
                .MintWorkerTokenAsync(team, sessionId, successor, ct)).Token;
        }
        await using (var worker = await ConnectAsync(baseUri, successorToken, ct))
        {
            var assignment = await worker.CallToolAsync(
                "get_inbox", new Dictionary<string, object?>(), cancellationToken: ct);
            var text = Assert.Single(assignment.Content.OfType<TextContentBlock>()).Text;

            // Parsed, not substring-matched: the wire escapes non-ASCII (the answer's
            // em dash arrives as —), and the pinned snake_case field names are
            // themselves part of the contract a worker harness reads.
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal(answer, doc.RootElement.GetProperty("answer").GetString());
            Assert.Equal(question, doc.RootElement.GetProperty("question").GetString());
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_worker_token_cannot_reach_the_lead_create_session_tool()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var team = TeamId.New();

        // A worker token authenticates (it is a valid principal) but carries no
        // lead claim, so create_session must refuse it — authority is structural (§5).
        string workerToken;
        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, TimeProvider.System);
            var created = (StoreResult.Applied)await store.CreateAsync(
                new CreateSession(new LeadClaim(team), team, "seed", "default"), ct);
            var instance = WorkerInstanceId.New();
            await store.DispatchNextAsync(
                new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance, ct);
            var tokens = new TokenService(db, TimeProvider.System);
            workerToken = (await tokens.MintWorkerTokenAsync(team, created.Session.Id, instance, ct)).Token;
        }

        await using var worker = await ConnectAsync(baseUri, workerToken, ct);
        var result = await worker.CallToolAsync("create_session", new Dictionary<string, object?>
        {
            ["description"] = "should not happen",
            ["profile"] = "default",
        }, cancellationToken: ct);

        // The tool throws (no lead claim); the SDK surfaces it as a tool error.
        Assert.Equal(true, result.IsError);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task List_profiles_answers_a_lead_over_mcp_and_refuses_a_worker_token()
    {
        // §7/§10 over the wire, both halves against the real auth handler: the Lead's
        // routing read arrives as structured data naming the fleet's profiles and the
        // machines offering them, and the same tool refuses a worker token — authority is
        // structural (§5), so the refusal is the credential's, not a check the tool chose
        // to make for this caller.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var team = TeamId.New();

        // Two machines dial in and heartbeat, exactly as the runner endpoint would: this is
        // the same singleton registry a dispatch pass reads, so what the tool reports below
        // is what routing would match.
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();
        registry.Register("m1", new HashSet<string> { "default", "gpu" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("m1", Heartbeat("m1", "default", "gpu"));
        registry.Register("m2", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("m2", Heartbeat("m2", "default"));

        string leadToken;
        SessionId seeded;
        string workerToken;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, team, ct: ct));
            leadToken = claim.Token.Token;

            // A real dispatched worker in the same Team, so its token is a valid principal
            // that simply carries no lead claim.
            var store = new SessionStore(db, TimeProvider.System);
            var created = (StoreResult.Applied)await store.CreateAsync(
                new CreateSession(new LeadClaim(team), team, "seed", "default"), ct);
            seeded = created.Session.Id;
            var instance = WorkerInstanceId.New();
            await store.DispatchNextAsync(
                new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance, ct);
            workerToken = (await tokens.MintWorkerTokenAsync(team, seeded, instance, ct)).Token;
        }

        // ── Lead: the routing view, parsed as the structured data it is ──────
        await using (var lead = await ConnectAsync(baseUri, leadToken, ct))
        {
            var listed = await lead.CallToolAsync(
                "list_profiles", new Dictionary<string, object?>(), cancellationToken: ct);
            Assert.NotEqual(true, listed.IsError);

            var text = Assert.Single(listed.Content.OfType<TextContentBlock>()).Text;
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            Assert.Equal(2, root.GetProperty("connectedMachines").GetInt32());
            Assert.False(root.TryGetProperty("defaultProfile", out _),
                "there is no reserved default profile");

            var profiles = root.GetProperty("profiles").EnumerateArray().ToList();
            Assert.Equal(
                new[] { "default", "gpu" }, profiles.Select(p => p.GetProperty("profile").GetString()));

            var shared = profiles.Single(p => p.GetProperty("profile").GetString() == "default");
            Assert.True(shared.GetProperty("dispatchable").GetBoolean());
            Assert.Equal(
                new[] { "m1", "m2" },
                shared.GetProperty("machines").EnumerateArray()
                    .Select(m => m.GetProperty("machineId").GetString()));
            // Liveness per candidate machine — "reachable now", not "declared once" (§10).
            Assert.All(shared.GetProperty("machines").EnumerateArray(), m =>
            {
                Assert.True(m.GetProperty("ready").GetBoolean());
                Assert.False(m.GetProperty("underBackPressure").GetBoolean());
                Assert.NotNull(m.GetProperty("lastHeartbeat").GetString());
            });

            // The narrow profile carries only the machine declaring it, which is the whole
            // point of reading this before setting create_session(profile:).
            Assert.Equal(
                new[] { "m1" },
                profiles.Single(p => p.GetProperty("profile").GetString() == "gpu")
                    .GetProperty("machines").EnumerateArray()
                    .Select(m => m.GetProperty("machineId").GetString()));
        }

        // ── Worker: refused, and told nothing about the fleet ────────────────
        await using (var worker = await ConnectAsync(baseUri, workerToken, ct))
        {
            var refused = await worker.CallToolAsync(
                "list_profiles", new Dictionary<string, object?>(), cancellationToken: ct);

            Assert.Equal(true, refused.IsError);
            var text = string.Concat(refused.Content.OfType<TextContentBlock>().Select(c => c.Text));
            // A worker learns the machine group's shape from neither the answer nor the
            // refusal: no machine id, no profile name it did not already know.
            Assert.DoesNotContain("m2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("gpu", text, StringComparison.Ordinal);
        }

        await app.StopAsync(ct);
    }

    private static MachineHeartbeat Heartbeat(string machineId, params string[] profiles) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningSessions: 0, profiles, DateTimeOffset.UtcNow);

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Mirrors Program.cs wiring, pointed at the fixture's ephemeral database.
        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>(); // §8.4: WorkerTools.open_preview
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        builder.Services.AddLandbridgeForwarding(); // §8.3: WorkerTools needs the forward orchestrator
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(LandbridgeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LandbridgeAuthenticationHandler>(
                LandbridgeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>()
            .WithTools<FrictionTools>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp().RequireAuthorization();
        return app;
    }

    private static async Task<McpClient> ConnectAsync(Uri endpoint, string bearer, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }
}
