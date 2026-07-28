using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.WorkerHarness;

/// <summary>
/// The walking skeleton's scripted worker (spec §10, worker-skill.md) — a REAL
/// process speaking REAL MCP back to the control plane, with no LLM. It stands in
/// for <c>claude -p</c> in the automated proof: it does exactly what a dispatched
/// worker's opening moves are and nothing else.
///
/// It reads the MCP client config <c>docketd</c> injected (§13) — the
/// <c>--mcp-config</c> path <see cref="ProcessSupervisor"/> substituted into its
/// argv, pointing at <c>{work_dir}/mcp.json</c> — connects to the plane with the
/// dispatched worker token as a bearer credential, calls <c>get_task</c> to learn
/// its assignment, then <c>report_result</c> with a reference, and exits 0. The
/// raw <c>get_task</c> response is written to <c>./get_task.json</c> in the work
/// dir so the harnessing test can assert the assignment crossed the wire intact.
///
/// No shell, argv only (§10 convention). Any failure writes a diagnostic to
/// <c>./harness_error.txt</c> and exits non-zero so the test sees a hard failure
/// rather than a hang.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cwd = Directory.GetCurrentDirectory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        try
        {
            var (url, authorization) = ResolveConnection(args);

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(url),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = authorization },
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            // ── Learn the assignment (§7): namespace, description, criteria, workspace, attempt.
            var assignment = await client.CallToolAsync(
                "get_task", new Dictionary<string, object?>(), cancellationToken: ct);
            if (assignment.IsError == true)
                throw new InvalidOperationException("get_task returned an error: " + TextOf(assignment));

            var assignmentJson = TextOf(assignment);
            await File.WriteAllTextAsync(Path.Combine(cwd, "get_task.json"), assignmentJson, ct);

            // ── Report a result (§10) — drives working → verifying. No real work; a
            //    reference is all the state machine requires (verification is separate).
            var reported = await client.CallToolAsync(
                "report_result",
                new Dictionary<string, object?> { ["resultReference"] = "docket-worker-harness:done" },
                cancellationToken: ct);
            if (reported.IsError == true)
                throw new InvalidOperationException("report_result returned an error: " + TextOf(reported));

            return 0;
        }
        catch (Exception e)
        {
            try { await File.WriteAllTextAsync(Path.Combine(cwd, "harness_error.txt"), e.ToString(), CancellationToken.None); }
            catch { /* best effort */ }
            return 1;
        }
    }

    /// <summary>
    /// Resolves the plane URL and Authorization header. Primary source is the
    /// injected <c>--mcp-config</c> file (proves the §13 injection path); falls
    /// back to <c>DOCKET_WORKER_TOKEN</c> + <c>DOCKET_MCP_URL</c> if no config
    /// path was passed.
    /// </summary>
    private static (string Url, string Authorization) ResolveConnection(string[] args)
    {
        var configPath = FlagValue(args, "--mcp-config");
        if (configPath is not null)
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath))
                ?? throw new InvalidOperationException($"empty mcp config at {configPath}");
            // The first server under mcpServers — the plane, named "docket" by docketd.
            var servers = root["mcpServers"]?.AsObject()
                ?? throw new InvalidOperationException("mcp config has no mcpServers");
            var server = servers.First().Value
                ?? throw new InvalidOperationException("mcp config has no server entry");
            var url = (string?)server["url"]
                ?? throw new InvalidOperationException("mcp config server has no url");
            var authorization = (string?)server["headers"]?["Authorization"]
                ?? throw new InvalidOperationException("mcp config server has no Authorization header");
            return (url, authorization);
        }

        var token = Environment.GetEnvironmentVariable("DOCKET_WORKER_TOKEN")
            ?? throw new InvalidOperationException("no --mcp-config and no DOCKET_WORKER_TOKEN");
        var mcpUrl = Environment.GetEnvironmentVariable("DOCKET_MCP_URL")
            ?? throw new InvalidOperationException("no --mcp-config and no DOCKET_MCP_URL");
        return (mcpUrl, $"Bearer {token}");
    }

    private static string? FlagValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    private static string TextOf(CallToolResult result)
    {
        // A typed tool return (get_task) also surfaces as structured content; a
        // string return (report_result) is a text block. Prefer structured, fall
        // back to the joined text blocks.
        if (result.StructuredContent is { } structured)
            return structured.GetRawText();
        return string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }
}
