using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Mcp;

/// <summary>
/// Shared store + Bearer auth + Hub client for LeadMCP, WorkerMCP, and
/// the fused plane. No dispatch, no <c>/runner</c>.
/// </summary>
public static class PlaneHost
{
    public static string ConnectionString(IConfiguration config) =>
        config.GetConnectionString("Landbridge")
        ?? Environment.GetEnvironmentVariable("LANDBRIDGE_DB")
        ?? "Host=localhost;Database=landbridge;Username=landbridge";

    public static WebApplicationBuilder AddPlane(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();
        var connectionString = ConnectionString(builder.Configuration);
        builder.Services.AddDbContextFactory<LandbridgeDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<LandbridgeDbContext>>().CreateDbContext());
        builder.Services.AddLandbridgeStore(
            builder.Configuration.GetValue<int?>("Landbridge:InfrastructureRequeueLimit"));
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<MachineRevocationService>();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication(LandbridgeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LandbridgeAuthenticationHandler>(
                LandbridgeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        // §5: who this host is, in OAuth terms. Every host here is a resource server —
        // PublicMcpUrl is the resource a token is minted for — and AuthUrl names the
        // authorization server that mints it, falling back to the resource id for a
        // single-origin Instance.
        //
        // It is registered for all of them, not just the ones that map OAuth endpoints,
        // because the challenge handler reads it: without it, LandbridgeAuthenticationHandler
        // derives the resource-metadata URL from the request and advertises a document on
        // an origin that does not serve one. That is a 401 pointing at a 404, and nothing
        // in the exchange says so.
        builder.Services.AddSingleton(OAuthServerConfig.FromPublicMcpUrl(
            builder.Configuration["Landbridge:PublicMcpUrl"]
                ?? Environment.GetEnvironmentVariable("LANDBRIDGE_PUBLIC_MCP_URL"),
            builder.Configuration["Landbridge:AuthUrl"]
                ?? Environment.GetEnvironmentVariable("LANDBRIDGE_AUTH_URL")));
        builder.Services.AddSingleton<RunnerEventSink>();
        builder.Services.AddLandbridgeForwarding();
        builder.Services.AddSingleton(new SessionEventListener(connectionString));
        builder.Services.AddSingleton(sp => new SessionEventFanout(
            connectionString, sp.GetRequiredService<ILogger<SessionEventFanout>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionEventFanout>());
        return builder;
    }

    public static void AddClassifier(this WebApplicationBuilder builder)
    {
        var classifierUrl = builder.Configuration["Landbridge:Classifier:Url"]
            ?? Environment.GetEnvironmentVariable("LANDBRIDGE_CLASSIFIER_URL");
        if (!string.IsNullOrWhiteSpace(classifierUrl)
            && Uri.TryCreate(classifierUrl.TrimEnd('/') + "/", UriKind.Absolute, out var classifierUri))
        {
            var timeoutMs = builder.Configuration.GetValue("Landbridge:Classifier:TimeoutMs", 2000);
            if (timeoutMs < 1)
                timeoutMs = 2000;
            builder.Services.AddHttpClient<IPermissionClassifier, PermissionClassifierClient>(c =>
            {
                c.BaseAddress = classifierUri;
                c.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
            });
        }
        else
        {
            builder.Services.AddSingleton<IPermissionClassifier>(NullPermissionClassifier.Instance);
        }
    }

    public static IServiceCollection AddLandbridgeHubClient(this IServiceCollection services)
    {
        services.AddHttpClient<HubClient>((sp, client) =>
        {
            var url = sp.GetRequiredService<IConfiguration>()["Landbridge:HubUrl"];
            if (!string.IsNullOrWhiteSpace(url))
                client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        });
        return services;
    }

    public static IServiceCollection AddLandbridgeCoreWrite(this IServiceCollection services)
    {
        services.AddHttpClient<CoreWriteClient>((sp, client) =>
        {
            var url = sp.GetRequiredService<IConfiguration>()["Landbridge:CoreUrl"];
            if (!string.IsNullOrWhiteSpace(url))
                client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        });
        return services;
    }
}
