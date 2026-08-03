using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Docket.Relay;
using Docket.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// Hosts and seeding for the multi-machine collaboration suite (spec §8.3). A real
/// control plane (MCP tools + the <c>/relay/validate</c> endpoint), a real relay (its
/// <see cref="ControlPlaneGrantValidator"/> pointed at that plane), and helpers to
/// issue a real lead token and connect the MCP client. It mirrors the wiring in the
/// production <c>Program.cs</c> and the increment-3/4 crown suites, pointed at the
/// ephemeral Postgres — additive, so it never touches the fenced Docket.Mcp.Tests kit.
/// </summary>
internal static class MultiMachineKit
{
    /// <summary>The plane, with the relay-validation endpoint and its shared bearer configured.</summary>
    /// <param name="relayUrl">
    /// The relay base URL <c>open_forward</c> hands docketd (§8.3). Configured up front
    /// (the relay's port is reserved before anything starts) so the plane and relay
    /// can be brought up in either order without a config race.
    /// </param>
    public static WebApplication BuildPlane(string connectionString, string relayValidationBearer, string relayUrl)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RelayValidationEndpoints.BearerConfigKey] = relayValidationBearer,
            ["Docket:RelayUrl"] = relayUrl,
        });

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TaskStore>();
        builder.Services.AddScoped<TeamBudgetService>(); // §9.9: the store commits dispatch budget through it
        builder.Services.AddScoped<TeamForwardUsageService>(); // §9.10: /relay/usage attributes bytes through it
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>(); // §8.4: WorkerTools.open_preview
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
        return app;
    }

    /// <summary>The relay, with its control-plane validator pointed at <paramref name="controlPlaneUrl"/>.</summary>
    public static WebApplication BuildRelay(string controlPlaneUrl, string bearer, string listenUrl)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(listenUrl);
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

    public static string HttpBaseUrl(WebApplication app) =>
        app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

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

    /// <summary>A live lead token for <paramref name="team"/>, as an OAuth callback would mint it.</summary>
    public static async Task<string> LeadTokenAsync(PostgresFixture pg, TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, team, ct: ct);
        return claim.Token.Token;
    }

    /// <summary>
    /// Reserve a free loopback URL by binding an ephemeral port and releasing it, so
    /// the plane can be configured and the relay bound to the same address without a
    /// build-order race (§8.3).
    /// </summary>
    public static string ReserveLoopbackUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }
}

/// <summary>
/// A real <see cref="RunnerDaemon"/> standing up a machine's relay data planes (§8.3),
/// re-created here (additive) from the increment-3 crown's harness. No worker ever
/// spawns through <em>this</em> supervisor — only open-forward commands are driven —
/// so the daemon's forwarder is the real one splicing bytes to the relay while a
/// separate <see cref="ProcessSupervisor"/> spawns the scripted collaborator.
/// </summary>
internal sealed class DaemonHarness : IAsyncDisposable
{
    private readonly RunnerDaemon _daemon;
    private readonly string _workRoot;
    private bool _stopped;

    public DaemonHarness(string machineId, IControlPlaneChannel channel)
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "docket-multimachine-daemon", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workRoot);
        var config = RunnerConfig.Load($$"""
            { "machine": { "work_root": {{JsonSerializer.Serialize(_workRoot)}} },
              "profiles": [ { "name": "default", "spawn": ["noop"] } ] }
            """);
        var ring = new OutboundEventRing(256);
        var supervisor = new ProcessSupervisor(config.Machine, ring, TimeProvider.System);
        var backPressure = new BackPressureMonitor(
            new PortableSystemLoadReader(config.Machine.WorkRoot), config.Machine.BackPressure);
        _daemon = new RunnerDaemon(
            machineId, config, supervisor, backPressure, channel, ring, new NoOpReaper(), TimeProvider.System);
    }

    public Task Send(RunnerCommand command, CancellationToken ct) => _daemon.HandleAsync(command, ct);

    public Task StartAsync() => _daemon.StartAsync();

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        await _daemon.ShutdownAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(_workRoot, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// An <see cref="IControlPlaneChannel"/> standing in for a runner socket's receive
/// loop: it forwards the forward-lifecycle events into the plane's
/// <see cref="RunnerEventSink"/> so the orchestrator's <c>forward-opened</c> waiter
/// completes and <c>forward-closed</c> bookkeeping runs. Heartbeats are dropped —
/// the rig drives readiness directly on the registry, deterministically.
/// </summary>
internal sealed class SinkForwardingChannel(RunnerEventSink sink) : IControlPlaneChannel
{
    public async Task<bool> PublishAsync(RunnerEvent evt, long gapBefore, CancellationToken ct)
    {
        if (evt is ForwardOpenedEvent or ForwardClosedEvent)
            await sink.HandleAsync(evt, ct);
        return true;
    }

    public Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>A stray reaper that reaps nothing — no harness spawns through the daemon.</summary>
internal sealed class NoOpReaper : IStrayReaper
{
    public int Reap(string machineId) => 0;
}
