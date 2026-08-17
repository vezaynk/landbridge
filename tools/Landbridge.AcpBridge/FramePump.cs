using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Docket.AcpBridge;

/// <summary>
/// One JSON-RPC object per NDJSON line on the stream, one text frame on the
/// socket. Receive and send run concurrently; that is the supported
/// <see cref="WebSocket"/> pattern.
/// </summary>
internal static class FramePump
{
    public static async Task RunAsync(
        WebSocket ws, TextReader incoming, TextWriter outgoing, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        var toWs = CopyLinesToSocketAsync(incoming, ws, token);
        var fromWs = CopyFramesToWriterAsync(ws, outgoing, token);

        var first = await Task.WhenAny(toWs, fromWs).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(toWs, fromWs).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* the other side ended */ }

        await first.ConfigureAwait(false);
    }

    private static async Task CopyLinesToSocketAsync(
        TextReader incoming, WebSocket ws, CancellationToken ct)
    {
        while (await incoming.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (ws.State != WebSocketState.Open)
                break;
            var bytes = Encoding.UTF8.GetBytes(line);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task CopyFramesToWriterAsync(
        WebSocket ws, TextWriter outgoing, CancellationToken ct)
    {
        var scratch = new byte[16 * 1024];
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            buffer.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(scratch, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (result.MessageType == WebSocketMessageType.Binary)
                    continue;
                buffer.Write(scratch.AsSpan(0, result.Count));
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(buffer.WrittenSpan);
            await outgoing.WriteLineAsync(text.AsMemory(), ct).ConfigureAwait(false);
            await outgoing.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
