using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Docket.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The whole task lifecycle end to end (spec §5, §6, §10), created → … →
/// completed, over real HTTP and real MCP with no LLM. It extends the walking
/// skeleton with the verifier verdict path:
///
/// <list type="number">
/// <item>A Lead creates an <c>automated</c> task over real MCP.</item>
/// <item>The real <see cref="DispatchService"/> claims it, mints the worker token,
///   and the real <see cref="ProcessSupervisor"/> spawns the fake
///   <see cref="Docket.WorkerHarness"/>, which authenticates back to <c>/mcp</c>,
///   calls <c>get_task</c>, then <c>report_result(ref)</c> — working → verifying.</item>
/// <item>A human provisions a verifier credential. The verifier polls
///   <c>GET /verify/pending</c> over plain HTTP and sees the task <em>with the
///   reference the worker reported</em> (proving #23 persistence end to end), then
///   posts <c>POST /verify/{id}</c> <c>accept</c> — verifying → completed.</item>
/// </list>
///
/// The verifier is not an MCP client: it posts to Docket, not the reverse (§10).
/// Real <c>claude -p</c> is the operator-run validation (§17.0) and out of scope.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FullLifecycleEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Dispatched_task_is_worked_reported_and_verified_all_the_way_to_completed()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));

        var team = TeamId.New();

        // A human claims the Lead of the Team (§5), as an OAuth callback would.
        string leadToken;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, team, ct: ct));
            leadToken = claim.Token.Token;
        }

        // ── Lead: create an automated task over real MCP ────────────────────
        const string description = "make the suite pass";
        const string criteria = "the suite is green";
        const string workspace = "git:repo@main#task-branch";
        TaskId taskId;
        await using (var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct))
        {
            var created = await lead.CallToolAsync("create_task", new Dictionary<string, object?>
            {
                ["description"] = description,
                ["completionCriteria"] = criteria,
                ["mode"] = "automated",
                ["profile"] = null,
                ["workspace"] = workspace,
            }, cancellationToken: ct);
            Assert.NotEqual(true, created.IsError);
            taskId = new TaskId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
        }

        // ── The runner side: the real supervisor spawns the fake worker harness ──
        var workRoot = NewWorkRoot();
        var ring = new OutboundEventRing(capacity: 256);
        var supervisor = new ProcessSupervisor(
            new MachineConfig(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default),
            ring, TimeProvider.System);

        var profile = new ProfileConfig(
            "default",
            [WorkerHarnessPath(), "--mcp-config", "{mcp_config}"],
            new StopConfig(StopMode.Signal, Signal: null, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(30)),
            Resume: null,
            new EventsConfig(EventsSource.None, new Dictionary<string, string>()),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(Path: null, Format: null),
            MaxConcurrent: null);

        try
        {
            var registry = new RunnerConnectionRegistry(TimeProvider.System);
            registry.Register("m1", new HashSet<string> { "default" }, (command, _) =>
            {
                if (command is DispatchCommand d)
                    supervisor.Spawn(d, profile, "m1");
                return Task.CompletedTask;
            });
            registry.ApplyHeartbeat("m1", new MachineHeartbeat(
                "m1", Ready: true, UnderBackPressure: false,
                new SystemLoad(0, 0, 0), RunningTasks: 0, ["default"], DateTimeOffset.UtcNow));

            var dispatch = new DispatchService(
                app.Services.GetRequiredService<IServiceScopeFactory>(),
                registry, TimeProvider.System, NullLogger<DispatchService>.Instance,
                publicMcpUrl: baseUrl);
            await dispatch.RunDispatchPassAsync(ct);

            // ── The harness authenticated and reported: working → verifying ──
            var workDir = Path.Combine(workRoot, taskId.ToString());
            var reached = await WaitUntilAsync(
                async () => await StateAsync(taskId, ct) == TaskState.Verifying,
                TimeSpan.FromSeconds(60));
            if (!reached)
            {
                var errPath = Path.Combine(workDir, "harness_error.txt");
                var detail = File.Exists(errPath) ? await File.ReadAllTextAsync(errPath, ct) : "(no harness_error.txt)";
                Assert.Fail($"worker harness never drove the task to verifying. Harness diagnostic:\n{detail}");
            }

            // The reference the harness reported (§10) — persisted by #23.
            const string reportedRef = "docket-worker-harness:done";

            // ── A human provisions a verifier; it polls over plain HTTP (§5, §10) ──
            string verifierToken;
            await using (var db = pg.NewContext())
                verifierToken = (await new TokenService(db, TimeProvider.System).ProvisionVerifierAsync(ct)).Token;

            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // GET /verify/pending shows the task AND the reported reference —
            // proving report_result's reference crossed real HTTP + MCP and landed.
            using (var pendingReq = new HttpRequestMessage(HttpMethod.Get, "/verify/pending"))
            {
                pendingReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", verifierToken);
                using var pendingResp = await http.SendAsync(pendingReq, ct);
                Assert.Equal(HttpStatusCode.OK, pendingResp.StatusCode);

                var pending = JsonSerializer.Deserialize<List<VerifyingTaskView>>(
                    await pendingResp.Content.ReadAsStringAsync(ct),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var view = Assert.Single(pending);
                Assert.Equal(taskId.Value, view.TaskId);
                Assert.Equal(reportedRef, view.ResultReference);
                Assert.Equal(criteria, view.CompletionCriteria);
            }

            // ── POST /verify/{id} accept → verifying → completed ────────────
            using (var verdictReq = new HttpRequestMessage(HttpMethod.Post, $"/verify/{taskId}")
            {
                Content = JsonContent.Create(new { verdict = "accept" }),
            })
            {
                verdictReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", verifierToken);
                using var verdictResp = await http.SendAsync(verdictReq, ct);
                Assert.Equal(HttpStatusCode.OK, verdictResp.StatusCode);
                using var doc = JsonDocument.Parse(await verdictResp.Content.ReadAsStringAsync(ct));
                Assert.Equal("Completed", doc.RootElement.GetProperty("state").GetString());
            }

            // ── The record reached the terminal completed state through the machine ──
            await using (var v = pg.NewContext())
                Assert.Equal(TaskState.Completed,
                    (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskId.Value, ct)).State);
        }
        finally
        {
            supervisor.KillAll();
            TryDeleteRoot(workRoot);
            await app.StopAsync(ct);
        }
    }

    private async Task<TaskState?> StateAsync(TaskId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await new TaskStore(db, TimeProvider.System).GetStateAsync(id, ct);
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TaskStore>();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        builder.Services.AddDocketForwarding(); // §8.3: WorkerTools needs the forward orchestrator
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
        app.MapVerifierEndpoints();
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

    private static string WorkerHarnessPath()
    {
        const string stem = "Docket.WorkerHarness";
        var testDir = Path.GetDirectoryName(typeof(FullLifecycleEndToEndTests).Assembly.Location)!;
        var harnessDir = testDir.Replace(
            Path.Combine("Docket.Mcp.Tests", "bin"),
            Path.Combine(stem, "bin"),
            StringComparison.Ordinal);
        var apphost = Path.Combine(harnessDir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        return File.Exists(apphost)
            ? apphost
            : throw new FileNotFoundException(
                $"worker harness apphost not found at {apphost}; is Docket.WorkerHarness built?");
    }

    private static string NewWorkRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "docket-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteRoot(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(100);
        }
        return await condition();
    }
}
