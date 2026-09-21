using System.Text.Json.Nodes;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Dashboard;
using Landbridge.Mcp.Skills;
using Landbridge.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.AddPlane();
builder.AddClassifier();

// §1 tracing: register the control-plane dispatch span source with the tracer
// ServiceDefaults configured, so DispatchService's `dispatch {task}` span exports
// to the same OTLP endpoint (the Aspire dashboard) as the AspNetCore/HTTP spans.
// AddOpenTelemetry() returns the same builder ServiceDefaults set up; WithTracing
// accumulates onto it.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(DispatchService.ActivitySourceName));
builder.Services.AddScoped<PreviewConnectService>();
builder.Services.AddSingleton<PreviewAuthStore>();
builder.Services.AddScoped<OAuthAuthorizationCodeService>();
builder.Services.AddDashboard();

// The operator verifier caches the configured passphrase hash. The authorization
// server is its own host now (Landbridge.Auth), but this one still needs the
// verifier: the dashboard's own login (§12) checks the same passphrase without
// going through OAuth at all.
builder.Services.AddSingleton<IOperatorVerifier, ConfiguredOperatorVerifier>();

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<WorkerTools>()
    .WithTools<LeadTools>()
    .WithTools<FrictionTools>()
    // §10/§14: the skill bundle ships as MCP resources so it reaches every agent
    // on connect. Stable landbridge:// URIs; scoping is advisory (see SkillResources).
    .WithResources<SkillResources>()
    .WithSessionTaskProjection();

// §13: the public MCP URL. The dashboard and a human Lead stay on PublicMcpUrl.
// Workers may need a different reachability (Aspire boxes in Linux containers
// dial host.docker.internal); WorkerMcpUrl overrides only the URL stamped onto
// dispatch / {mcp_url}. Unset, they are the same.
var publicMcpUrl = builder.Configuration["Landbridge:PublicMcpUrl"]
    ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PUBLIC_MCP_URL")
    ?? DispatchService.DefaultPublicMcpUrl;
var workerMcpUrl = builder.Configuration["Landbridge:WorkerMcpUrl"]
    ?? Environment.GetEnvironmentVariable("LANDBRIDGE_WORKER_MCP_URL")
    ?? publicMcpUrl;


// §10 per-task liveness runs on two clocks, both configurable: PerTaskLivenessWindow
// is how long landbridged may go without asserting the harness process is alive (it
// asserts every heartbeat), and NoProgressCeiling is how long an alive process may
// make no progress before it is treated as wedged. A task bearing a registered
// service (§8.2) is exempt from the second, never the first.
builder.Services.AddSingleton(sp => new DispatchService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<RunnerConnectionRegistry>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<DispatchService>>(),
    sp.GetRequiredService<SessionEventListener>(),
    livenessWindow: builder.Configuration.GetValue<TimeSpan?>("Landbridge:PerTaskLivenessWindow"),
    publicMcpUrl: workerMcpUrl,
    noProgressCeiling: builder.Configuration.GetValue<TimeSpan?>("Landbridge:NoProgressCeiling")));
builder.Services.AddHostedService(sp => sp.GetRequiredService<DispatchService>());

// §11 wait-TTL sweeper: requeues a task whose machine went silent while it
// waited. Auto-park on wait TTL is off by default (a live ACP session is held
// until a Lead answers or park_session); set Landbridge:WaitTtl to restore a timer.
builder.Services.AddHostedService(sp => new WaitTtlSweeper(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<RunnerConnectionRegistry>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<WaitTtlSweeper>>(),
    waitTtl: builder.Configuration.GetValue<TimeSpan?>("Landbridge:WaitTtl"),
    machineLivenessWindow: builder.Configuration.GetValue<TimeSpan?>("Landbridge:MachineLivenessTtl"),
    sweepInterval: builder.Configuration.GetValue<TimeSpan?>("Landbridge:WaitTtlSweepInterval")));

var app = builder.Build();

