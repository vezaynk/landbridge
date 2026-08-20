using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Landbridge.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The whole task lifecycle end to end (spec §5, §6, §9 check 4), created → … →
/// completed, over real HTTP and real MCP with no LLM. It extends the walking
/// skeleton with the Lead close path:
///
/// <list type="number">
/// <item>A Lead creates a <c>lead</c>-mode task over real MCP.</item>
/// <item>The real <see cref="DispatchService"/> claims it, mints the worker token,
///   and the real <see cref="ProcessSupervisor"/> spawns the fake
///   <see cref="Landbridge.WorkerHarness"/>, which authenticates back to <c>/mcp</c>,
///   calls <c>get_session</c>, then <c>report_result(ref)</c>.</item>
/// <item>The Lead reads the reported reference (proving #23 persistence end to end),
///   then calls <c>stop_session</c> over MCP — close, with
///   lead-session provenance (the doer/judge split: the Lead closes, never the
///   session's own worker).</item>
/// </list>
///
/// The former automated-verifier module is cut (§7): CI and tests are evidence a
/// Lead gathers itself. Real <c>claude -p</c> is the operator-run validation (§17.0).
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
        SessionId sessionId;
        await using (var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct))
        {
            var created = await lead.CallToolAsync("create_session", new Dictionary<string, object?>
            {
                ["description"] = description,
                ["profile"] = "default",
            }, cancellationToken: ct);
            Assert.NotEqual(true, created.IsError);
            sessionId = new SessionId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
        }

        // ── The runner side: the real supervisor spawns the fake worker harness ──
        var workRoot = NewWorkRoot();
        var ring = new OutboundEventRing(capacity: 256);
        var supervisor = new ProcessSupervisor(
            new MachineConfig(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default),
            ring, TimeProvider.System);

        var profile = new ProfileConfig(
            "default",
            [WorkerHarnessPath(), "--acp"],
            new StopConfig(WindDown: TimeSpan.FromSeconds(30)),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(),
            Prompt: "Do the task.",
            FollowUp: "There is new input on your assignment. Read it, then continue.");

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
                new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], DateTimeOffset.UtcNow));

            var dispatch = new DispatchService(
                app.Services.GetRequiredService<IServiceScopeFactory>(),
                registry, TimeProvider.System, NullLogger<DispatchService>.Instance,
                publicMcpUrl: baseUrl);
            await dispatch.RunDispatchPassAsync(ct);

            // ── The harness authenticated and reported: report_result ──
            var workDir = Path.Combine(workRoot, sessionId.ToString());
            var reached = await WaitUntilAsync(
                async () => await HasReportAsync(sessionId, ct),
                TimeSpan.FromSeconds(60));
            if (!reached)
            {
                var errPath = Path.Combine(workDir, "harness_error.txt");
                var detail = File.Exists(errPath) ? await File.ReadAllTextAsync(errPath, ct) : "(no harness_error.txt)";
                Assert.Fail($"worker harness never mailed a report. Harness diagnostic:\n{detail}");
            }

            // The reference the harness reported (§10) — persisted by #23; a plain
            // row read proves report_result's reference crossed real HTTP + MCP.
            const string reportedRef = "landbridge-worker-harness:done";
            await using (var v = pg.NewContext())
                Assert.Equal(reportedRef,
                    (await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value, ct)).ResultReference);

            // ── Lead closes over real MCP: stop_session → completed ──
            // (§7, §9 check 4): the Lead session closes the row — never the
            // session's own worker.
            await using (var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct))
            {
                // #81: and it landed where the Lead actually reads it. The row assertion
                // above only proves persistence; this is the Lead's get_session_report
                // over real MCP.
                var reportRead = await lead.CallToolAsync("get_session_report", new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId.ToString(),
                }, cancellationToken: ct);
                Assert.NotEqual(true, reportRead.IsError);
                Assert.Contains(reportedRef,
                    Assert.Single(reportRead.Content.OfType<TextContentBlock>()).Text, StringComparison.Ordinal);

                var stopped = await lead.CallToolAsync("stop_session", new Dictionary<string, object?>
                {
                    ["sessionId"] = sessionId.ToString(),
                }, cancellationToken: ct);
                Assert.NotEqual(true, stopped.IsError);
                Assert.Contains("Completed", Assert.Single(stopped.Content.OfType<TextContentBlock>()).Text);
            }

            // ── The record reached completed, with lead-session provenance (§9.4) ──
            await using (var v = pg.NewContext())
            {
                var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value, ct);
                Assert.Equal(SessionState.Completed, row.State);
                Assert.Equal(VerdictProvenance.LeadSession, row.CompletionProvenance);
            }
        }
        finally
        {
            supervisor.KillAll();
            TryDeleteRoot(workRoot);
            await app.StopAsync(ct);
        }
    }

    private async Task<SessionState?> StateAsync(SessionId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await new SessionStore(db, TimeProvider.System).GetStateAsync(id, ct);
    }

    private async Task<bool> HasReportAsync(SessionId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var row = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id.Value, ct);
        return row?.MessageState == MessageState.AwaitingReport;
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

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

    private static string WorkerHarnessPath()
    {
        const string stem = "Landbridge.WorkerHarness";
        var testDir = Path.GetDirectoryName(typeof(FullLifecycleEndToEndTests).Assembly.Location)!;
        var harnessDir = testDir.Replace(
            Path.Combine("Landbridge.Mcp.Tests", "bin"),
            Path.Combine(stem, "bin"),
            StringComparison.Ordinal);
        var apphost = Path.Combine(harnessDir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        return File.Exists(apphost)
            ? apphost
            : throw new FileNotFoundException(
                $"worker harness apphost not found at {apphost}; is Landbridge.WorkerHarness built?");
    }

    private static string NewWorkRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "landbridge-lifecycle-tests", Guid.NewGuid().ToString("N"));
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
