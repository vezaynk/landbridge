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
app.Run();
