using Landbridge.ControlPlane;
using Landbridge.Hub;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("Landbridge")
    ?? Environment.GetEnvironmentVariable("LANDBRIDGE_DB")
    ?? "Host=localhost;Database=landbridge;Username=landbridge";

builder.Services.AddDbContextFactory<LandbridgeDbContext>(o =>
    o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<HubOptions>().BindConfiguration(HubOptions.SectionName);
builder.Services.AddSingleton<HubWaiters>();
builder.Services.AddSingleton(sp => new HubProjector(
    connectionString,
    sp.GetRequiredService<IDbContextFactory<LandbridgeDbContext>>(),
    sp.GetRequiredService<HubWaiters>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<HubProjector>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubProjector>());
builder.Services.AddHostedService<HubRetention>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapHub();
app.Run();

/// <summary>Exposed so a test host can construct the app.</summary>
public partial class Program;
