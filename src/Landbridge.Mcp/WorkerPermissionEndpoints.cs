using Landbridge.ControlPlane;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Landbridge.Mcp;

/// <summary>
/// The runner-facing half of the §11 permission bridge. ACP
/// <c>session/request_permission</c> lands in landbridged, which has the worker
/// bearer but is not an MCP client; this endpoint is the same
/// <see cref="PermissionRelay"/> the MCP <c>request_permission</c> tool runs,
/// reachable over plain HTTP with that bearer.
/// </summary>
public static class WorkerPermissionEndpoints
{
    public static IEndpointRouteBuilder MapWorkerPermissionEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/worker/permission", HandleAsync).RequireAuthorization();
        return app;
    }

    public sealed record Ask(string? Tool, string? Input, System.Text.Json.Nodes.JsonNode? Options = null);

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        Ask? body,
        SessionStore store,
        IConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        var caller = LandbridgeClaims.AsWorker(http.User);
        if (caller is null)
            return Results.Json(new { error = "worker credential required" }, statusCode: StatusCodes.Status403Forbidden);

        if (body is null || string.IsNullOrWhiteSpace(body.Tool))
            return Results.BadRequest(new { error = "tool is required" });

        var proposed = string.IsNullOrWhiteSpace(body.Input) ? "{}" : body.Input;
        var optionsJson = body.Options is System.Text.Json.Nodes.JsonArray
            ? body.Options.ToJsonString()
            : null;
        var poll = WorkerTools.DefaultPermissionPollInterval;
        if (int.TryParse(config["Landbridge:PermissionPollIntervalMs"], out var ms) && ms > 0)
            poll = TimeSpan.FromMilliseconds(ms);

        // Isolated test hosts map this endpoint without the full Program.cs
        // container. Missing classifier is Ask, same as an unset URL.
        var classifier = http.RequestServices.GetService<IPermissionClassifier>()
            ?? NullPermissionClassifier.Instance;

        var result = await PermissionRelay.OpenAndAwaitAsync(
            store, caller, body.Tool, proposed, poll, clock, ct, classifier, optionsJson);

        return Results.Json(new
        {
            verdict = result.Allow ? "allow" : "deny",
            message = result.Message,
            optionId = result.OptionId,
        });
    }
}
