using System.ComponentModel;
using Docket.ControlPlane;
using Docket.Core;
using Docket.Mcp.Auth;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static Docket.Mcp.Tools.ToolResults;

namespace Docket.Mcp.Tools;

/// <summary>
/// The worker tool surface (spec §10). A worker's only channel to Docket. The
/// caller is never a parameter — it comes from the authenticated token
/// (HttpContext.User → WorkerCaller), so a worker can only ever act as itself
/// on its own task. Each tool is a thin adapter over an already-tested
/// <see cref="TaskStore"/> transition; the store re-checks incumbency (§9.14),
/// state, and every other rule.
/// </summary>
[McpServerToolType]
public sealed class WorkerTools(TaskStore store, IHttpContextAccessor http)
{
    private WorkerCaller Caller =>
        DocketClaims.AsWorker(http.HttpContext?.User ?? throw Unauthorized())
        ?? throw Unauthorized();

    [McpServerTool(Name = "report_result"),
     Description("Report the task's result reference and hand it to verification. " +
                 "The reference points at where the work actually is (the workspace substrate), " +
                 "not the work itself. Reporting is not a claim that verification passed.")]
    public async Task<string> ReportResult(
        [Description("A reference to where the completed work lives, e.g. a branch or commit.")]
        string resultReference,
        CancellationToken ct)
    {
        var caller = Caller;
        return Describe(await store.ApplyAsync(caller.Task, new ReportResult(caller, resultReference), ct));
    }

    [McpServerTool(Name = "request_input"),
     Description("Block the task pending input. Use when genuinely blocked or when a decision is " +
                 "above your scope. The task pauses and is answered by the Lead or a human.")]
    public async Task<string> RequestInput(
        [Description("The kind of input needed: question, spawn_request, auth_help, endpoint_wait, or unreachable.")]
        string kind,
        CancellationToken ct)
    {
        if (!Enum.TryParse<InputRequestKind>(kind, ignoreCase: true, out var parsed))
            throw new McpException(
                $"unknown input kind '{kind}'; expected one of: {string.Join(", ", Enum.GetNames<InputRequestKind>())}");

        var caller = Caller;
        return Describe(await store.ApplyAsync(caller.Task, new RequestInput(caller, parsed), ct));
    }

    [McpServerTool(Name = "register_service"),
     Description("Advertise a live endpoint to other tasks in your Team. Bind the port first, " +
                 "then register — an entry pointing at a port you failed to bind sends consumers " +
                 "into the wrong process.")]
    public async Task<string> RegisterService(
        [Description("A name other tasks will use to find this service.")] string name,
        [Description("The loopback port you have already bound.")] int port,
        CancellationToken ct)
    {
        var caller = Caller;
        return Describe(await store.RegisterServiceAsync(caller, name, port, ct));
    }

    private static McpException Unauthorized() =>
        new("this tool requires a worker credential");
}
