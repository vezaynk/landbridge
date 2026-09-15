using Landbridge.Mcp;
using Landbridge.Mcp.Skills;
using Landbridge.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);
builder.AddPlane();
builder.AddClassifier();
builder.Services.AddLandbridgeHubClient();

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<WorkerTools>()
    .WithTools<FrictionTools>()
    .WithResources<SkillResources>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp().RequireAuthorization();
app.MapWorkerPermissionEndpoint();
app.Run();
