using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp;
using Landbridge.Mcp.Dashboard;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlane();
builder.Services.AddDashboard();
builder.Services.AddSingleton<IOperatorVerifier, ConfiguredOperatorVerifier>();
builder.Services.AddSingleton<PreviewAuthStore>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapDashboard();
app.MapDashboardTranscripts();
// §5: this host is a resource server, so it serves the document its own 401
// challenge advertises (RFC 9728 §3). The authorize/token endpoints and the RFC
// 8414 document belong to Landbridge.Auth; a client reaches them by following the
// authorization_servers pointer in here.
app.MapOAuthResourceMetadata();
app.Run();