// Dev-loop only (set by the Aspire app host): apply the checked-in EF migration
// so a fresh Postgres container comes up with the schema. Production applies
// migrations out of band and the tests migrate through their fixture — neither
// sets this flag, so their startup behaviour is unchanged.
if (app.Configuration.GetValue<bool>("Landbridge:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<LandbridgeDbContext>();
    await db.Database.MigrateAsync();
    await HaikuSlug.ReplaceMachinePlaceholdersAsync(db);
}

// Dev-loop only (set by the Aspire app host): enroll the standing fleet and
// drop each access token where that box's landbridged can pick it up. Runs
// AFTER migration so the schema exists. This is the shortcut around the
// enrollment handshake a real operator performs out of band (§5, §11).
// Production never sets this key. The writes happen before app.Run(), so the
// files exist by the time the host is serving. No Team is minted — a human
// Lead creates work, exactly as in production.
//
// Keep the harness list in lockstep with AppHost's DevHarnesses.
var devSeedTokenDir = app.Configuration["Landbridge:DevSeed:TokenDir"];
if (!string.IsNullOrWhiteSpace(devSeedTokenDir))
{
    Directory.CreateDirectory(devSeedTokenDir);
    using var scope = app.Services.CreateScope();
    var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

    foreach (var harness in (string[])["codex", "claude", "grok"])
    {
        var name = DevSeedNaming.Box(harness);
        var enrollment = await tokens.IssueEnrollmentTokenAsync();
        var credentials = await tokens.ExchangeEnrollmentAsync(
            enrollment.Token,
            new MachineDeclaration(
                Name: name,
                Os: DevSeedNaming.Os))
            ?? throw new InvalidOperationException($"dev seed: enrollment exchange returned null for {name}");

        var seedJson = new JsonObject
        {
            ["machineId"] = string.IsNullOrEmpty(credentials.Slug)
                ? credentials.MachineId.ToString()
                : credentials.Slug,
            ["machineToken"] = credentials.Access.Token,
        }.ToJsonString();

        var seedPath = Path.Combine(devSeedTokenDir, $"{name}.json");
        await File.WriteAllTextAsync(seedPath, seedJson);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(seedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        app.Logger.LogInformation(
            "dev seed: enrolled {Name} as machine {MachineId} → {File}",
            name, credentials.MachineId, seedPath);
    }
}

// Aspire health endpoints (/health, /alive), mapped in Development only.
app.MapDefaultEndpoints();

// landbridged dials the runner endpoint outbound as a WebSocket (§10).
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
// Blazor Server needs this between auth and Map*. Sitting it here — not inside
// MapDashboard after MapRunnerEndpoint — keeps the /runner upgrade on the same
// pipeline the rest of the host uses.
app.UseAntiforgery();

// A browser opening the plane URL (launchSettings, Aspire, a typed :5050) is a
// GET / with Accept: text/html. MapMcp owns that path for POST (and SSE GET
// with Accept: text/event-stream), so a human GET is 405 without this bounce.
// Agents are unaffected: they POST, or GET with text/event-stream.
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsGet(ctx.Request.Method)
        && ctx.Request.Path == "/"
        && AcceptsHtml(ctx.Request.Headers.Accept.ToString()))
    {
        ctx.Response.Redirect("/dashboard");
        return;
    }
    await next();
});

// The MCP endpoint requires an authenticated principal; tools resolve their
// caller from it.
app.MapMcp().RequireAuthorization();

// ACP session/request_permission lands in landbridged, which is not an MCP client.
// POST /worker/permission runs PermissionRelay, authenticated with the worker bearer.
app.MapWorkerPermissionEndpoint();

// Lead inbox: JSON snapshot plus SSE of the same snapshot. Lead bearer only.
// Separate from MapMcp's SSE-on-/ and from the human dashboard's 5s poll.
app.MapLeadInbox();

// The control plane ↔ runner WebSocket (machine-only, §10).
app.MapRunnerEndpoint();

// The §12 web dashboard — the primary human surface (Machine Group, Team view,
// Human inbox, event log), Blazor Server with a JSON twin. Gated by its own
// bearer-or-cookie resolution (DashboardAuth), not RequireAuthorization, so the
// browser path never trips the MCP challenge.
app.MapDashboard();
// §12 transcript serving: its own file and its own auth rule (human operator only).
app.MapDashboardTranscripts();

// The relay grant-validation endpoint (§8.3): plain HTTP, shared-bearer auth,
// fail-closed. The relay asks whether a presented grant is valid for a tunnel;
// this is the real control-plane validator behind Landbridge.Relay's IGrantValidator.
// Not in the Principal system — the relay is not a §5 credential class.
app.MapRelayValidationEndpoint();

// The preview-frontend connect endpoint (§8.4): plain HTTP, shared-bearer auth,
// fail-closed. The HTTP preview frontend calls it per browser connection to
// resolve a label, authorize it, mint a grant + forward id, and arm the producer
// to dial on demand. Not in the Principal system — the frontend, like the relay,
// is not a §5 credential class.
app.MapPreviewConnectEndpoint();

// The machine bootstrap surface (§5 Bootstrap, §13): /enroll exchanges a
// human-issued enrollment token for machine credentials; /machine/refresh
// re-mints landbridged's short-lived access token. Both anonymous — the presented
// token is the credential, validated by TokenService, not a Principal.
app.MapEnrollmentEndpoints();

// The resource server's discovery document (RFC 9728, anonymous). It names the
// authorization server, which is what the RFC 9728 challenge on the MCP 401 sends
// a client to read. The authorize/token endpoints and the RFC 8414 document are
// Landbridge.Auth's; this host never serves them.
app.MapOAuthResourceMetadata();

app.Run();

static bool AcceptsHtml(string accept) =>
    accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);

/// <summary>Exposed so WebApplicationFactory-based tests can host the app.</summary>
public partial class Program;
