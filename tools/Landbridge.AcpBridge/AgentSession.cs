using System.Diagnostics;
using System.Net.WebSockets;

namespace Docket.AcpBridge;

/// <summary>One WebSocket ↔ one spawned ACP agent, for the life of that socket.</summary>
internal static class AgentSession
{
    public static async Task RunAsync(WebSocket ws, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);

        using var agent = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start " + argv[0]);

        var stderr = DrainAsync(agent.StandardError, ct);
        try
        {
            await FramePump.RunAsync(ws, agent.StandardOutput, agent.StandardInput, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { agent.StandardInput.Close(); } catch { /* already closed */ }
            if (!agent.HasExited)
            {
                try { agent.Kill(entireProcessTree: true); } catch { /* gone */ }
            }

            try { await agent.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* disposing */ }
            try { await stderr.ConfigureAwait(false); } catch { /* disposing */ }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                await Console.Error.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* listener going down */
        }
    }
}
