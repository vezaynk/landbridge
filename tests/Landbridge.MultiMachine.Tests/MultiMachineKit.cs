using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Mcp;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Landbridge.Relay;
using Landbridge.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// Hosts and seeding for the multi-machine collaboration suite (spec §8.3). A real
/// control plane (MCP tools + the <c>/relay/validate</c> endpoint), a real relay (its
/// <see cref="ControlPlaneGrantValidator"/> pointed at that plane), and helpers to
/// issue a real lead token and connect the MCP client. It mirrors the wiring in the
/// production <c>Program.cs</c> and the increment-3/4 crown suites, pointed at the
/// ephemeral Postgres — additive, so it never touches the fenced Landbridge.Mcp.Tests kit.
/// </summary>
internal static class MultiMachineKit
{
    /// <summary>The plane, with the relay-validation endpoint and its shared bearer configured.</summary>
    /// <param name="relayUrl">
    /// The relay base URL <c>open_forward</c> hands landbridged (§8.3). Configured up front
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
            ["Landbridge:RelayUrl"] = relayUrl,
            // Real ACP workers block inside session/request_permission; keep the
            // plane-side poll tight so a Lead verdict is noticed immediately.
            ["Landbridge:PermissionPollIntervalMs"] = "50",
        });

        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddDbContextFactory<LandbridgeDbContext>(o =>
            o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>(); // §8.4: WorkerTools.open_preview
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        // §8.3: the forward orchestrator + waiter, and the event sink that completes
        // the waiter when the consumer end reports its bound port.
        builder.Services.AddSingleton<RunnerEventSink>();
        builder.Services.AddLandbridgeForwarding();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IPermissionClassifier>(NullPermissionClassifier.Instance);

        builder.Services.AddAuthentication(LandbridgeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LandbridgeAuthenticationHandler>(
                LandbridgeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>()
            .WithTools<FrictionTools>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp().RequireAuthorization();
        app.MapWorkerPermissionEndpoint();
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

    // MCP connect, Lead-token minting, and loopback reservation used to live here. They are
    // not multi-machine-specific — the chaos rig had grown its own copy of each — so they now
    // live in PlaneProbe (tests/RigSupport), source-linked into both suites. Call
    // PlaneProbe.ConnectMcpAsync / LeadTokenAsync / ReserveLoopbackUrl / ReserveLoopbackPort
    // directly rather than through a wrapper here: one name per thing.
}

/// <summary>
/// A real <see cref="RunnerDaemon"/> standing up a machine's relay data planes (§8.3),
/// re-created here (additive) from the increment-3 crown's harness. By default no worker
/// spawns through <em>this</em> supervisor — only open-forward commands are driven — so
/// the daemon's forwarder is the real one splicing bytes to the relay while a separate
/// <see cref="ProcessSupervisor"/> spawns the scripted collaborator.
///
/// <para>The §10 <b>process</b> commands are the exception, and they are why the
/// <c>workerSupervisor</c> parameter exists. <c>start_process</c> is gated on the
/// machine, not the plane: <see cref="RunnerDaemon"/> resolves the profile of the task
/// that asked by calling <see cref="ProcessSupervisor.ProfileFor"/> on <em>its own</em>
/// supervisor, so a daemon holding a private supervisor refuses every start with
/// "no supervised task on this machine". Handing it the supervisor that really spawned
/// the worker — together with a <see cref="RunnerConfig"/> carrying that worker's
/// profile and a real <see cref="AgentProcessSupervisor"/> — is what makes the process tools
/// reachable end to end, with the gate decided by the profile exactly as in production.</para>
/// </summary>
internal sealed class DaemonHarness : IAsyncDisposable
{
    private readonly RunnerDaemon _daemon;
    private readonly string _workRoot;
    private readonly AgentProcessSupervisor? _processes;
    private bool _stopped;

    /// <param name="workerSupervisor">
    /// The supervisor that spawns this machine's workers, shared so the daemon can resolve
    /// the profile of a task calling <c>start_process</c>. Null keeps the original
    /// forward-only harness: a private supervisor, and process commands that refuse.
    /// </param>
    /// <param name="workerConfig">
    /// The config the daemon resolves profiles from — must declare the profile the worker
    /// supervisor spawns under, or the process gate has nothing to consult. Required with
    /// <paramref name="workerSupervisor"/>.
    /// </param>
    /// <remarks>
    /// The daemon keeps its <b>own</b> ring, deliberately, and must never be handed the worker
    /// supervisor's: a ring is a channel with single-take semantics, so two pumps reading one
    /// ring split its events between them at random — the daemon's own pump would swallow the
    /// <c>started</c>/<c>exited</c> events the rig's pump needs, leaving a killed worker's task
    /// stuck in <c>working</c>. The daemon's replies reach the plane through
    /// <paramref name="channel"/> instead, which is the production path anyway.
    /// </remarks>
    /// <param name="log">
    /// Where the machine's own process-supervision log lines go. Worth wiring rather than
    /// dropping: a spawn that fails to start is reported to the agent as a bare
    /// <c>spawn_failed</c> refusal, and the underlying reason (a bad path, a missing runtime, a
    /// permission) exists only in this log.
    /// </param>
    public DaemonHarness(
        string machineId,
        IControlPlaneChannel channel,
        ProcessSupervisor? workerSupervisor = null,
        RunnerConfig? workerConfig = null,
        Action<string>? log = null)
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "landbridge-multimachine-daemon", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workRoot);
        var config = workerConfig ?? RunnerConfig.Load($$"""
            { "machine": { "work_root": {{JsonSerializer.Serialize(_workRoot)}} },
              "profiles": [ { "name": "default", "prompt": "go", "spawn": ["noop"] } ] }
            """);
        var ring = new OutboundEventRing(256);
        var supervisor = workerSupervisor ?? new ProcessSupervisor(config.Machine, ring, TimeProvider.System);
        var backPressure = new BackPressureMonitor(
            new PortableSystemLoadReader(config.Machine.WorkRoot), config.Machine.BackPressure);
        // §10: agent-started processes live under the machine's AgentProcessSupervisor.
        // Only stood up for a rig that shares its worker supervisor — without one the
        // daemon could not resolve a profile to gate on anyway.
        _processes = workerSupervisor is null
            ? null
            : new AgentProcessSupervisor(machineId, TimeProvider.System,
                logs: new ProcessLogStore(Path.Combine(_workRoot, "processes")),
                log: log);
        _daemon = new RunnerDaemon(
            machineId, config, supervisor, backPressure, channel, ring, new NoOpReaper(), TimeProvider.System,
            processes: _processes);
    }

    /// <summary>What this machine is running, as the heartbeat would report it (§10, §12) —
    /// the read <c>list_processes</c> answers from. Empty for a forward-only harness.</summary>
    public IReadOnlyList<ProcessStatus> ReportProcesses() => _processes?.ReportProcesses() ?? [];

    public Task Send(RunnerCommand command, CancellationToken ct) => _daemon.HandleAsync(command, ct);

    public Task StartAsync() => _daemon.StartAsync();

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        await _daemon.ShutdownAsync();
        // Agent-started processes are machine-scoped and outlive every task (§10), so
        // nothing else takes them down — the rig going away is this fleet's "machine
        // restart", and the sweep is what keeps a leaked listener out of the next test.
        if (_processes is not null)
        {
            _processes.KillAll();
            await _processes.DisposeAsync();
        }
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
        // Forward lifecycle, plus the §10 process request/reply events: both are answers a
        // plane-side call is parked on (ForwardWaiters / ProcessControlRelay), so dropping
        // either would hang the tool call rather than fail it. Everything else a daemon emits
        // here is liveness the rig drives deterministically on the registry instead.
        if (evt is ForwardOpenedEvent or ForwardClosedEvent
            or ProcessStartedEvent or ProcessStoppedEvent or ProcessWrittenEvent)
        {
            await sink.HandleAsync(evt, ct);
        }
        return true;
    }

    public Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct) => Task.FromResult(true);
}

/// <summary>A stray reaper that reaps nothing — no harness spawns through the daemon.</summary>
internal sealed class NoOpReaper : IStrayReaper
{
    public int Reap(string machineId) => 0;
}
