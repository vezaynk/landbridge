using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
using Docket.Mcp.Dashboard;
using Docket.Mcp.Tools;
using Docket.Relay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Docket.Mcp.Tests;

/// <summary>
/// Hosts for the relay-grant tests (spec §8.3): the real control plane (MCP tools
/// + the <c>/relay/validate</c> endpoint), the real relay (its
/// <see cref="ControlPlaneGrantValidator"/> pointed at that plane), and helpers
/// to seed a working service and issue a real grant against the fixture's
/// database. Mirrors the wiring in <c>Program.cs</c> and the other end-to-end
/// suites, pointed at the ephemeral Postgres.
/// </summary>
internal static class RelayGrantTestKit
{
    public static MachineSnapshot Machine =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    /// <summary>The plane, with the relay-validation endpoint and its shared bearer configured.</summary>
    /// <param name="relayUrl">
    /// The relay base URL <c>open_forward</c> hands docketd (§8.3). Configured up
    /// front (the crown E2E reserves the relay's port before starting anything) so
    /// the plane and relay can be brought up in either order without a config race.
    /// </param>
    public static WebApplication BuildPlane(
        string connectionString, string? relayValidationBearer, string? relayUrl = null,
        string? previewConnectBearer = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var settings = new Dictionary<string, string?>();
        if (relayValidationBearer is not null)
            settings[RelayValidationEndpoints.BearerConfigKey] = relayValidationBearer;
        if (relayUrl is not null)
            settings["Docket:RelayUrl"] = relayUrl;
        // §8.4: the shared bearer the preview frontend presents to /preview/connect.
        if (previewConnectBearer is not null)
            settings[PreviewConnectEndpoints.BearerConfigKey] = previewConnectBearer;
        if (settings.Count > 0)
            builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TaskStore>();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<RelayGrantService>();
        // §8.4: the preview mapping store + the per-connection connect orchestrator.
        builder.Services.AddScoped<PreviewMappingService>();
        builder.Services.AddScoped<PreviewConnectService>();
        builder.Services.AddSingleton<PreviewAuthStore>();
        // §12 dashboard: the read side + operator verifier, so the preview-auth confirm
        // and the mint endpoint are exercisable end to end (the gated browser flow, §8.4).
        builder.Services.AddScoped<DashboardQueries>();
        builder.Services.AddSingleton<IOperatorVerifier, ConfiguredOperatorVerifier>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        // §8.3: the forward orchestrator + waiter, and the event sink that completes
        // the waiter when the consumer end reports its bound port.
        builder.Services.AddSingleton<RunnerEventSink>();
        builder.Services.AddDocketForwarding();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(DocketAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DocketAuthenticationHandler>(
                DocketAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp().RequireAuthorization();
        app.MapRelayValidationEndpoint();
        app.MapPreviewConnectEndpoint();
        app.MapDashboard();
        return app;
    }

    /// <summary>The relay, with its control-plane validator pointed at <paramref name="controlPlaneUrl"/>.</summary>
    /// <param name="listenUrl">A fixed loopback URL to bind (the crown E2E pre-reserves one); ephemeral if null.</param>
    public static WebApplication BuildRelay(string controlPlaneUrl, string? bearer, string? listenUrl = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(listenUrl ?? "http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{ControlPlaneGrantValidatorOptions.SectionName}:Url"] = controlPlaneUrl,
            [$"{ControlPlaneGrantValidatorOptions.SectionName}:Bearer"] = bearer,
        });

        builder.Services.AddRelay(builder.Configuration);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapRelayTunnel();
        return app;
    }

    public static Uri BaseUri(WebApplication app) =>
        new(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal)) + "/");

    public static Uri TunnelUri(WebApplication relay)
    {
        var http = relay.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));
        return new Uri(http.Replace("http://", "ws://", StringComparison.Ordinal) + "/tunnel");
    }

    public static async Task<McpClient> ConnectMcpAsync(Uri endpoint, string bearer, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    // ── Seeding (against the fixture DB, so the plane's own scope sees it) ─────

    /// <summary>
    /// Creates a producer task, dispatches it to working, and registers
    /// <paramref name="serviceName"/> on it. Dispatch is deterministic because
    /// this is the only submitted task at the moment it runs — call it before
    /// seeding any other task.
    /// </summary>
    public static async Task<TaskId> RegisterWorkingServiceAsync(
        PostgresFixture pg, TeamId team, string serviceName, CancellationToken ct, int port = 5432)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Automated, null, true), ct);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine, instance, ct);
        await store.RegisterServiceAsync(new WorkerCaller(team, created.Task.Id, instance), serviceName, port, ct);
        return created.Task.Id;
    }

    /// <summary>A dispatched-to-working worker: its task, instance, and minted token.</summary>
    public sealed record SeededWorker(TaskId Task, WorkerInstanceId Instance, string Token);

    /// <summary>
    /// Creates a consumer task, dispatches it to working, and returns its task id,
    /// instance, and minted worker token. Like the producer helper, dispatch is
    /// deterministic only while this is the sole submitted task.
    /// </summary>
    public static async Task<SeededWorker> SeedWorkingWorkerAsync(
        PostgresFixture pg, TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var tokens = new TokenService(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Automated, null, true), ct);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine, instance, ct);
        var token = (await tokens.MintWorkerTokenAsync(team, created.Task.Id, instance, ct)).Token;
        return new SeededWorker(created.Task.Id, instance, token);
    }

    /// <summary>The worker token only — the common case for tests that don't need the ids.</summary>
    public static async Task<string> SeedWorkingWorkerTokenAsync(
        PostgresFixture pg, TeamId team, CancellationToken ct) =>
        (await SeedWorkingWorkerAsync(pg, team, ct)).Token;

    /// <summary>
    /// Reserve a free loopback URL by binding an ephemeral port and releasing it,
    /// so the crown E2E can configure the plane and bind the relay to the same
    /// address without a build-order race (§8.3).
    /// </summary>
    public static string ReserveLoopbackUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    /// <summary>Issues a real grant for a consumer against the fixture DB (§8.3).</summary>
    public static async Task<RelayGrantResult.Issued> IssueGrantAsync(
        PostgresFixture pg, TeamId team, string serviceName, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var grants = new RelayGrantService(db, TimeProvider.System);
        var consumer = new WorkerCaller(team, TaskId.New(), WorkerInstanceId.New());
        return (RelayGrantResult.Issued)await grants.IssueAsync(consumer, serviceName, ct);
    }

    /// <summary>A live lead token for <paramref name="team"/>, as a future OAuth callback would mint it.</summary>
    public static async Task<string> LeadTokenAsync(PostgresFixture pg, TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
        return claim.Token.Token;
    }

    // ── Relay tunnel client helpers (the docketd sides, stubbed by the test) ──

    public static async Task<ClientWebSocket> ConnectTunnelAsync(
        Uri tunnelUri, string forwardId, string grant, string role, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.SetRequestHeader(RelayTunnel.ForwardIdHeader, forwardId);
        ws.Options.SetRequestHeader(RelayTunnel.GrantHeader, grant);
        ws.Options.SetRequestHeader(RelayTunnel.RoleHeader, role);
        await ws.ConnectAsync(tunnelUri, ct);
        return ws;
    }

    public static Task SendAsync(WebSocket ws, byte[] data, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, endOfMessage: true, ct);

    public static async Task<byte[]> ReceiveExactlyAsync(WebSocket ws, int count, CancellationToken ct)
    {
        var received = new byte[count];
        var offset = 0;
        var buffer = new byte[64 * 1024];
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
}
