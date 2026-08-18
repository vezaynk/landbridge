using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
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
/// The walking skeleton (spec §17 build order, §10): the whole dispatch loop
/// proven end to end with a REAL spawned worker process speaking REAL MCP — no
/// LLM. A Lead creates a task <em>with a description</em> over the wire; the real
/// <see cref="DispatchService"/> claims it, mints the worker token, and builds
/// the worker's <c>--mcp-config</c> (§13); the real <see cref="ProcessSupervisor"/>
/// spawns the fake worker harness, writing that config to
/// <c>{work_dir}/mcp.json</c> (0600) and substituting its path into the argv; the
/// harness authenticates back to the real <c>/mcp</c> endpoint with the
/// dispatched token, calls <c>get_session</c> to read its assignment, then
/// <c>report_result</c> — driving the task working → verifying.
///
/// This closes the loop the design leans on: a dispatched task reaches a worker
/// that learns what to do and reports back, over real MCP, with the actual auth
/// handler and state machine in the path. Real <c>claude -p</c> execution is the
/// operator-run validation (§17.0 spikes) and out of scope for automated tests.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WalkingSkeletonEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Dispatched_task_spawns_a_worker_that_authenticates_back_and_reports()
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

        // ── Lead: create a task WITH a description + workspace over real MCP ──
        const string description = "make the suite pass";
        const string criteria = "the suite is green";
        const string workspace = "git:repo@main#task-branch";
        SessionId sessionId;
        await using (var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct))
        {
            var created = await lead.CallToolAsync("create_session", new Dictionary<string, object?>
            {
                ["description"] = description,
                ["completionCriteria"] = criteria,
                ["mode"] = "lead",
                ["profile"] = null,
                ["workspace"] = workspace,
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

        // The profile runs the harness with the injected --mcp-config path (§13).
        var profile = new ProfileConfig(
            "default",
            [WorkerHarnessPath(), "--acp"],
            new StopConfig(WindDown: TimeSpan.FromSeconds(30)),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(),
            MaxConcurrent: null,
            Prompt: "Do the task.",
            FollowUp: "There is new input on your assignment. Read it, then continue.");

        try
        {
            // A registered ready machine whose send delegate hands the real
            // DispatchCommand — token + generated MCP config and all — to the real
            // supervisor. This is the seam a socket would occupy in production.
            var registry = new RunnerConnectionRegistry(TimeProvider.System);
            DispatchCommand? seen = null;
            registry.Register("m1", new HashSet<string> { "default" }, (command, _) =>
            {
                if (command is DispatchCommand d)
                {
                    seen = d;
                    supervisor.Spawn(d, profile, "m1");
                }
                return Task.CompletedTask;
            });
            registry.ApplyHeartbeat("m1", new MachineHeartbeat(
                "m1", Ready: true, UnderBackPressure: false,
                new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], DateTimeOffset.UtcNow));

            // The real dispatch pass, pointed at THIS server's MCP URL so the
            // generated mcp.json reaches the loopback plane the harness dials.
            var dispatch = new DispatchService(
                app.Services.GetRequiredService<IServiceScopeFactory>(),
                registry, TimeProvider.System, NullLogger<DispatchService>.Instance,
                publicMcpUrl: baseUrl);
            await dispatch.RunDispatchPassAsync(ct);

            // ── The harness authenticated and reported: working → verifying ──
            var workDir = Path.Combine(workRoot, sessionId.ToString());
            var reached = await WaitUntilAsync(
                async () => await StateAsync(sessionId, ct) == SessionState.Verifying,
                TimeSpan.FromSeconds(60));
            if (!reached)
            {
                var errPath = Path.Combine(workDir, "harness_error.txt");
                var detail = File.Exists(errPath) ? await File.ReadAllTextAsync(errPath, ct) : "(no harness_error.txt)";
                Assert.Fail($"worker harness never drove the task to verifying. Harness diagnostic:\n{detail}");
            }

            Assert.NotNull(seen);
            Assert.Equal(baseUrl, seen!.SpawnSubstitutions?["mcp_url"]);

            // ── §13 under ACP: the MCP server crossed on the WIRE, and left nothing on disk ──
            //
            // The inversion is the point. A stream profile names {mcp_config} in its argv and
            // landbridged writes the generated config to {work_dir}/mcp.json (0600) for the
            // harness to read; #112 G11 made that write conditional on the argv actually
            // referencing it. An ACP profile references nothing, because the server is a
            // parameter of session/new — so the correct assertion here is that no file was
            // written at all, and therefore that no live bearer token sat on disk for the
            // length of the task.
            //
            // What proves the wire path worked is the assignment below: the harness could
            // only have reached the plane by using the url and Authorization header it read
            // out of session/new's mcpServers array. There is no other source for them in
            // this profile.
            Assert.False(
                File.Exists(Path.Combine(workDir, "mcp.json")),
                "an ACP profile names no {mcp_config}, so landbridged should have written no config file — " +
                "the server rides session/new instead, which is what keeps the worker token off disk");

            // ── get_session delivered the assignment over the wire (§7) ──
            var assignmentPath = Path.Combine(workDir, "get_session.json");
            Assert.True(File.Exists(assignmentPath), "the harness never recorded its get_session response");
            var assignmentJson = await File.ReadAllTextAsync(assignmentPath, ct);
            Assert.Contains(description, assignmentJson);
            Assert.Contains(criteria, assignmentJson);
            Assert.Contains(workspace, assignmentJson);
            Assert.Contains($"team-{team}/session-{sessionId}", assignmentJson); // server-assigned namespace
            Assert.Contains("\"attempt\":1", assignmentJson);

            // ── report_result drove the record through the real state machine ──
            // (Persisting the opaque result reference onto the row is a separate
            // §7-content concern the store does capture — FullLifecycleEndToEndTests
            // asserts it — but the skeleton's proof is that the transition committed,
            // not its content.)
            await using (var v = pg.NewContext())
                Assert.Equal(SessionState.Verifying,
                    (await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value, ct)).State);
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

    /// <summary>
    /// The apphost of the built <see cref="Landbridge.WorkerHarness"/>, spawned
    /// directly (argv, no shell — §10). It is resolved from the harness's <b>own</b>
    /// build output, not the copy beside this test assembly: the harness is a
    /// plain console whose MCP-client dependency closure (Logging.Abstractions et
    /// al.) is copied local to its own bin, whereas this test project draws those
    /// same assemblies from the ASP.NET shared framework and so never copies them —
    /// so the copy beside the test cannot start. The ProjectReference guarantees
    /// the harness is built first; its output lives at the sibling project path.
    /// </summary>
    private static string WorkerHarnessPath()
    {
        const string stem = "Landbridge.WorkerHarness";
        var testDir = Path.GetDirectoryName(typeof(WalkingSkeletonEndToEndTests).Assembly.Location)!;
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
        var dir = Path.Combine(Path.GetTempPath(), "landbridge-skeleton-tests", Guid.NewGuid().ToString("N"));
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
