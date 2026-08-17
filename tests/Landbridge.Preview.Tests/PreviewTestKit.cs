using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Docket.Preview;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Docket.Preview.Tests;

/// <summary>
/// L2 doubles for the HTTP preview frontend (spec §8.4): a fake control plane
/// (<c>/preview/connect</c>), a fake tunnel bridge standing in for relay+producer,
/// and a real upstream service — all on loopback Kestrel — plus a harness that runs
/// the <b>real</b> <see cref="PreviewServer"/> against them and drives it with a
/// browser <see cref="HttpClient"/>/<see cref="ClientWebSocket"/> that carries a
/// preview Host but dials loopback.
/// </summary>
internal static class PreviewTestKit
{
    public const string Domain = "preview.localhost";

    /// <summary>A browser HttpClient whose Host header is honoured but which always dials the frontend port.</summary>
    public static HttpClient Browser(int frontendPort, bool followRedirects = true)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, frontendPort, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            // Force one connection per handler so N browsers = N frontend connections.
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            AllowAutoRedirect = followRedirects,
        };
        return new HttpClient(handler);
    }

    /// <summary>A browser WebSocket to <c>ws://{label}.{Domain}{path}</c> that dials the frontend port.</summary>
    public static async Task<ClientWebSocket> BrowserWebSocketAsync(
        int frontendPort, string label, string path, CancellationToken ct)
    {
        var invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            ConnectCallback = async (_, c) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, frontendPort, c);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://{label}.{Domain}{path}"), invoker, ct);
        return ws;
    }
}

/// <summary>Runs the real <see cref="PreviewServer"/> (plaintext, loopback) wired to a fake control plane.</summary>
internal sealed class PreviewHarness : IAsyncDisposable
{
    private readonly PreviewServer _server;
    private readonly ServiceProvider _sp;

    public int Port => _server.BoundPort;

    private PreviewHarness(PreviewServer server, ServiceProvider sp)
    {
        _server = server;
        _sp = sp;
    }

    public static PreviewHarness Start(
        string controlPlaneUrl,
        TimeSpan? firstByteTimeout = null,
        PreviewCertificateProvider? certificates = null,
        string? dashboardUrl = null)
    {
        var options = new PreviewOptions
        {
            Domain = PreviewTestKit.Domain,
            ControlPlaneUrl = controlPlaneUrl,
            DashboardUrl = dashboardUrl ?? "http://dashboard.test",
            ControlPlaneBearer = "preview-shared-secret-under-test",
            FirstByteTimeout = firstByteTimeout ?? TimeSpan.FromSeconds(20),
            HeadReadTimeout = TimeSpan.FromSeconds(10),
        };
        var sp = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var client = new PreviewControlPlaneClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            Options.Create(options),
            NullLogger<PreviewControlPlaneClient>.Instance);
        var server = new PreviewServer(
            new IPEndPoint(IPAddress.Loopback, 0), certificates, options, client,
            NullLogger<PreviewServer>.Instance);
        server.Start();
        return new PreviewHarness(server, sp);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _sp.DisposeAsync();
    }
}

/// <summary>A fake <c>/preview/connect</c> endpoint: records calls, returns a configurable outcome (§8.4).</summary>
internal sealed class FakeControlPlane : IAsyncDisposable
{
    private readonly WebApplication _app;

    public sealed record Call(string? Label, string? OperatorSession, string? PreviewSession, string? Bearer);

    /// <summary>Every connect call the frontend made — asserts routing/auth forwarding and N-connection counts.</summary>
    public ConcurrentQueue<Call> Calls { get; } = new();

    /// <summary>The outcome to return; defaults to Armed pointing at <see cref="RelayUrl"/> with a fresh forward id.</summary>
    public Func<Call, IResult> Handler { get; set; }

    /// <summary>Where an Armed result points the frontend (set to the fake bridge's URL).</summary>
    public string RelayUrl { get; set; } = "";

    /// <summary>Gated mode: connect returns 401 unless the call carries exactly this preview session.</summary>
    public string? RequiredPreviewSession { get; set; }

    /// <summary>The one-time code /preview/exchange accepts, redeeming it into <see cref="RequiredPreviewSession"/>.</summary>
    public string ExchangeCode { get; set; } = "good-code";

    private FakeControlPlane(WebApplication app)
    {
        _app = app;
        Handler = call =>
        {
            if (call.Bearer is null) // the frontend→plane shared bearer must be present
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            // Gated mode: admit only a matching per-label preview session.
            if (RequiredPreviewSession is { } req && call.PreviewSession != req)
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            return Results.Ok(new { grant = "g-" + Guid.NewGuid().ToString("N"), forwardId = Guid.NewGuid().ToString("N"), relayUrl = RelayUrl });
        };
    }

    public string Url => _app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

