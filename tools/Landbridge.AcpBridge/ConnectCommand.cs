using System.Net.WebSockets;

namespace Landbridge.AcpBridge;

/// <summary>
/// Profile-side half: stdin/stdout are ACP NDJSON; the WebSocket is the remote.
/// Exits when either the pipe or the socket ends, so landbridged sees a process death.
/// </summary>
internal static class ConnectCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0].Length == 0)
            return Program.Fail("connect needs exactly one ws:// URL");

        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await ws.ConnectAsync(new Uri(args[0]), cts.Token).ConfigureAwait(false);

        var stdin = new StreamReader(Console.OpenStandardInput());
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

        try
        {
            await FramePump.RunAsync(ws, stdin, stdout, cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "eof", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    /* already gone */
                }
            }
        }
    }
}
