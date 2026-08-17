using System.Diagnostics;
using System.Text;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// The listen half of <c>docket-acp-bridge</c> as a test fixture: bind loopback,
/// spawn the agent argv on each WebSocket, expose the <c>ws://</c> URL.
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
        var dll = typeof(Docket.AcpBridge.Program).Assembly.Location;
        var dir = Path.GetDirectoryName(dll)!;
        var stem = Path.GetFileNameWithoutExtension(dll);
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        if (!File.Exists(apphost))
            throw new FileNotFoundException("docket-acp-bridge apphost not found at " + apphost);
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
            try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
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
