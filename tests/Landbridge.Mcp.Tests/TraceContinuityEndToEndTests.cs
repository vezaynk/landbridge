using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
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
using Xunit.Abstractions;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The §1 proof, deterministic and collector-free: ONE trace spans the whole loop.
/// A Lead creates a task over real MCP; the real <see cref="DispatchService"/>
/// opens its dispatch span parented on the create_session trace context the store
/// captured; the real <see cref="ProcessSupervisor"/> spawns the real
/// <c>Landbridge.WorkerHarness</c>, which roots its own span on the
/// <c>LANDBRIDGE_TRACEPARENT</c> landbridged injected and records it to <c>trace.json</c>.
///
/// The chain is asserted end to end without a dashboard: the stored
/// <c>SessionRow.TraceContext</c>, the dispatch span (captured through an
/// <see cref="ActivityListener"/>), and the worker's <c>trace.json</c> all carry
/// the <b>same trace id</b>, with the parent/child span links matching at each hop
/// — so the context provably survived create_session → row → dispatch span → worker
/// env → worker span. (The wire → landbridged hop is unit-tested by the RunnerWire
/// traceparent round-trip and validated live against the Aspire dashboard; here,
/// as in the walking skeleton, the supervisor stands in for the socket.)
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TraceContinuityEndToEndTests(PostgresFixture pg, ITestOutputHelper output) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task One_trace_id_spans_create_session_dispatch_and_the_spawned_worker()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // ── A process-wide listener so activities are created + sampled with real
        //    ids (AspNetCore's create_session request span and DispatchService's
        //    dispatch span). Records stopped activities so the dispatch span can be
        //    inspected after the run. The spawned worker is a separate process, so
        //    its span is read from trace.json, not from here.
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));

        var team = TeamId.New();
        string leadToken;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, team, ct: ct));
            leadToken = claim.Token.Token;
        }

        // ── Lead: create a task over real MCP (its server span is the trace root) ──
        SessionId sessionId;
        await using (var lead = await ConnectAsync(new Uri(baseUrl + "/"), leadToken, ct))
        {
            var created = await lead.CallToolAsync("create_session", new Dictionary<string, object?>
            {
                ["description"] = "trace me end to end",
                ["profile"] = "default",
            ["teamId"] = team.Value.ToString(),
            }, cancellationToken: ct);
            Assert.NotEqual(true, created.IsError);
            sessionId = new SessionId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
        }

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
            // The registry's send delegate hands the real DispatchCommand straight
            // to the real supervisor — the seam a socket would occupy. The dispatch
            // span is current when Spawn runs, so LANDBRIDGE_TRACEPARENT carries it.
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

            // ── The stored create_session trace context (opaque metadata on the row) ──
            string? storedTraceContext;
            await using (var v = pg.NewContext())
                storedTraceContext = (await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value, ct)).TraceContext;

            Assert.False(string.IsNullOrEmpty(storedTraceContext), "the row did not capture the create_session trace context");
            Assert.True(ActivityContext.TryParse(storedTraceContext, null, out var createContext),
                $"SessionRow.TraceContext is not a W3C traceparent: {storedTraceContext}");
            var createTraceId = createContext.TraceId.ToHexString();

            // ── The dispatch span: parented on the create context, same trace ──
            var dispatchSpan = Assert.Single(stopped, a =>
                a.Source.Name == DispatchService.ActivitySourceName &&
                a.OperationName == $"dispatch {sessionId}");
            Assert.Equal(createTraceId, dispatchSpan.TraceId.ToHexString());
            Assert.Equal(createContext.SpanId.ToHexString(), dispatchSpan.ParentSpanId.ToHexString());

            // ── The worker span (separate process): read from its trace.json ──
            var tracePath = Path.Combine(workDir, "trace.json");
            Assert.True(File.Exists(tracePath), "the worker harness never recorded trace.json");
            var marker = JsonNode.Parse(await File.ReadAllTextAsync(tracePath, ct))!;
            var workerTraceId = (string?)marker["traceId"];
            var workerParentSpanId = (string?)marker["parentSpanId"];

            // THE PROOF: the worker's span shares the create_session trace id — the
            // context survived create_session → row → dispatch span → worker env →
            // worker span — and it parented on the dispatch span exactly.
            Assert.Equal(createTraceId, workerTraceId);
            Assert.Equal(dispatchSpan.SpanId.ToHexString(), workerParentSpanId);

            output.WriteLine($"one trace id across all hops: {createTraceId}");
            output.WriteLine($"  create_session span      : {createContext.SpanId.ToHexString()} (SessionRow.TraceContext)");
            output.WriteLine($"  dispatch span         : {dispatchSpan.SpanId.ToHexString()} (parent {dispatchSpan.ParentSpanId.ToHexString()})");
            output.WriteLine($"  worker span           : {(string?)marker["spanId"]} (parent {workerParentSpanId})");
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
        return row is { ResultReference: { Length: > 0 } };
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

    private static string WorkerHarnessPath()
    {
        const string stem = "Landbridge.WorkerHarness";
        var testDir = Path.GetDirectoryName(typeof(TraceContinuityEndToEndTests).Assembly.Location)!;
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
        var dir = Path.Combine(Path.GetTempPath(), "landbridge-trace-tests", Guid.NewGuid().ToString("N"));
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
