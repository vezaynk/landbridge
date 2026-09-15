using Landbridge.ControlPlane;
using Landbridge.Hub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Landbridge.Hub.Tests;

internal static class HubTestHost
{
    public static WebApplication Build(string connectionString)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContextFactory<LandbridgeDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<LandbridgeDbContext>>().CreateDbContext());
        builder.Services.AddScoped<FriendlyIds>();
        builder.Services.AddScoped<HubReads>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddOptions<HubOptions>();
        builder.Services.AddSingleton<HubWaiters>();
        builder.Services.AddSingleton(sp => new HubProjector(
            connectionString,
            sp.GetRequiredService<HubWaiters>(),
            sp.GetRequiredService<ILogger<HubProjector>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HubProjector>());
        var app = builder.Build();
        app.MapHub();
        return app;
    }

    public static HttpClient Client(WebApplication app)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }
}
