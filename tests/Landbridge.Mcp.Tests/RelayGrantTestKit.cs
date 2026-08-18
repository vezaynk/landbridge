using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Dashboard;
using Landbridge.Mcp.Tools;
using Landbridge.Relay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Landbridge.Mcp.Tests;

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
    /// The relay base URL <c>open_forward</c> hands landbridged (§8.3). Configured up
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
            settings["Landbridge:RelayUrl"] = relayUrl;
        // §8.4: the shared bearer the preview frontend presents to /preview/connect.
        if (previewConnectBearer is not null)
            settings[PreviewConnectEndpoints.BearerConfigKey] = previewConnectBearer;
        if (settings.Count > 0)
            builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
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
        builder.Services.AddLandbridgeForwarding();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(LandbridgeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LandbridgeAuthenticationHandler>(
                LandbridgeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp().RequireAuthorization();
        app.MapWorkerPermissionEndpoint();
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

    /// <summary>
    /// <see cref="LeadTools"/> wired for the direct-call tests: the store plus the
    /// two §8.3 human-path collaborators (the lead↔machine binding and the grant
    /// service) over <paramref name="db"/>, and a forward orchestrator over
    /// <paramref name="registry"/> so the same instance the test drives is the one
    /// commands are sent through. Config is empty, so the relay URL falls back to
    /// <see cref="WorkerTools.DefaultRelayUrl"/>.
    /// </summary>
    public static LeadTools LeadToolsFor(
        LandbridgeDbContext db, TimeProvider clock, RunnerConnectionRegistry registry, IHttpContextAccessor http) =>
        new(new SessionStore(db, clock),
            registry,
            new LeadMachineBindingService(db, clock),
            new RelayGrantService(db, clock),
            new ForwardOrchestrator(registry, new ForwardWaiters(), NullLogger<ForwardOrchestrator>.Instance),
            http,
            new ConfigurationBuilder().Build());

    /// <summary>
    /// <see cref="WorkerTools"/> wired for the direct-call tests, over the same store shape
    /// <see cref="LeadToolsFor"/> uses. <paramref name="pollIntervalMs"/> sets
    /// <c>Landbridge:PermissionPollIntervalMs</c> so §11's permission wait runs at millisecond
    /// granularity against the real clock — the wait is a genuine delay loop, so a test
    /// drives it by making the ticks short rather than by advancing a fake clock through
    /// them.
    /// </summary>
    public static WorkerTools WorkerToolsFor(
        LandbridgeDbContext db, TimeProvider clock, RunnerConnectionRegistry registry,
        IHttpContextAccessor http, int? pollIntervalMs = null)
    {
        var config = new ConfigurationBuilder();
        if (pollIntervalMs is { } ms)
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Landbridge:PermissionPollIntervalMs"] = ms.ToString(),
            });
        return new WorkerTools(
            new SessionStore(db, clock),
            new RelayGrantService(db, clock),
            new ForwardOrchestrator(registry, new ForwardWaiters(), NullLogger<ForwardOrchestrator>.Instance),
            new PreviewMappingService(db, clock),
            http,
            config.Build(),
            new ProcessControlRelay(registry));
    }

    // ── Seeding (against the fixture DB, so the plane's own scope sees it) ─────

    /// <summary>
    /// Creates a producer task, dispatches it to working, and registers
    /// <paramref name="serviceName"/> on it. Dispatch is deterministic because
    /// this is the only submitted task at the moment it runs — call it before
    /// seeding any other task.
    /// </summary>
    public static async Task<SessionId> RegisterWorkingServiceAsync(
        PostgresFixture pg, TeamId team, string serviceName, CancellationToken ct, int port = 5432)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", "default"), ct);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine, instance, ct);
        await store.RegisterServiceAsync(new WorkerCaller(team, created.Session.Id, instance), serviceName, port, ct);
        return created.Session.Id;
    }

    /// <summary>A dispatched-to-working worker: its task, instance, and minted token.</summary>
    public sealed record SeededWorker(SessionId Session, WorkerInstanceId Instance, string Token);

    /// <summary>
    /// Creates a consumer task, dispatches it to working, and returns its task id,
    /// instance, and minted worker token. Like the producer helper, dispatch is
    /// deterministic only while this is the sole submitted task.
    /// </summary>
    public static async Task<SeededWorker> SeedWorkingWorkerAsync(
        PostgresFixture pg, TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var tokens = new TokenService(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", "default"), ct);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine, instance, ct);
        var token = (await tokens.MintWorkerTokenAsync(team, created.Session.Id, instance, ct)).Token;
        return new SeededWorker(created.Session.Id, instance, token);
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
        var consumer = new WorkerCaller(team, SessionId.New(), WorkerInstanceId.New());
        return (RelayGrantResult.Issued)await grants.IssueAsync(consumer, serviceName, ct);
    }

    /// <summary>A human session that has claimed the Lead of a Team: the human's own
    /// session id (what a lead↔machine binding keys on, §8.3) and the lead token.</summary>
    public sealed record SeededLead(Guid HumanId, string HumanToken, string Token);

    /// <summary>
    /// A live lead claim for <paramref name="team"/>, minted through the same seam the
    /// OAuth callback uses: a human session, then that human claiming the Team (§4, §5).
    /// </summary>
    public static async Task<SeededLead> LeadSessionAsync(PostgresFixture pg, TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
        // A human's id IS its session credential's id (§5), which is what the Lead
        // credential's HumanId column points at.
        return new SeededLead(human.CredentialId, human.Token, claim.Token.Token);
    }

    /// <summary>A live lead token for <paramref name="team"/>, through the same seam the OAuth callback uses.</summary>
    public static async Task<string> LeadTokenAsync(PostgresFixture pg, TeamId team, CancellationToken ct) =>
        (await LeadSessionAsync(pg, team, ct)).Token;

    /// <summary>
    /// An enrolled machine, as <c>/landbridge-enroll</c> leaves it (§11): the id a Lead
    /// passes to <c>bind_machine</c>, and the key the connection registry uses.
    /// </summary>
    public static async Task<Guid> EnrollMachineAsync(PostgresFixture pg, string name, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var enrollment = await tokens.IssueEnrollmentTokenAsync(ct);
        var credentials = await tokens.ExchangeEnrollmentAsync(
            enrollment.Token, new MachineDeclaration(name, "a human's own machine", "macos", "standard"), ct);
        return credentials!.MachineId;
    }

    // ── Relay tunnel client helpers (the landbridged sides, stubbed by the test) ──

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
