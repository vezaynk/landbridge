using System.Net.WebSockets;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
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
    public static WebApplication BuildPlane(string connectionString, string? relayValidationBearer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        if (relayValidationBearer is not null)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RelayValidationEndpoints.BearerConfigKey] = relayValidationBearer,
            });

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TaskStore>();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
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
        return app;
    }

    /// <summary>The relay, with its control-plane validator pointed at <paramref name="controlPlaneUrl"/>.</summary>
    public static WebApplication BuildRelay(string controlPlaneUrl, string? bearer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
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
        PostgresFixture pg, TeamId team, string serviceName, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Automated, null, true), ct);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine, instance, ct);
        await store.RegisterServiceAsync(new WorkerCaller(team, created.Task.Id, instance), serviceName, 5432, ct);
        return created.Task.Id;
    }

    /// <summary>
    /// Creates a consumer task, dispatches it to working, and returns its minted
    /// worker token. Like the producer helper, dispatch is deterministic only
    /// while this is the sole submitted task.
    /// </summary>
    public static async Task<string> SeedWorkingWorkerTokenAsync(
        PostgresFixture pg, TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var tokens = new TokenService(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Automated, null, true), ct);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine, instance, ct);
        return (await tokens.MintWorkerTokenAsync(team, created.Task.Id, instance, ct)).Token;
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
        ws.Options.SetRequestHeader(TunnelEndpoint.ForwardIdHeader, forwardId);
        ws.Options.SetRequestHeader(TunnelEndpoint.GrantHeader, grant);
        ws.Options.SetRequestHeader(TunnelEndpoint.RoleHeader, role);
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
