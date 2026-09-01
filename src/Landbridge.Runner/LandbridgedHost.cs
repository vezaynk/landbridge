using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Landbridge.Runner;

/// <summary>
/// Hosted landbridged: start the daemon on host start, tear it down on SIGINT /
/// SIGTERM / <c>systemctl stop</c>. <c>--enroll</c> does not use this.
/// </summary>
internal sealed class LandbridgedHost(
    RunnerDaemon daemon,
    AgentProcessSupervisor processes,
    RunnerConfig config,
    string machineId,
    string channelMode,
    ILogger<LandbridgedHost> log,
    WebSocketControlPlaneChannel? wsChannel = null,
    LocalIdentityListener? identity = null,
    MachineTokenRefresher? refresher = null,
    HttpClient? refreshHttp = null,
    IDisposable? otelExport = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = daemon.StartAsync();
        wsChannel?.Start((command, ct) => daemon.HandleAsync(command, ct));
        var identityBit = identity is null
            ? "identity=unbound"
            : $"identity=http://127.0.0.1:{LocalIdentityListener.Port}";
        log.LogInformation(
            "landbridged up: machine={MachineId} profiles=[{Profiles}] strays_reaped={Strays} {Identity} control={Control}",
            machineId, string.Join(", ", config.DeclaredProfiles), daemon.StraysReaped, identityBit, channelMode);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        log.LogInformation("landbridged shutting down; killing everything it started");
        await daemon.ShutdownAsync();
        if (identity is not null)
            await identity.DisposeAsync();
        await processes.DisposeAsync();
        if (wsChannel is not null)
            await wsChannel.DisposeAsync();
        if (refresher is not null)
            await refresher.DisposeAsync();
        refreshHttp?.Dispose();
        otelExport?.Dispose();
    }
}
