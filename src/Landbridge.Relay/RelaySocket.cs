using System.Net.WebSockets;

namespace Landbridge.Relay;

/// <summary>Best-effort WebSocket teardown helpers used across the relay.</summary>
internal static class RelaySocket
{
    /// <summary>
    /// Send a close frame and return without waiting for the peer's
    /// acknowledgement. Bounded and exception-swallowing: teardown must never
    /// hang or throw, whatever state the socket is in.
    /// </summary>
    public static async Task CloseOutputQuietlyAsync(WebSocket socket, string description)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, description, cts.Token);
        }
        catch (Exception)
        {
            // Best-effort: the peer may already be gone, or the socket aborted.
        }
    }
}