    public static async Task<FakeControlPlane> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var cp = new FakeControlPlane(app);
        app.MapPost("/preview/connect", async (HttpContext http) =>
        {
            var body = await http.Request.ReadFromJsonAsync<ConnectBody>();
            var auth = http.Request.Headers.Authorization.ToString();
            var bearer = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? auth["Bearer ".Length..].Trim() : null;
            var call = new Call(body?.Label, body?.OperatorSession, body?.PreviewSession, bearer);
            cp.Calls.Enqueue(call);
            return cp.Handler(call);
        });
        app.MapPost("/preview/exchange", async (HttpContext http) =>
        {
            var body = await http.Request.ReadFromJsonAsync<ExchangeBody>();
            return body?.Code == cp.ExchangeCode
                ? Results.Ok(new { previewSession = cp.RequiredPreviewSession ?? "sess-ok", maxAgeSeconds = 1800 })
                : Results.StatusCode(StatusCodes.Status401Unauthorized);
        });
        await app.StartAsync();
        return cp;
    }

    private sealed record ConnectBody(string? Label, string? OperatorSession, string? PreviewSession);

    private sealed record ExchangeBody(string? Label, string? Code);

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// A fake tunnel bridge standing in for the relay + producer (§8.4). Its
/// <c>/tunnel</c> WS end either splices to a real upstream TCP service (the happy
/// path), closes immediately (no producer pair → the frontend's 502), or black-holes
/// (no upstream byte → the frontend's 504).
/// </summary>
internal sealed class FakeTunnelBridge : IAsyncDisposable
{
    public enum Behaviour { Splice, CloseImmediately, BlackHole }

    private readonly WebApplication _app;
    private const int BufferSize = 64 * 1024;

    public Behaviour Mode { get; set; } = Behaviour.Splice;

    /// <summary>Loopback port of the upstream TCP service to splice to in <see cref="Behaviour.Splice"/> mode.</summary>
    public int UpstreamPort { get; set; }

    private FakeTunnelBridge(WebApplication app) => _app = app;

    public string Url => _app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

    public static async Task<FakeTunnelBridge> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var bridge = new FakeTunnelBridge(app);
        app.UseWebSockets();
        app.Map("/tunnel", bridge.HandleAsync);
        await app.StartAsync();
        return bridge;
    }

    private async Task HandleAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var ws = await http.WebSockets.AcceptWebSocketAsync();
        switch (Mode)
        {
            case Behaviour.CloseImmediately:
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "no pair", http.RequestAborted);
                return;
            case Behaviour.BlackHole:
                try { await Task.Delay(Timeout.Infinite, http.RequestAborted); } catch { /* aborted */ }
                return;
            default:
                await SpliceToUpstreamAsync(ws, http.RequestAborted);
                return;
        }
    }

    private async Task SpliceToUpstreamAsync(WebSocket ws, CancellationToken ct)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, UpstreamPort, ct);
        await using var tcp = new NetworkStream(socket, ownsSocket: false);
        var wsToTcp = Task.Run(async () =>
        {
            var buffer = new byte[BufferSize];
            try
            {
                while (true)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    await tcp.WriteAsync(buffer.AsMemory(0, r.Count), ct);
                }
            }
            catch { /* teardown */ }
        }, ct);
        var tcpToWs = Task.Run(async () =>
        {
            var buffer = new byte[BufferSize];
            try
            {
                while (true)
                {
                    var n = await tcp.ReadAsync(buffer, ct);
                    if (n == 0) return;
                    await ws.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, ct);
                }
            }
            catch { /* teardown */ }
        }, ct);
        await Task.WhenAny(wsToTcp, tcpToWs);
        try { socket.Shutdown(SocketShutdown.Both); } catch { /* torn down */ }
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// A real upstream service reached through the tunnel: an HTTP endpoint that echoes
/// the request path + Host and returns a Set-Cookie + absolute Location (to prove
/// the frontend rewrites nothing, §8.4), and a WebSocket echo at <c>/ws</c>.
/// </summary>
internal sealed class UpstreamService : IAsyncDisposable
{
    private readonly WebApplication _app;

    private UpstreamService(WebApplication app) => _app = app;

    public int Port => new Uri(_app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))).Port;

    public static async Task<UpstreamService> StartAsync()
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
                    // Echo the frame back, prefixed so bidirectionality is unambiguous.
                    await ws.SendAsync(buffer.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, http.RequestAborted);
                }
            }
            catch { /* client went away */ }
        });

        // Server-INITIATED close: send one frame, then a Close frame, then return.
        // Exercises the frontend delivering an upstream-originated close handshake to
        // a browser that never sent Close (§8.4 graceful close).
        app.Map("/wsclose", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            try
            {
                await ws.SendAsync("last"u8.ToArray(), WebSocketMessageType.Text, true, http.RequestAborted);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "server-close", http.RequestAborted);
            }
            catch { /* client went away */ }
        });

        // Everything else: echo request facts + set headers a rewrite would mangle.
        // A fallback endpoint (not app.Run) so the /ws endpoint still wins its path.
        app.MapFallback(async (HttpContext http) =>
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.Headers["X-Echo-Path"] = http.Request.Path + http.Request.QueryString;
            http.Response.Headers["X-Echo-Host"] = http.Request.Host.Value;
            // Echo any auth material the upstream received back, so a test can assert
            // the frontend HEAD-STRIP removed it (§8.4): these must come back empty.
            http.Response.Headers["X-Echo-Auth"] = http.Request.Headers.Authorization.ToString();
            http.Response.Headers["X-Echo-Cookie"] = http.Request.Headers.Cookie.ToString();
            http.Response.Headers["Set-Cookie"] = "sess=abc123; Path=/; HttpOnly";
            http.Response.Headers["Location"] = "https://example.com/absolute/target";
            await http.Response.WriteAsync("hello from upstream");
        });

        await app.StartAsync();
        return new UpstreamService(app);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
