using System.Text;
using System.Text.Json;

namespace Docket.Runner.TestHarness;

/// <summary>
/// Tiny ACP agent for runner tests. Owns stdin as JSON-RPC. Inner mode runs
/// on <c>session/prompt</c> and must not steal stdin.
/// </summary>
internal static class AcpServer
{
    public static async Task<int> RunAsync(string[] innerArgs)
    {
        var cwd = Directory.GetCurrentDirectory();
        var inner = innerArgs.Length > 0 ? innerArgs[0] : "run";
        string? sessionId = null;
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
                    sessionId = "sess-" + Guid.NewGuid().ToString("N")[..12];
                    await Program.WriteMarkerAtomicAsync(Path.Combine(cwd, "acp-session"), sessionId);
                    await ReplyAsync(id, "{\"sessionId\":\"" + sessionId + "\"}");
                    break;
                case "session/load":
                    sessionId = msg.GetProperty("params").GetProperty("sessionId").GetString();
                    await Program.WriteMarkerAtomicAsync(Path.Combine(cwd, "acp-loaded"), sessionId ?? "");
                    await ReplyAsync(id, "null");
                    break;
                case "session/prompt":
                    await RunInnerAsync(inner, innerArgs, cwd);
                    if (inner is "emit-stream" or "emit-both")
                        await EmitToolCallsAsync();
                    if (inner is "emit-both")
                    {
                        foreach (var err in Program.EmitBothStderrLines)
                            await Console.Error.WriteLineAsync(err);
                        await Console.Error.FlushAsync();
                    }
                    await ReplyAsync(id, """{"stopReason":"end_turn"}""");
                    if (IsOneShot(inner))
                        done.TrySetResult(0);
                    break;
                case "session/cancel":
                    await Program.WriteMarkerAtomicAsync(Path.Combine(cwd, "stopped"), "acp-cancel");
                    if (inner != "ignore-cancel")
                        return 0;
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
        return Program.DeadManExitCode;
    }

    private static bool IsOneShot(string inner) =>
        inner is "exit-code" or "hook-ok" or "hook-fail" or "hook-env";

    private static async Task RunInnerAsync(string inner, string[] args, string cwd)
    {
        switch (inner)
        {
            case "echo-env":
                await Program.WriteMarkerAtomicAsync(Path.Combine(cwd, "env"), Program.EnvironmentLines());
                break;
            case "echo-argv":
                await Program.WriteMarkerAtomicAsync(Path.Combine(cwd, "argv"), string.Join('\n', args));
                break;
            case "run":
            case "stdin-stop":
            case "emit-stream":
            case "emit-both":
                await Program.WriteStartedAsync(cwd);
                break;
            case "spawn-child":
                await Program.WriteStartedAsync(cwd);
                {
                    var grandchild = Program.SpawnSelf("child");
                    await Program.WriteMarkerAtomicAsync(Path.Combine(cwd, "child.pid"), grandchild.Id.ToString());
                }
                break;
            case "exit-code":
                break;
            default:
                await Program.WriteStartedAsync(cwd);
                break;
        }
    }

    private static async Task EmitToolCallsAsync()
    {
        foreach (var name in Program.EmitStreamToolNames)
        {
            await Console.Out.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s\",\"update\":{"
                + "\"sessionUpdate\":\"tool_call\",\"title\":\"" + name + "\"}}}");
        }
        await Console.Out.FlushAsync();
    }

    private static async Task ReplyAsync(string? id, string resultJson)
    {
        if (id is null) return;
        await Console.Out.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}");
        await Console.Out.FlushAsync();
    }

}
