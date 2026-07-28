using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
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
    .WithTools<WorkerTools>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// The MCP endpoint requires an authenticated principal; tools resolve their
// caller from it.
app.MapMcp().RequireAuthorization();

app.Run();

/// <summary>Exposed so WebApplicationFactory-based tests can host the app.</summary>
public partial class Program;
