using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Landbridge.Preview.Tests;

/// <summary>
/// L2 for the HTTP preview frontend (spec §8.4): the real <see cref="Landbridge.Preview.PreviewServer"/>
/// against a fake control plane + fake tunnel bridge + a real upstream. Covers Host
/// routing, auth-policy delegation, the byte-faithful HTTP proxy (no rewrite),
/// bidirectional WebSocket, N concurrent connections, and the failure modes that
/// must return clean statuses instead of hanging.
/// </summary>
public sealed class PreviewFrontendTests
{
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Fact]
    public async Task Http_request_round_trips_and_nothing_is_rewritten()
    {
        await using var upstream = await UpstreamService.StartAsync();
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        using var browser = PreviewTestKit.Browser(harness.Port);
        using var response = await browser.GetAsync("http://label1.preview.localhost/deep/path?x=1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello from upstream", await response.Content.ReadAsStringAsync(Ct));
        // The served app's path, Host, cookie, and absolute redirect all pass through
        // unrewritten — the whole point of splicing rather than re-emitting (§8.4).
        Assert.Equal("/deep/path?x=1", First(response, "X-Echo-Path"));
        Assert.Equal("label1.preview.localhost", First(response, "X-Echo-Host"));
        Assert.Contains("sess=abc123", response.Headers.GetValues("Set-Cookie").First());
        Assert.Equal("https://example.com/absolute/target", First(response, "Location"));
    }

    [Fact]
    public async Task Websocket_upgrades_and_echoes_bidirectionally()
    {
        await using var upstream = await UpstreamService.StartAsync();
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        using var ws = await PreviewTestKit.BrowserWebSocketAsync(harness.Port, "label1", "/ws", Ct);
        Assert.Equal(WebSocketState.Open, ws.State);

        foreach (var message in new[] { "first frame", "second frame" })
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, Ct);
            var buffer = new byte[1024];
            var result = await ws.ReceiveAsync(buffer, Ct);
            Assert.Equal(message, Encoding.UTF8.GetString(buffer, 0, result.Count));
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    [Fact]
    public async Task N_concurrent_connections_are_N_distinct_forwards()
    {
        await using var upstream = await UpstreamService.StartAsync();
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        const int n = 8;
        var browsers = Enumerable.Range(0, n).Select(_ => PreviewTestKit.Browser(harness.Port)).ToArray();
        try
        {
            var responses = await Task.WhenAll(browsers.Select(b =>
                b.GetAsync("http://label1.preview.localhost/", Ct)));
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        }
        finally
        {
            foreach (var b in browsers) b.Dispose();
        }

        // N browser connections minted N /preview/connect calls (§8.4: N forwards).
        Assert.Equal(n, cp.Calls.Count);
    }

    [Fact]
    public async Task Unknown_host_is_404_and_never_reaches_the_plane()
    {
        await using var cp = await FakeControlPlane.StartAsync();
        await using var harness = PreviewHarness.Start(cp.Url);

        using var browser = PreviewTestKit.Browser(harness.Port);
        using var response = await browser.GetAsync("http://not-our-domain.example.com/", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(cp.Calls); // routing refused before any orchestration
    }

    // 401 is deliberately absent: a browser's gated-401 becomes a redirect to the
    // dashboard confirm, and a bearer's stays a 401 — both covered in
    // PreviewGatedFlowTests. This theory covers the statuses relayed verbatim.
    [Theory]
    [InlineData(StatusCodes.Status404NotFound, HttpStatusCode.NotFound)]
    [InlineData(StatusCodes.Status410Gone, HttpStatusCode.Gone)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Control_plane_refusals_are_relayed_as_clean_statuses(int planeStatus, HttpStatusCode expected)
    {
        await using var cp = await FakeControlPlane.StartAsync();
        cp.Handler = _ => Results.StatusCode(planeStatus);
        await using var harness = PreviewHarness.Start(cp.Url);

        using var browser = PreviewTestKit.Browser(harness.Port);
        using var response = await browser.GetAsync("http://label1.preview.localhost/", Ct);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task No_producer_pair_is_a_clean_502()
    {
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.Mode = FakeTunnelBridge.Behaviour.CloseImmediately; // relay closes with no pair
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        using var browser = PreviewTestKit.Browser(harness.Port);
        using var response = await browser.GetAsync("http://label1.preview.localhost/", Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Silent_upstream_is_a_clean_504()
    {
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.Mode = FakeTunnelBridge.Behaviour.BlackHole; // tunnel opens but no upstream byte
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url, firstByteTimeout: TimeSpan.FromMilliseconds(750));

        using var browser = PreviewTestKit.Browser(harness.Port);
        using var response = await browser.GetAsync("http://label1.preview.localhost/", Ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task Operator_session_and_frontend_bearer_are_forwarded_distinctly()
    {
        await using var upstream = await UpstreamService.StartAsync();
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        // The browser's operator session (Authorization bearer here) is forwarded in
        // the connect BODY; the frontend's own shared bearer authenticates the call.
        using var browser = PreviewTestKit.Browser(harness.Port);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "operator-token");
        using var response = await browser.GetAsync("http://label1.preview.localhost/", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(cp.Calls.TryDequeue(out var call));
        Assert.Equal("label1", call!.Label);
        Assert.Equal("operator-token", call.OperatorSession);            // browser session, in the body
        Assert.Equal("preview-shared-secret-under-test", call.Bearer);   // frontend bearer, in the header
    }

    [Fact]
    public async Task Operator_session_from_the_cookie_is_forwarded()
    {
        await using var upstream = await UpstreamService.StartAsync();
        await using var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        await using var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        await using var harness = PreviewHarness.Start(cp.Url);

        using var browser = PreviewTestKit.Browser(harness.Port);
        browser.DefaultRequestHeaders.Add("Cookie", "landbridge_session=cookie-token");
        using var response = await browser.GetAsync("http://label1.preview.localhost/", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(cp.Calls.TryDequeue(out var call));
        Assert.Equal("cookie-token", call!.OperatorSession);
    }

    private static string? First(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var v) ? v.First()
        : response.Content.Headers.TryGetValues(header, out var cv) ? cv.First()
        : null;
}
