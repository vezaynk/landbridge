using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Preview;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The §8.4 crown: an HTTP preview end to end with <b>no fakes on the data path</b>.
/// A real control plane (preview mapping + connect + grant service), a real relay
/// validating grants against that plane, a real producer <c>landbridged</c> that dials
/// a real upstream HTTP/WS service on demand, and the real preview frontend as the
/// consumer end. A browser HTTP request and a WebSocket both round-trip
/// frontend → real relay → producer-landbridged → upstream and back, proving the
/// frontend's consumer tunnel client is wire-compatible with the unchanged relay.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PreviewEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string RelayBearer = "relay-shared-secret-under-test";
    private const string PreviewBearer = "preview-shared-secret-under-test";
    private const string Domain = "preview.localhost";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Public_preview_round_trips_http_and_websocket_through_the_real_relay()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // ── The producer's registered service: a real HTTP + WS upstream ─────────
        await using var upstream = await PreviewUpstream.StartAsync();
        var team = TeamId.New();
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "web", ct, port: upstream.Port);

        // ── Plane + relay on a pre-reserved relay URL (no build-order race) ─────
        var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, RelayBearer, relayUrl, PreviewBearer);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(
            RelayGrantTestKit.BaseUri(plane).ToString(), RelayBearer, listenUrl: relayUrl);
        await relay.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = plane.Services.GetRequiredService<RunnerEventSink>();

        // ── A real producer landbridged, dialing the upstream on demand ─────────────
        var producerChannel = new SinkForwardingChannel(sink);
        await using var producerDaemon = new DaemonHarness("mp", producerChannel);
        await producerDaemon.StartAsync();
        registry.Register("mp", new HashSet<string> { "default" }, producerDaemon.Send);
        registry.TrackDispatch("mp", producerTask);

        // ── A real preview mapping (public), minted against the fixture DB ──────
        string label;
        await using (var db = pg.NewContext())
        {
            var mint = await new PreviewMappingService(db, TimeProvider.System)
                .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30), ct);
            label = mint.Label;
        }

        // ── The real preview frontend as the consumer end ──────────────────────
        await using var frontend = PreviewFrontendHarness.Start(RelayGrantTestKit.BaseUri(plane).ToString());

        // ── HTTP request round-trips through the whole chain, unrewritten ───────
        using (var browser = frontend.Browser())
        using (var response = await browser.GetAsync($"http://{label}.{Domain}/deep/path?x=1", ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("hello from upstream", await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("/deep/path?x=1", response.Headers.GetValues("X-Echo-Path").First());
            Assert.Equal($"{label}.{Domain}", response.Headers.GetValues("X-Echo-Host").First());
        }

        // ── WebSocket upgrade round-trips + echoes bidirectionally ──────────────
        using (var ws = await frontend.BrowserWebSocketAsync(label, "/ws", ct))
        {
            Assert.Equal(WebSocketState.Open, ws.State);
            await ws.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, ct);
            var buffer = new byte[256];
            var result = await ws.ReceiveAsync(buffer, ct);
            Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, result.Count));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        }

        await producerDaemon.StopAsync();
        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Gated_preview_is_refused_without_an_operator_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var upstream = await PreviewUpstream.StartAsync();
        var team = TeamId.New();
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "web", ct, port: upstream.Port);

        var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, RelayBearer, relayUrl, PreviewBearer);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(
            RelayGrantTestKit.BaseUri(plane).ToString(), RelayBearer, listenUrl: relayUrl);
        await relay.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var producerDaemon = new DaemonHarness("mp", new SinkForwardingChannel(plane.Services.GetRequiredService<RunnerEventSink>()));
        await using var _ = producerDaemon;
        await producerDaemon.StartAsync();
        registry.Register("mp", new HashSet<string> { "default" }, producerDaemon.Send);
        registry.TrackDispatch("mp", producerTask);

        string label;
        await using (var db = pg.NewContext())
        {
            var mint = await new PreviewMappingService(db, TimeProvider.System)
                .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(30), ct);
            label = mint.Label;
        }

        var planeBase = RelayGrantTestKit.BaseUri(plane).ToString();
        await using var frontend = PreviewFrontendHarness.Start(planeBase, dashboardUrl: planeBase);

        // Gated + no session: a browser is 302'd to the dashboard confirm (not 401).
        using (var browser = frontend.Browser(followRedirects: false))
        using (var response = await browser.GetAsync($"http://{label}.{Domain}/", ct))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.StartsWith($"{planeBase.TrimEnd('/')}/dashboard/preview-auth", response.Headers.Location!.ToString());
        }

        // The tooling path: a live Lead bearer on the mapping's Team admits directly.
        var leadToken = await RelayGrantTestKit.LeadTokenAsync(pg, team, ct);
        using (var browser = frontend.Browser())
        {
            browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", leadToken);
            using var response = await browser.GetAsync($"http://{label}.{Domain}/", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await producerDaemon.StopAsync();
        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Gated_preview_full_browser_flow_redirect_code_cookie_content()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var upstream = await PreviewUpstream.StartAsync();
        var team = TeamId.New();
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "web", ct, port: upstream.Port);

        var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, RelayBearer, relayUrl, PreviewBearer);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(
            RelayGrantTestKit.BaseUri(plane).ToString(), RelayBearer, listenUrl: relayUrl);
        await relay.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        await using var producerDaemon = new DaemonHarness("mp", new SinkForwardingChannel(plane.Services.GetRequiredService<RunnerEventSink>()));
        await producerDaemon.StartAsync();
        registry.Register("mp", new HashSet<string> { "default" }, producerDaemon.Send);
        registry.TrackDispatch("mp", producerTask);

        string label;
        await using (var db = pg.NewContext())
            label = (await new PreviewMappingService(db, TimeProvider.System)
                .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(30), ct)).Label;

        var planeBase = RelayGrantTestKit.BaseUri(plane).ToString().TrimEnd('/');
        await using var frontend = PreviewFrontendHarness.Start(planeBase, dashboardUrl: planeBase);
        var operatorSession = await RelayGrantTestKit.LeadTokenAsync(pg, team, ct); // a §12 operator session

        // Hop 1 — browser hits the gated preview with nothing → 302 to the dashboard confirm.
        using var browser = frontend.Browser(followRedirects: false);
        var authRedirect = (await browser.GetAsync($"http://{label}.{Domain}/app", ct)).Headers.Location!.ToString();
        Assert.StartsWith($"{planeBase}/dashboard/preview-auth", authRedirect);

        // Hop 2 — the operator confirms at the dashboard origin (landbridge_session) → 302 back with a code.
        using var dashboard = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var authReq = new HttpRequestMessage(HttpMethod.Get, authRedirect);
        authReq.Headers.Add("Cookie", $"landbridge_session={operatorSession}");
        var back = (await dashboard.SendAsync(authReq, ct)).Headers.Location!.ToString();
        Assert.Contains("landbridge_preview_code=", back);
        Assert.StartsWith($"http://{label}.{Domain}/app", back);

        // Hop 3 — browser carries the code back to the preview origin → cookie set, clean redirect.
        HttpResponseMessage exchange = await browser.GetAsync(back, ct);
        Assert.Equal(HttpStatusCode.Found, exchange.StatusCode);
        Assert.Equal("/app", exchange.Headers.Location!.ToString());
        var setCookie = Assert.Single(exchange.Headers.GetValues("Set-Cookie"));
        var previewCookie = setCookie.Split(';')[0]; // landbridge_preview=…
        Assert.StartsWith("landbridge_preview=", previewCookie);
        exchange.Dispose();

        // Hop 4 — the clean URL with the per-label cookie → content, end to end through the real relay.
        using var content = new HttpRequestMessage(HttpMethod.Get, $"http://{label}.{Domain}/app");
        content.Headers.Add("Cookie", previewCookie);
        using var final = await browser.SendAsync(content, ct);
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        Assert.Equal("hello from upstream", await final.Content.ReadAsStringAsync(ct));

        await producerDaemon.StopAsync();
        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }
}

