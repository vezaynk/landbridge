using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The payoff (spec §10): a Lead creates a task over a real MCP connection and a
/// dispatched worker acts on it over its own — end to end, over the wire, with
/// the actual opaque-token auth handler in the loop.
///
/// The server is the real <c>docket-mcp</c> pipeline (auth scheme, both tool
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
    public async Task Lead_creates_a_task_over_mcp_and_a_worker_carries_it_to_verifying()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var team = TeamId.New();

        // A human session claims the Lead of the Team, exactly as a future OAuth
        // callback would drive it (§5).
        string leadToken;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, team, ct: ct));
            leadToken = claim.Token.Token;
        }

        // ── Lead: create a task over MCP ────────────────────────────────────
        TaskId taskId;
        await using (var lead = await ConnectAsync(baseUri, leadToken, ct))
        {
            var created = await lead.CallToolAsync("create_task", new Dictionary<string, object?>
            {
                ["description"] = "make the suite pass",
                ["completionCriteria"] = "the suite is green",
                ["mode"] = "automated",
                ["profile"] = null,
                ["workspace"] = null,
            }, cancellationToken: ct);

            Assert.NotEqual(true, created.IsError);
            var idText = Assert.Single(created.Content.OfType<TextContentBlock>()).Text;
            taskId = new TaskId(Guid.Parse(idText));
        }

        // ── Control plane: dispatch mints the incumbent worker instance ─────
        WorkerInstanceId instance = WorkerInstanceId.New();
        string workerToken;
        await using (var db = pg.NewContext())
        {
            var store = new TaskStore(db, TimeProvider.System);
            var machine = new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });
            var dispatched = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(machine, instance, ct));
            Assert.Equal(taskId, dispatched.Task.Id);

            var tokens = new TokenService(db, TimeProvider.System);
            workerToken = (await tokens.MintWorkerTokenAsync(team, taskId, instance, ct)).Token;
        }

        // ── Worker: report the result over its own MCP connection ───────────
        await using (var worker = await ConnectAsync(baseUri, workerToken, ct))
        {
            var reported = await worker.CallToolAsync("report_result", new Dictionary<string, object?>
            {
                ["resultReference"] = "git:branch/done",
            }, cancellationToken: ct);

            Assert.NotEqual(true, reported.IsError);
            Assert.Contains("Verifying", Assert.Single(reported.Content.OfType<TextContentBlock>()).Text);
        }

        // ── The record moved through the state machine, not around it ───────
        await using (var v = pg.NewContext())
        {
            var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value, ct);
            Assert.Equal(TaskState.Verifying, row.State);
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_worker_token_cannot_reach_the_lead_create_task_tool()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var team = TeamId.New();

        // A worker token authenticates (it is a valid principal) but carries no
        // lead claim, so create_task must refuse it — authority is structural (§5).
        string workerToken;
        await using (var db = pg.NewContext())
        {
            var store = new TaskStore(db, TimeProvider.System);
            var created = (StoreResult.Applied)await store.CreateAsync(
                new CreateTask(new LeadClaim(team), team, "seed", CompletionMode.Automated, null, TeamBudgetRemains: true), ct);
            var instance = WorkerInstanceId.New();
            await store.DispatchNextAsync(
                new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance, ct);
            var tokens = new TokenService(db, TimeProvider.System);
            workerToken = (await tokens.MintWorkerTokenAsync(team, created.Task.Id, instance, ct)).Token;
        }

        await using var worker = await ConnectAsync(baseUri, workerToken, ct);
        var result = await worker.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["description"] = "should not happen",
            ["completionCriteria"] = "should not happen",
            ["mode"] = "automated",
            ["profile"] = null,
            ["workspace"] = null,
        }, cancellationToken: ct);

        // The tool throws (no lead claim); the SDK surfaces it as a tool error.
        Assert.Equal(true, result.IsError);

        await app.StopAsync(ct);
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Mirrors Program.cs wiring, pointed at the fixture's ephemeral database.
        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TaskStore>();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(DocketAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DocketAuthenticationHandler>(
                DocketAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>();

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
