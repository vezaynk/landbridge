using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp;
using Landbridge.Mcp.Dashboard;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlane();
builder.Services.AddDashboard();
builder.Services.AddSingleton<IOperatorVerifier, ConfiguredOperatorVerifier>();
builder.Services.AddSingleton<PreviewAuthStore>();
builder.Services.AddScoped<OAuthAuthorizationCodeService>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapDashboard();
app.MapDashboardTranscripts();
app.Run();
