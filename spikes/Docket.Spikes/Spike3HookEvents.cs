using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Docket.Spikes;

/// <summary>
/// S3 — per-tool-call events with task attribution (spec §10, §17.0c).
/// Proves PostToolUse hooks fire per tool call, inherit DOCKET_SESSION_ID from
/// the spawning environment, and can report {task id, tool name} to a
/// loopback listener — the event source docketd's per-task liveness derives
/// from. The hook command is this same binary in `hook-relay` mode; there is
/// no shell script anywhere.
/// </summary>
internal static class Spike3HookEvents
{
    private const string SessionId = "spike-task-42";

    public static async Task<int> Run()
    {
        var outDir = SpikeEnv.OutDir("s3");
        var workDir = Directory.CreateDirectory(Path.Combine(outDir, "work")).FullName;

        var port = FreeLoopbackPort();
        var url = $"http://127.0.0.1:{port}/events/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();

        var events = new List<(string SessionId, string Tool)>();
        using var done = new CancellationTokenSource();
        var collector = CollectEvents(listener, events, done.Token);

        var self = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve own binary path for the hook command");
        var settingsPath = Path.Combine(outDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
        {
            hooks = new
            {
                PostToolUse = new[]
                {
                    new { hooks = new[] { new { type = "command", command = $"{self} hook-relay {url}" } } },
                },
            },
        }));

        Console.WriteLine($"== running a worker that must make several tool calls (task id: {SessionId})");
        var result = await ClaudeCli.Run(
            ["-p", "Create hello.txt containing 'hi', then run 'wc -c hello.txt', then run 'ls'. Use one tool call per action.",
             "--model", SpikeEnv.Model, "--settings", settingsPath, "--allowedTools", "Bash,Write"],
            workDir, TimeSpan.FromMinutes(3),
            environment: new Dictionary<string, string>
            {
                ["DOCKET_SESSION_ID"] = SessionId,
                ["DOCKET_MACHINE_ID"] = "spike-machine",
            });
        await File.WriteAllTextAsync(Path.Combine(outDir, "result.txt"), result.StdOut + result.StdErr);

        // Let stragglers land, then stop collecting.
        await Task.Delay(TimeSpan.FromSeconds(2));
        done.Cancel();
        listener.Stop();
        try { await collector; } catch (OperationCanceledException) { }

        Console.WriteLine("== results");
        if (events.Count == 0)
        {
            SpikeEnv.Report(false, "no hook events captured");
            return 1;
        }

        var attributed = events.Count(e => e.SessionId == SessionId);
        Console.WriteLine($"   events: {events.Count}, correctly attributed: {attributed}");
        foreach (var (sessionId, tool) in events)
            Console.WriteLine($"     {sessionId} {tool}");

        var pass = events.Count >= 3 && attributed == events.Count;
        SpikeEnv.Report(pass, pass
            ? "every tool call produced an event carrying the task id"
            : "missing events or attribution");
        return pass ? 0 : 1;
    }

    private static async Task CollectEvents(
        HttpListener listener, List<(string, string)> events, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(token);
            }
            catch (HttpListenerException)
            {
                return; // listener stopped
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync(token);
            using var json = JsonDocument.Parse(body);
            lock (events)
            {
                events.Add((
                    json.RootElement.GetProperty("sessionId").GetString() ?? "MISSING",
                    json.RootElement.GetProperty("tool").GetString() ?? "?"));
            }
            context.Response.StatusCode = 204;
            context.Response.Close();
        }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>
/// The hook side of S3: Claude Code invokes this binary per PostToolUse with
/// the hook payload on stdin; we attach DOCKET_SESSION_ID from the inherited
/// environment and POST to the spike's loopback listener.
/// </summary>
internal static class HookRelay
{
    public static async Task<int> Run(string url)
    {
        var payload = await Console.In.ReadToEndAsync();
        string tool;
        try
        {
            using var json = JsonDocument.Parse(payload);
            tool = json.RootElement.TryGetProperty("tool_name", out var name)
                ? name.GetString() ?? "?"
                : "?";
        }
        catch (JsonException)
        {
            tool = "?";
        }

        var body = JsonSerializer.Serialize(new
        {
            sessionId = Environment.GetEnvironmentVariable("DOCKET_SESSION_ID") ?? "MISSING",
            tool,
        });

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await http.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        }
        catch (Exception)
        {
            // A hook must never fail the tool call it observes.
        }
        return 0;
    }
}
