using System.Text.Json.Nodes;
using Landbridge.Core;
using Landbridge.Mcp.Tools;

#pragma warning disable MCPEXP001, MCPEXP002
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Landbridge.Mcp;

/// <summary>
/// Registers MCP Tasks methods as a projection of Landbridge sessions.
/// Does not wrap <c>tools/call</c>; <c>create_session</c> still returns a session id,
/// which is the task id.
/// </summary>
public static class SessionTaskMcp
{
    public static IMcpServerBuilder WithSessionTaskProjection(this IMcpServerBuilder mcp)
    {
        mcp.Services.AddScoped<SessionTaskHandlers>();
        mcp.Services.AddOptions<McpServerOptions>()
            .PostConfigure<IHttpContextAccessor>(Configure);
        return mcp;
    }

    internal static void Configure(McpServerOptions options, IHttpContextAccessor http)
    {
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Extensions ??= new Dictionary<string, object>(StringComparer.Ordinal);
        options.Capabilities.Extensions[SessionTaskProjection.ExtensionId] = new JsonObject();

        options.RequestHandlers ??= [];
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = "tasks/get",
            Handler = (req, ct) => Resolve(http).GetAsync(req, ct),
        });
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = "tasks/list",
            Handler = (req, ct) => Resolve(http).ListAsync(req, ct),
        });
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = "tasks/cancel",
            Handler = (req, ct) => Resolve(http).CancelAsync(req, ct),
        });
    }

    private static SessionTaskHandlers Resolve(IHttpContextAccessor http) =>
        http.HttpContext?.RequestServices.GetRequiredService<SessionTaskHandlers>()
        ?? throw new InvalidOperationException("tasks handlers require an HTTP request scope");
}
