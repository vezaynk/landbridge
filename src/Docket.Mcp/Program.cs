using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.Mcp;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Docket")
    ?? Environment.GetEnvironmentVariable("DOCKET_DB")
    ?? "Host=localhost;Database=docket;Username=docket";

// The store: one DbContext per request scope; the state machine is the only
// write path (spec §15).
builder.Services.AddDbContext<DocketDbContext>(o =>
    o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
builder.Services.AddScoped<TaskStore>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

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
builder.Services.AddSingleton<DispatchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DispatchService>());

var app = builder.Build();

// docketd dials the runner endpoint outbound as a WebSocket (§10).
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

// The MCP endpoint requires an authenticated principal; tools resolve their
// caller from it.
app.MapMcp().RequireAuthorization();

// The control plane ↔ runner WebSocket (machine-only, §10).
app.MapRunnerEndpoint();

app.Run();

/// <summary>Exposed so WebApplicationFactory-based tests can host the app.</summary>
public partial class Program;
