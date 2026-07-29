using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.Mcp;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry (traces/metrics/logs), health checks,
// service discovery, and HTTP resilience. The OTLP exporter only activates when
// OTEL_EXPORTER_OTLP_ENDPOINT is set — the Aspire app host sets it, so the
// dashboard captures the plane's telemetry; standalone runs and tests leave it
// unset and simply don't export.
builder.AddServiceDefaults();

// §1 tracing: register the control-plane dispatch span source with the tracer
// ServiceDefaults configured, so DispatchService's `dispatch {task}` span exports
// to the same OTLP endpoint (the Aspire dashboard) as the AspNetCore/HTTP spans.
// AddOpenTelemetry() returns the same builder ServiceDefaults set up; WithTracing
// accumulates onto it.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(DispatchService.ActivitySourceName));

var connectionString = builder.Configuration.GetConnectionString("Docket")
    ?? Environment.GetEnvironmentVariable("DOCKET_DB")
    ?? "Host=localhost;Database=docket;Username=docket";

// The store: one DbContext per request scope; the state machine is the only
// write path (spec §15).
builder.Services.AddDbContext<DocketDbContext>(o =>
    o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
builder.Services.AddScoped<TaskStore>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<RelayGrantService>();
builder.Services.AddScoped<OAuthAuthorizationCodeService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

// OAuth 2.1 authorization-server support (§5). The operator verifier and CIMD
// client are singletons: the verifier caches the configured passphrase hash, and
// the CIMD client holds one SSRF-fenced HttpClient. The CIMD insecure flag relaxes
// the https/private-host guard for dev/test loopback fetches only — off by default.
builder.Services.AddSingleton<IOperatorVerifier, ConfiguredOperatorVerifier>();
builder.Services.AddSingleton<ICimdClient>(sp =>
    new CimdClient(sp.GetRequiredService<IConfiguration>().GetValue<bool>(CimdClient.AllowInsecureKey)));

// Opaque bearer tokens validated against the store (§5). Every MCP request
// authenticates as its token's principal; a worker can only reach worker tools.
builder.Services.AddAuthentication(DocketAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DocketAuthenticationHandler>(
        DocketAuthenticationHandler.SchemeName, configureOptions: null);
builder.Services.AddAuthorization();

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<WorkerTools>()
    .WithTools<LeadTools>();

// The runner spine (spec §10): the connection registry and event sink are
// singletons; the dispatch loop is a hosted service, exposed as a singleton too
// so the runner endpoint can nudge it when a machine becomes ready. The task-
// event listener owns its own session-mode Postgres connection (§3.1 LISTEN).
builder.Services.AddSingleton<RunnerConnectionRegistry>();
builder.Services.AddSingleton<RunnerEventSink>();
builder.Services.AddSingleton(new TaskEventListener(connectionString));

// §13: the public MCP URL a worker dials the plane with. docketd wraps it plus
// the minted worker token into the harness's --mcp-config at dispatch.
var publicMcpUrl = builder.Configuration["Docket:PublicMcpUrl"]
    ?? Environment.GetEnvironmentVariable("DOCKET_PUBLIC_MCP_URL")
    ?? DispatchService.DefaultPublicMcpUrl;

// The canonical OAuth identity (§5): resource id, issuer, and the two endpoint
// URLs all derive from the same public URL, so the RFC 9728 challenge, the
// well-known metadata documents, and authorize/token validation never disagree.
builder.Services.AddSingleton(OAuthServerConfig.FromPublicMcpUrl(publicMcpUrl));
builder.Services.AddSingleton(sp => new DispatchService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<RunnerConnectionRegistry>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<DispatchService>>(),
    sp.GetRequiredService<TaskEventListener>(),
    publicMcpUrl: publicMcpUrl));
builder.Services.AddHostedService(sp => sp.GetRequiredService<DispatchService>());

var app = builder.Build();

// Dev-loop only (set by the Aspire app host): apply the checked-in EF migration
// so a fresh Postgres container comes up with the schema. Production applies
// migrations out of band and the tests migrate through their fixture — neither
// sets this flag, so their startup behaviour is unchanged.
if (app.Configuration.GetValue<bool>("Docket:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DocketDbContext>()
        .Database.MigrateAsync();
}

// Dev-loop only (set by the Aspire app host): bootstrap a machine identity and
// drop its access token where docketd can pick it up. Runs AFTER migration so
// the schema exists. This is the dev shortcut around the enrollment handshake a
// real operator performs out of band (§5, §11) — an enrollment token is minted
// and immediately exchanged for machine credentials here, then the access token
// is written to a file the app host hands docketd via DOCKET_MACHINE_TOKEN.
// Minting fresh each startup is fine for a throwaway dev cluster. Production
// never sets this key, so its startup is unchanged. The write happens before
// app.Run(), so the file exists by the time the host is serving.
var devSeedTokenFile = app.Configuration["Docket:DevSeed:TokenFile"];
if (!string.IsNullOrWhiteSpace(devSeedTokenFile))
{
    using var scope = app.Services.CreateScope();
    var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

    var enrollment = await tokens.IssueEnrollmentTokenAsync();
    var credentials = await tokens.ExchangeEnrollmentAsync(
        enrollment.Token,
        new MachineDeclaration(
            Name: "docket-apphost-dev",
            Purpose: "Aspire dev-loop runner",
            Os: RuntimeInformation.OSDescription,
            PermissionLevel: "standard"))
        ?? throw new InvalidOperationException("dev seed: enrollment exchange returned null");

    // Built with the DOM so the token is escaped correctly with no serializer
    // reflection, mirroring DispatchService.BuildWorkerMcpConfig.
    var seedJson = new JsonObject
    {
        ["machineId"] = credentials.MachineId.ToString(),
        ["machineToken"] = credentials.Access.Token,
    }.ToJsonString();

    await File.WriteAllTextAsync(devSeedTokenFile, seedJson);
    // Owner-only: this file carries a live machine credential (§5).
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(devSeedTokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    app.Logger.LogInformation(
        "dev seed: wrote machine {MachineId} token to {File}", credentials.MachineId, devSeedTokenFile);
}

// Aspire health endpoints (/health, /alive), mapped in Development only.
app.MapDefaultEndpoints();

// docketd dials the runner endpoint outbound as a WebSocket (§10).
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

// The MCP endpoint requires an authenticated principal; tools resolve their
// caller from it.
app.MapMcp().RequireAuthorization();

// The control plane ↔ runner WebSocket (machine-only, §10).
app.MapRunnerEndpoint();

// The verifier webhook (§5, §10): plain HTTP, verifier-credential-only. Not MCP —
// the verifier posts verdicts to Docket, it is not an agent. RequireAuthorization
// is applied inside on the /verify group; each handler further narrows to the
// verifier principal.
app.MapVerifierEndpoints();

// The relay grant-validation endpoint (§8.3): plain HTTP, shared-bearer auth,
// fail-closed. The relay asks whether a presented grant is valid for a tunnel;
// this is the real control-plane validator behind Docket.Relay's IGrantValidator.
// Not in the Principal system — the relay is not a §5 credential class.
app.MapRelayValidationEndpoint();

// OAuth 2.1 authorization server (§5): the two anonymous well-known discovery
// documents (RFC 9728 / RFC 8414) and the authorize + token endpoints. Together
// with the RFC 9728 challenge on the MCP 401, these make the plane a real
// authorization server whose completed flow mints the existing human session.
app.MapOAuthMetadataEndpoints();
app.MapOAuthEndpoints();

app.Run();

/// <summary>Exposed so WebApplicationFactory-based tests can host the app.</summary>
public partial class Program;
