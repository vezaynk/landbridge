using System.Net.WebSockets;
using Docket.Relay;

namespace Docket.Relay.Tests;

/// <summary>Test doubles and WebSocket helpers for the relay splice tests.</summary>
internal static class RelayTestKit
{
    /// <summary>A grant that is always accepted (a stand-in for a valid grant).</summary>
    public const string GoodGrant = "test-grant";

    private const int ReceiveBufferSize = 64 * 1024;

    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Send <paramref name="data"/> as a single binary message.</summary>
    public static Task SendAsync(WebSocket ws, byte[] data, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, endOfMessage: true, ct);

    /// <summary>
    /// Send <paramref name="data"/> as several back-to-back binary messages, to
    /// prove multi-write fidelity through the splice.
    /// </summary>
    public static async Task SendChunkedAsync(WebSocket ws, byte[] data, int chunkSize, CancellationToken ct)
    {
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, data.Length - offset);
            await ws.SendAsync(
                new ArraySegment<byte>(data, offset, length),
                WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }

    /// <summary>
    /// Read exactly <paramref name="count"/> bytes, accumulating across whatever
    /// frame boundaries the relay produces (a payload larger than the relay's
    /// receive buffer arrives fragmented).
    /// </summary>
    public static async Task<byte[]> ReceiveExactlyAsync(WebSocket ws, int count, CancellationToken ct)
    {
        var received = new byte[count];
        var offset = 0;
        var buffer = new byte[ReceiveBufferSize];
        while (offset < count)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"peer closed after {offset}/{count} bytes");
            Buffer.BlockCopy(buffer, 0, received, offset, result.Count);
            offset += result.Count;
        }

        return received;
    }

    public static void AssertBytesEqual(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.AsSpan().SequenceEqual(actual), "relayed bytes differ from what was sent");
    }

    /// <summary>Send a close frame (best-effort) and dispose the client.</summary>
    public static async Task ShutdownAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }

        ws.Dispose();
    }

    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }

        return condition();
    }
}

/// <summary>An <see cref="IGrantValidator"/> whose verdict is supplied by a delegate.</summary>
internal sealed class DelegateGrantValidator(Func<string, string, RelayRole, bool> predicate) : IGrantValidator
{
    public static DelegateGrantValidator Accept { get; } = new((_, _, _) => true);

    public static DelegateGrantValidator Reject { get; } = new((_, _, _) => false);

    public Task<bool> ValidateAsync(string grant, string forwardId, RelayRole role, CancellationToken ct) =>
        Task.FromResult(predicate(grant, forwardId, role));
}
