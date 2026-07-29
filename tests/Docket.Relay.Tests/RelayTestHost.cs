using System.Net;
using System.Net.WebSockets;
using Docket.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Docket.Relay.Tests;

/// <summary>
/// Stands up the real relay pipeline (<see cref="TunnelEndpoint"/> +
/// <see cref="ForwardRegistry"/> + an injected <see cref="IGrantValidator"/>) on
/// a loopback Kestrel port, mirroring how the Docket.Mcp spine tests host the
/// control plane on <c>127.0.0.1:0</c>. Tests drive it with real
/// <see cref="ClientWebSocket"/> connections.
/// </summary>
internal sealed class RelayTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>The relay's pairing table — inspected to prove no forward-id leak.</summary>
    public ForwardRegistry Registry { get; }

    private RelayTestHost(WebApplication app, ForwardRegistry registry)
    {
        _app = app;
        Registry = registry;
    }

    public static async Task<RelayTestHost> StartAsync(
        IGrantValidator validator, TimeSpan? pairWaitTimeout = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Register the test validator first so AddRelay's TryAddSingleton no-ops.
        builder.Services.AddSingleton(validator);
        builder.Services.AddRelay(builder.Configuration);

        // PostConfigure runs after AddRelay's .Bind(), so the override wins over
        // any bound appsettings value (the relay's appsettings.json is copied
        // into the test bin via the project reference).
        if (pairWaitTimeout is { } timeout)
            builder.Services.PostConfigure<RelayOptions>(o => o.PairWaitTimeout = timeout);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapRelayTunnel();

        await app.StartAsync();

        return new RelayTestHost(app, app.Services.GetRequiredService<ForwardRegistry>());
    }

    /// <summary>The <c>ws://…/tunnel</c> URI for this running host.</summary>
    public Uri TunnelUri
    {
        get
        {
            var http = _app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));
            return new Uri(http.Replace("http://", "ws://", StringComparison.Ordinal) + "/tunnel");
        }
    }

    /// <summary>
    /// Connect a tunnel with the given headers. When the server refuses before
    /// the upgrade, <see cref="ClientWebSocket.ConnectAsync"/> throws a
    /// <see cref="WebSocketException"/>; with <c>CollectHttpResponseDetails</c>
    /// on, the caller can read the refused status off the socket.
    /// </summary>
    public async Task<ClientWebSocket> ConnectAsync(
        string forwardId, string grant, string role, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.SetRequestHeader(TunnelEndpoint.ForwardIdHeader, forwardId);
        ws.Options.SetRequestHeader(TunnelEndpoint.GrantHeader, grant);
        ws.Options.SetRequestHeader(TunnelEndpoint.RoleHeader, role);
        await ws.ConnectAsync(TunnelUri, ct);
        return ws;
    }

    /// <summary>
    /// Attempt a tunnel the relay is expected to refuse before the upgrade, and
    /// return the refused HTTP status. Fails if the relay upgrades instead.
    /// </summary>
    public async Task<HttpStatusCode> ConnectExpectingRefusalAsync(
        string forwardId, string grant, string role, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.SetRequestHeader(TunnelEndpoint.ForwardIdHeader, forwardId);
        ws.Options.SetRequestHeader(TunnelEndpoint.GrantHeader, grant);
        ws.Options.SetRequestHeader(TunnelEndpoint.RoleHeader, role);
        try
        {
            await ws.ConnectAsync(TunnelUri, ct);
        }
        catch (WebSocketException)
        {
            return ws.HttpStatusCode;
        }

        throw new InvalidOperationException("expected the relay to refuse the tunnel, but it upgraded");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
