using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
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
        builder.Services.AddHubAuth();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddOptions<HubOptions>();
        builder.Services.AddSingleton<HubWaiters>();
        builder.Services.AddSingleton(sp => new HubProjector(
            connectionString,
            sp.GetRequiredService<HubWaiters>(),
            sp.GetRequiredService<ILogger<HubProjector>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HubProjector>());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub();
        return app;
    }

    public static HttpClient Client(WebApplication app, string? bearer = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };
        client.Timeout = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrEmpty(bearer))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    public static async Task<string> HumanTokenAsync(PostgresFixture pg, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await new TokenService(db, TimeProvider.System).IssueHumanSessionAsync(ct)).Token;
    }
}
