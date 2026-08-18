using System.Net;
using System.Net.Http.Headers;

namespace Landbridge.Preview.Tests;

/// <summary>
/// L2 for the §8.4 gated browser flow + head-strip at the frontend edge: a gated
/// preview with no session bounces a browser to the dashboard confirm (302, not
/// 401); a returned one-time code is exchanged for a per-label cookie; and every
/// spliced request has its operator auth material stripped before reaching the
/// upstream. Drives the real <see cref="Landbridge.Preview.PreviewServer"/> against the
/// fake control plane + bridge + a real upstream that echoes what it received.
/// </summary>
public sealed class PreviewGatedFlowTests
{
    private const string Dashboard = "http://dashboard.test";
    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static async Task<(UpstreamService, FakeTunnelBridge, FakeControlPlane, PreviewHarness)> GatedStackAsync()
    {
        var upstream = await UpstreamService.StartAsync();
        var bridge = await FakeTunnelBridge.StartAsync();
        bridge.UpstreamPort = upstream.Port;
        var cp = await FakeControlPlane.StartAsync();
        cp.RelayUrl = bridge.Url;
        cp.RequiredPreviewSession = "sess-ok"; // gated: only this per-label session admits
        var harness = PreviewHarness.Start(cp.Url, dashboardUrl: Dashboard);
        return (upstream, bridge, cp, harness);
    }

    [Fact]
    public async Task Gated_without_a_session_redirects_a_browser_to_the_dashboard_not_401()
    {
        var (upstream, bridge, cp, harness) = await GatedStackAsync();
        await using var _ = upstream;
        await using var __ = bridge;
        await using var ___ = cp;
        await using var ____ = harness;

        using var browser = PreviewTestKit.Browser(harness.Port, followRedirects: false);
        using var response = await browser.GetAsync("http://label1.preview.localhost/app?x=1", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode); // 302, not 401
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith($"{Dashboard}/dashboard/preview-auth", location);
        Assert.Contains("label=label1", location);
        Assert.Contains("return=", location);
    }

    [Fact]
    public async Task Gated_tooling_with_a_bearer_gets_401_not_a_redirect()
    {
        var (upstream, bridge, cp, harness) = await GatedStackAsync();
        await using var _ = upstream;
        await using var __ = bridge;
        await using var ___ = cp;
        await using var ____ = harness;

        using var browser = PreviewTestKit.Browser(harness.Port, followRedirects: false);
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "some-lead-token");
        using var response = await browser.GetAsync("http://label1.preview.localhost/", Ct);

        // A bearer is the tooling path — a flat 401, never an interactive redirect.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_returned_code_is_exchanged_for_a_cookie_and_redirects_to_the_clean_url()
    {
        var (upstream, bridge, cp, harness) = await GatedStackAsync();
        await using var _ = upstream;
        await using var __ = bridge;
        await using var ___ = cp;
        await using var ____ = harness;

        using var browser = PreviewTestKit.Browser(harness.Port, followRedirects: false);
        using var response = await browser.GetAsync(
            "http://label1.preview.localhost/app?x=1&landbridge_preview_code=good-code", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // Clean redirect: the one-time code is stripped from the target.
        Assert.Equal("/app?x=1", response.Headers.Location!.ToString());
        // The frontend set its own per-label HttpOnly cookie on the preview origin.
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("landbridge_preview=sess-ok", setCookie);
        Assert.Contains("HttpOnly", setCookie);
    }

    [Fact]
    public async Task A_valid_preview_cookie_admits_and_auth_material_is_stripped_before_the_upstream()
    {
        var (upstream, bridge, cp, harness) = await GatedStackAsync();
        await using var _ = upstream;
        await using var __ = bridge;
        await using var ___ = cp;
        await using var ____ = harness;

        using var browser = PreviewTestKit.Browser(harness.Port);
        // The browser carries the per-label preview cookie, an operator session
        // cookie, an unrelated cookie, and a bearer — all of which except the
        // unrelated cookie must be stripped before the upstream sees the request.
        browser.DefaultRequestHeaders.Add("Cookie", "landbridge_preview=sess-ok; landbridge_session=op-token; keep=me");
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "op-bearer");
        using var response = await browser.GetAsync("http://label1.preview.localhost/secret", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello from upstream", await response.Content.ReadAsStringAsync(Ct));

        // HEAD-STRIP: the upstream saw no Authorization and neither auth cookie...
        Assert.Equal("", First(response, "X-Echo-Auth"));
        var echoedCookie = First(response, "X-Echo-Cookie");
        Assert.DoesNotContain("landbridge_preview", echoedCookie);
        Assert.DoesNotContain("landbridge_session", echoedCookie);
        // ...but a non-auth cookie is preserved (never-rewrite holds for app content).
        Assert.Contains("keep=me", echoedCookie);

        // The plane's connect saw the preview session forwarded from the cookie.
        Assert.Contains(cp.Calls, c => c.PreviewSession == "sess-ok");
    }

    private static string First(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var v) ? v.First()
        : response.Content.Headers.TryGetValues(header, out var cv) ? cv.First()
        : "";
}