/// <summary>Runs the real preview frontend (plaintext, loopback) pointed at the plane's /preview/connect.</summary>
internal sealed class PreviewFrontendHarness : IAsyncDisposable
{
    private const string Domain = "preview.localhost";
    private const string PreviewBearer = "preview-shared-secret-under-test";

    private readonly PreviewServer _server;
    private readonly ServiceProvider _sp;

    private PreviewFrontendHarness(PreviewServer server, ServiceProvider sp)
    {
        _server = server;
        _sp = sp;
    }

    public static PreviewFrontendHarness Start(string controlPlaneUrl, string? dashboardUrl = null)
    {
        var options = new PreviewOptions
        {
            Domain = Domain,
            ControlPlaneUrl = controlPlaneUrl,
            DashboardUrl = dashboardUrl ?? controlPlaneUrl,
            ControlPlaneBearer = PreviewBearer,
            FirstByteTimeout = TimeSpan.FromSeconds(30),
        };
        var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var client = new PreviewControlPlaneClient(
            sp.GetRequiredService<IHttpClientFactory>(), Options.Create(options),
            NullLogger<PreviewControlPlaneClient>.Instance);
        var server = new PreviewServer(
            new IPEndPoint(IPAddress.Loopback, 0), certificates: null, options, client,
            NullLogger<PreviewServer>.Instance);
        server.Start();
        return new PreviewFrontendHarness(server, sp);
    }

    public int Port => _server.BoundPort;

    public HttpClient Browser(bool followRedirects = true) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, _server.BoundPort, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });

    public async Task<ClientWebSocket> BrowserWebSocketAsync(string label, string path, CancellationToken ct)
    {
        var invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            ConnectCallback = async (_, c) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, _server.BoundPort, c);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://{label}.{Domain}{path}"), invoker, ct);
        return ws;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _sp.DisposeAsync();
    }
}

/// <summary>A real upstream HTTP + WebSocket service the producer task registers and landbridged dials.</summary>
internal sealed class PreviewUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    private PreviewUpstream(WebApplication app) => _app = app;

    public int Port => new Uri(_app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))).Port;

    public static async Task<PreviewUpstream> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();

        app.Map("/ws", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(buffer, http.RequestAborted);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", http.RequestAborted);
                        return;
                    }
                    await ws.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, http.RequestAborted);
                }
            }
            catch { /* client went away */ }
        });

        app.MapFallback(async (HttpContext http) =>
        {
            http.Response.Headers["X-Echo-Path"] = http.Request.Path + http.Request.QueryString;
            http.Response.Headers["X-Echo-Host"] = http.Request.Host.Value;
            await http.Response.WriteAsync("hello from upstream");
        });

        await app.StartAsync();
        return new PreviewUpstream(app);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
