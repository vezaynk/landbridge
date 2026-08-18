using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Landbridge.Runner.Tests;

/// <summary>
/// Zero-cost proof that <c>landbridge-acp-bridge</c> is a faithful ACP pipe:
/// listen wraps <c>Landbridge.Runner.TestHarness --acp</c>, connect speaks
/// initialize + session/new over the socket, and the agent answers.
/// </summary>
public sealed class AcpBridgeLoopbackTests
{
    [Fact]
    public async Task Connect_drives_initialize_and_session_new_through_the_bridge()
    {
        using var far = AcpBridgeFarSide.Start(AcpBridgeFarSide.BridgePath(), [TestKit.HarnessPath(), "--acp"]);
        using var connect = StartConnect(far.Url);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await WriteAsync(connect, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false},"clientInfo":{"name":"bridge-test","version":"0"}}}""", cts.Token);
        var init = await ReadJsonAsync(connect, cts.Token);
        Assert.True(init.RootElement.TryGetProperty("result", out var result), init.RootElement.GetRawText());
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
        Assert.True(result.GetProperty("agentCapabilities").GetProperty("loadSession").GetBoolean());

        await WriteAsync(connect, """{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/tmp","mcpServers":[]}}""", cts.Token);
        var opened = await ReadJsonAsync(connect, cts.Token);
        Assert.True(opened.RootElement.TryGetProperty("result", out var session), opened.RootElement.GetRawText());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("sessionId").GetString()));
    }

    private static Process StartConnect(string url)
    {
        var psi = new ProcessStartInfo(AcpBridgeFarSide.BridgePath())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("connect");
        psi.ArgumentList.Add(url);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start connect");
        _ = DrainAsync(proc.StandardError);
        return proc;
    }

    private static Task WriteAsync(Process connect, string json, CancellationToken ct) =>
        connect.StandardInput.WriteLineAsync(json.AsMemory(), ct);

    private static async Task<JsonDocument> ReadJsonAsync(Process connect, CancellationToken ct)
    {
        while (true)
        {
            var line = await connect.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                throw new InvalidOperationException("connect stdout ended before a JSON-RPC reply");
            if (string.IsNullOrWhiteSpace(line))
                continue;
            return JsonDocument.Parse(line);
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
            { /* discard */ }
        }
        catch
        {
            /* process gone */
        }
    }
}

/// <summary>
/// Starts <c>landbridge-acp-bridge listen</c> and waits for the <c>listening</c> line.
/// </summary>
internal sealed class AcpBridgeFarSide : IDisposable
{
    public string Url { get; }
    private readonly Process _process;

    private AcpBridgeFarSide(Process process, string url)
    {
        _process = process;
        Url = url;
    }

    public static string BridgePath()
    {
        var dll = typeof(Landbridge.AcpBridge.Program).Assembly.Location;
        var dir = Path.GetDirectoryName(dll)!;
        var stem = Path.GetFileNameWithoutExtension(dll);
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        if (!File.Exists(apphost))
            throw new FileNotFoundException("landbridge-acp-bridge apphost not found at " + apphost);
        return apphost;
    }

    public static AcpBridgeFarSide Start(string bridge, IReadOnlyList<string> agentArgv)
    {
        var psi = new ProcessStartInfo(bridge)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("listen");
        psi.ArgumentList.Add("--bind");
        psi.ArgumentList.Add("127.0.0.1:0");
        psi.ArgumentList.Add("--");
        foreach (var arg in agentArgv)
            psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start listen");
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (stderr) stderr.AppendLine(e.Data);
        };
        proc.BeginErrorReadLine();

        string? line;
        try
        {
            line = proc.StandardOutput.ReadLine();
        }
        catch
        {
            proc.Kill(entireProcessTree: true);
            throw;
        }

        if (line is null || !line.StartsWith("listening ", StringComparison.Ordinal))
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            lock (stderr)
                throw new InvalidOperationException(
                    "listen did not print a listening URL. stdout=" + (line ?? "<eof>")
                    + " stderr=" + stderr);
        }

        return new AcpBridgeFarSide(proc, line["listening ".Length..].Trim());
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            /* already gone */
        }

        _process.Dispose();
    }
}
