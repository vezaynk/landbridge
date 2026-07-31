using System.Net.WebSockets;
using System.Text;

namespace Docket.Preview.Tests;

/// <summary>
/// Frontend-edge guard for the websocket close handshake (spec §8.4: a graceful
/// close path matters — real browsers complete close handshakes). These stress
/// both a browser-initiated and a server-initiated close, sequential and
/// concurrent, against the fake bridge — so the frontend's own splice teardown
/// never truncates a close. (The relay/producer teardown fix that this depends on
/// end-to-end lives in Docket.Relay/Docket.Runner and is guarded there + by the
/// L3 stress in Docket.Mcp.Tests.)
/// </summary>
public sealed class PreviewCloseHandshakeTests
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;
    private const int Iterations = 60;

    [Fact]
    public async Task Browser_initiated_close_completes_cleanly_under_stress()
    {
        await using var upstream = await UpstreamService.StartAsync();
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        for (var i = 0; i < Iterations; i++)
        {
            using var ws = await PreviewTestKit.BrowserWebSocketAsync(harness.Port, "label1", "/ws", Ct);
            await ws.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, Ct);
            var buffer = new byte[64];
            var echo = await ws.ReceiveAsync(buffer, Ct);
            Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, echo.Count));

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
            Assert.Equal(WebSocketState.Closed, ws.State);
        }
    }

    [Fact]
    public async Task Server_initiated_close_is_delivered_to_the_browser_under_stress()
    {
        await using var upstream = await UpstreamService.StartAsync();
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        for (var i = 0; i < Iterations; i++)
        {
            using var ws = await PreviewTestKit.BrowserWebSocketAsync(harness.Port, "label1", "/wsclose", Ct);

            var buffer = new byte[64];
            var data = await ws.ReceiveAsync(buffer, Ct);
            Assert.Equal("last", Encoding.UTF8.GetString(buffer, 0, data.Count));

            var close = await ws.ReceiveAsync(buffer, Ct);
            Assert.Equal(WebSocketMessageType.Close, close.MessageType);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, ws.CloseStatus);
        }
    }

    [Fact]
    public async Task Concurrent_browser_closes_all_complete_cleanly()
    {
        await using var upstream = await UpstreamService.StartAsync();
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        async Task OneAsync()
        {
            using var ws = await PreviewTestKit.BrowserWebSocketAsync(harness.Port, "label1", "/ws", Ct);
            await ws.SendAsync(Encoding.UTF8.GetBytes("hi"), WebSocketMessageType.Text, true, Ct);
            var buffer = new byte[64];
            await ws.ReceiveAsync(buffer, Ct);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
            Assert.Equal(WebSocketState.Closed, ws.State);
        }

        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => OneAsync()));
    }
}
