using System.Text.Json;

namespace Docket.HarnessSupport;

/// <summary>
/// Tiny ACP agent loop for scripted workers. Owns stdin as JSON-RPC.
/// <c>session/new</c> carries the plane MCP server; <c>session/prompt</c> runs
/// the assignment. stdin EOF is the dead-man.
/// </summary>
public static class AcpStdio
{
    public const int DeadManExitCode = 66;

    public sealed record Session(string SessionId, string? McpUrl, string? Authorization);

    /// <summary>
    /// Run the ACP loop. <paramref name="onPrompt"/> does the assignment.
    /// A zero return keeps the process in the loop (another prompt or cancel);
    /// non-zero ends the process. Long-running serve modes should block until
    /// <paramref name="cts"/> is cancelled, then return 0.
    /// </summary>
    public static async Task<int> RunAsync(
        Func<Session, CancellationToken, Task<int>> onPrompt,
        CancellationTokenSource cts)
    {
        string? sessionId = null;
        string? mcpUrl = Environment.GetEnvironmentVariable("DOCKET_MCP_URL");
        string? authorization = EnvBearer();
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            JsonElement msg;
            try { msg = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { continue; }

            var id = msg.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : null;
            var method = msg.TryGetProperty("method", out var m) ? m.GetString() : null;
            if (method is null)
                continue;

            switch (method)
            {
                case "initialize":
                    await ReplyAsync(id, """{"protocolVersion":1,"agentCapabilities":{"loadSession":true}}""");
                    break;
                case "session/new":
                case "session/load":
                    ParseMcp(msg, ref mcpUrl, ref authorization);
                    if (method == "session/load"
                        && msg.TryGetProperty("params", out var load)
                        && load.TryGetProperty("sessionId", out var loadId))
                        sessionId = loadId.GetString();
                    else
                        sessionId = "sess-" + Guid.NewGuid().ToString("N")[..12];
                    await ReplyAsync(id, "{\"sessionId\":" + JsonString(sessionId ?? "sess-unknown") + "}");
                    break;
                case "session/prompt":
                    var promptId = id;
                    var session = new Session(sessionId ?? "sess-unknown", mcpUrl, authorization);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var code = await onPrompt(session, cts.Token);
                            await ReplyAsync(promptId, """{"stopReason":"end_turn"}""");
                            if (code != 0)
                                done.TrySetResult(code);
                        }
                        catch (OperationCanceledException)
                        {
                            await ReplyAsync(promptId, """{"stopReason":"cancelled"}""");
                            done.TrySetResult(0);
                        }
                        catch (Exception e)
                        {
                            try
                            {
                                await File.WriteAllTextAsync(
                                    Path.Combine(Directory.GetCurrentDirectory(), "harness_error.txt"),
                                    e.ToString());
                            }
                            catch { /* best effort */ }
                            await ReplyAsync(promptId, """{"stopReason":"end_turn"}""");
                            done.TrySetResult(1);
                        }
                    });
                    break;
                case "session/cancel":
                    if (!cts.IsCancellationRequested)
                        cts.Cancel();
                    break;
                default:
                    if (id is not null)
                        await ReplyAsync(id, "null");
                    break;
            }

            if (done.Task.IsCompleted)
                return await done.Task;
        }

        if (done.Task.IsCompleted)
            return await done.Task;
        if (!cts.IsCancellationRequested)
            cts.Cancel();
        return DeadManExitCode;
    }

    public static (string Url, string Authorization) RequireConnection(Session session)
    {
        var url = session.McpUrl
            ?? Environment.GetEnvironmentVariable("DOCKET_MCP_URL")
            ?? throw new InvalidOperationException("ACP session/new carried no MCP url and DOCKET_MCP_URL is unset");
        var authorization = session.Authorization ?? EnvBearer()
            ?? throw new InvalidOperationException("ACP session/new carried no Authorization and DOCKET_WORKER_TOKEN is unset");
        return (url, authorization);
    }

    private static string? EnvBearer()
    {
        var token = Environment.GetEnvironmentVariable("DOCKET_WORKER_TOKEN");
        return string.IsNullOrWhiteSpace(token) ? null : "Bearer " + token;
    }

    private static void ParseMcp(JsonElement msg, ref string? mcpUrl, ref string? authorization)
    {
        if (!msg.TryGetProperty("params", out var p))
            return;
        if (!p.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            return;
        foreach (var server in servers.EnumerateArray())
        {
            if (server.TryGetProperty("url", out var urlEl) && urlEl.GetString() is { Length: > 0 } url)
                mcpUrl = url;
            if (server.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in headers.EnumerateArray())
                {
                    var name = h.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
                        && h.TryGetProperty("value", out var v)
                        && v.GetString() is { Length: > 0 } value)
                        authorization = value;
                }
            }
            if (mcpUrl is not null && authorization is not null)
                return;
        }
    }

    private static async Task ReplyAsync(string? id, string resultJson)
    {
        if (id is null) return;
        await Console.Out.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}");
        await Console.Out.FlushAsync();
    }

    private static string JsonString(string s) =>
        JsonSerializer.Serialize(s);
}
