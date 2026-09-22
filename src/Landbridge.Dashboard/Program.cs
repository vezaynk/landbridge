using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp;
using Landbridge.Mcp.Dashboard;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlane();
builder.Services.AddLandbridgeCoreWrite();
builder.Services.AddDashboard();
builder.Services.AddSingleton<IOperatorVerifier, ConfiguredOperatorVerifier>();
builder.Services.AddSingleton<PreviewAuthStore>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.Use(async (ctx, next) =>
{
    if (HttpMethods.IsGet(ctx.Request.Method)
        && ctx.Request.Path == "/"
        && (ctx.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(ctx.Request.Headers.Accept)))
    {
        ctx.Response.Redirect("/dashboard");
        return;
    }
    await next();
});
app.MapDashboard();
app.MapDashboardTranscripts();
// §5: this host is a resource server, so it serves the document its own 401
// challenge advertises (RFC 9728 §3). The authorize/token endpoints and the RFC
// 8414 document belong to Landbridge.Auth; a client reaches them by following the
// authorization_servers pointer in here.
app.MapOAuthResourceMetadata();
app.Run();

/// <summary>
/// Exposed so a test host can construct the app. Named rather than the usual
/// <c>Program</c> because one test assembly hosts all three of these, and three
/// top-level <c>Program</c> types in the global namespace cannot be told apart
/// from there. <c>WebApplicationFactory</c> only needs a type to find the
/// assembly by, so the human operator UI is identified by this.
/// </summary>
public sealed class DashboardHost;
