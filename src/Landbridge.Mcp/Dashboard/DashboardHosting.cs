using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp.Dashboard.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Registers the §12 dashboard as Blazor Server (interactive server + prerender)
/// so a first GET is a complete HTML document and a live circuit then refreshes
/// it. Test hosts call <see cref="AddDashboard"/> so they get the same pipeline
/// as <c>Program.cs</c>.
/// </summary>
public static class DashboardHosting
{
    public static IServiceCollection AddDashboard(this IServiceCollection services)
    {
        services.AddScoped<DashboardQueries>();
        services.AddHttpContextAccessor();
        services.TryAddSingleton<OperatorAttemptLimiter>();
        services.AddRazorComponents()
            .AddInteractiveServerComponents();
        return services;
    }

    /// <summary>
    /// Maps the Blazor circuit under <c>/dashboard/_blazor</c> so the
    /// <c>landbridge_session</c> cookie (Path=/dashboard) rides the SignalR
    /// connection, then maps the component tree.
    /// </summary>
    public static WebApplication MapDashboardUi(this WebApplication app)
    {
        app.MapBlazorHub("/dashboard/_blazor");
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        return app;
    }

    public static IResult RazorPage<T>(object? parameters = null, int status = 200)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        var result = parameters is null
            ? new RazorComponentResult<T>()
            : new RazorComponentResult<T>(parameters);
        result.StatusCode = status;
        return result;
    }
}
