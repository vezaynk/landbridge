using Landbridge.Mcp;
using Landbridge.Mcp.Skills;
using Landbridge.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlane();
builder.Services.AddLandbridgeHubClient();

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<LeadTools>()
    .WithTools<FrictionTools>()
    .WithResources<SkillResources>()
    .WithSessionTaskProjection();

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp().RequireAuthorization();
app.MapLeadInbox();
// §5: this host is a resource server, so it serves the document its own 401
// challenge advertises (RFC 9728 §3). The authorize/token endpoints and the RFC
// 8414 document belong to Landbridge.Auth; a client reaches them by following the
// authorization_servers pointer in here.
app.MapOAuthResourceMetadata();
app.Run();
