using Docket.Meta;
using Docket.Meta.Auth;
using Docket.Meta.Data;
using Docket.Meta.Edge;
using Docket.Meta.Provisioning;
using Docket.Meta.Substrate;
using Docket.Meta.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Shared Aspire service defaults (health, OTel, resilience) — the ONLY Docket
// dependency meta carries, and it is MCP-free, so meta is structurally not an MCP
// server (spec §3). The OTLP exporter only activates when the endpoint env is set.
builder.AddServiceDefaults();

// Meta's own configuration (section "Meta") plus its own store connection string.
builder.Services.Configure<MetaOptions>(builder.Configuration.GetSection(MetaOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MetaOptions>>().Value);

var connectionString = builder.Configuration.GetConnectionString("Meta")
    ?? Environment.GetEnvironmentVariable("DOCKET_META_DB")
    ?? "Host=localhost;Database=docket_meta;Username=docket";
builder.Services.AddDbContext<MetaDbContext>(o =>
    o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);

// Operator auth (design note §5): a single passphrase (hash-configured, fail-closed)
// and an in-memory session store — meta shares no token store with the plane.
builder.Services.AddSingleton<MetaOperatorVerifier>();
builder.Services.AddSingleton<MetaSessionStore>();

// Substrate + edge seams. The Docker client factory and the operation tracker are
// singletons (pooled clients, process-wide state); Caddy + health probe get their
// own HttpClient via the factory.
builder.Services.AddSingleton<ISubstrateFactory, DockerSubstrateFactory>();
builder.Services.AddHttpClient<ICaddyAdmin, CaddyAdmin>();
builder.Services.AddHttpClient<InstanceHealthProbe>();
builder.Services.AddSingleton<OperationTracker>();
builder.Services.AddSingleton<LifecycleLauncher>();

// The saga + its collaborators. Scoped where they touch the per-request DbContext;
// the provisioner is resolved inside a fresh scope by the background launcher and
// the reconciler.
builder.Services.AddScoped<SecretGenerator>();
builder.Services.AddScoped<InstanceRecipe>();
builder.Services.AddScoped<PlacementService>();
builder.Services.AddScoped<InstanceCreator>();
builder.Services.AddScoped<InstanceProvisioner>();

// Resume any provision a meta restart interrupted (design note §2).
builder.Services.AddHostedService<InstanceReconciler>();

var app = builder.Build();

// Dev/single-operator convenience: apply meta's OWN migration to its OWN store at
// startup. Unrelated to the per-instance Docket:MigrateOnStartup meta injects into
// instance containers (deviation #4).
if (app.Services.GetRequiredService<MetaOptions>().MigrateOnStartup)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<MetaDbContext>().Database.MigrateAsync();
}

// Aspire health endpoints (/health, /alive), mapped in Development only.
app.MapDefaultEndpoints();

app.MapMeta();

app.Run();

/// <summary>Exposed so WebApplicationFactory-based tests can host the panel, mirroring the plane's hosts.</summary>
public partial class Program;
