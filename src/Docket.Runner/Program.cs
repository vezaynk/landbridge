using System.Runtime.InteropServices;
using Docket.Contracts;

namespace Docket.Runner;

/// <summary>
/// The <c>docketd</c> entrypoint (spec §10). Loads and validates a config,
/// wires the supervisor / back-pressure / heartbeat / ring, and runs until a
/// termination signal. The control-plane <b>wire transport is deferred</b>
/// (§10 — the frozen interface's real bytes), so this skeleton ships events to
/// an <see cref="InMemoryControlPlaneChannel"/> mirrored to the console. The
/// daemon logic — stray reaping, reboot announcement, back-pressure gating,
/// heartbeat cadence — is real and runnable.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configPath = ArgValue(args, "--config");
        if (configPath is null)
        {
            Console.Error.WriteLine("usage: docketd --config <path> [--machine-id <id>]");
            return 2;
        }

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"config not found: {configPath}");
            return 2;
        }

        RunnerConfig config;
        try
        {
            config = RunnerConfig.Load(await File.ReadAllTextAsync(configPath));
        }
        catch (RunnerConfigException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        var machineId = ArgValue(args, "--machine-id")
            ?? Environment.GetEnvironmentVariable("DOCKET_MACHINE_ID")
            ?? Guid.NewGuid().ToString("N");

        var clock = TimeProvider.System;
        var ring = new OutboundEventRing(capacity: 1024);
        var reaper = new StrayReaper(ProcessInventory.ForCurrentPlatform(), Environment.ProcessId);
        var supervisor = new ProcessSupervisor(config.Machine, ring, clock, reaper);
        var backPressure = new BackPressureMonitor(
            new PortableSystemLoadReader(config.Machine.WorkRoot), config.Machine.BackPressure);

        // §10: dial the control plane outbound when a URL + machine token are
        // configured; otherwise fall back to the console placeholder. The WS
        // channel's receive loop is wired to the daemon after it exists.
        var controlUrl = Environment.GetEnvironmentVariable("DOCKET_CONTROL_URL");
        var machineToken = Environment.GetEnvironmentVariable("DOCKET_MACHINE_TOKEN");
        WebSocketControlPlaneChannel? wsChannel = null;
        IControlPlaneChannel channel;
        if (!string.IsNullOrWhiteSpace(controlUrl) && !string.IsNullOrWhiteSpace(machineToken))
        {
            wsChannel = new WebSocketControlPlaneChannel(new Uri(controlUrl), machineToken, clock, Console.WriteLine);
            channel = wsChannel;
        }
        else
        {
            channel = new ConsoleControlPlaneChannel();
        }

        var daemon = new RunnerDaemon(machineId, config, supervisor, backPressure, channel, ring, reaper, clock);
        await daemon.StartAsync();

        // Outbound-only: the receive loop runs on the socket docketd dialed, not
        // a listener (§10). Commands arriving on it drive the daemon.
        wsChannel?.Start((command, ct) => daemon.HandleAsync(command, ct));

        var channelMode = wsChannel is null ? "console" : controlUrl;
        Console.WriteLine($"docketd up: machine={machineId} profiles=[{string.Join(", ", config.DeclaredProfiles)}] strays_reaped={daemon.StraysReaped} control={channelMode}");

        using var shutdown = new CancellationTokenSource();
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // signalled
        }

        Console.WriteLine("docketd shutting down; killing everything it started");
        await daemon.ShutdownAsync();
        if (wsChannel is not null)
            await wsChannel.DisposeAsync();
        return 0;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>
/// Placeholder channel: mirrors what the real transport would ship to the
/// console. Best-effort and never queues, matching <see cref="IControlPlaneChannel"/>.
/// </summary>
internal sealed class ConsoleControlPlaneChannel : IControlPlaneChannel
{
    public Task<bool> PublishAsync(RunnerEvent evt, long gapBefore, CancellationToken ct)
    {
        var gap = gapBefore > 0 ? $" [gap:{gapBefore}]" : "";
        Console.WriteLine($"event {evt.GetType().Name}{gap}");
        return Task.FromResult(true);
    }

    public Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct)
    {
        Console.WriteLine($"heartbeat ready={heartbeat.Ready} running={heartbeat.RunningTasks} mem={heartbeat.Load.MemoryLoad:P0} disk={heartbeat.Load.DiskUsage:P0}");
        return Task.FromResult(true);
    }
}
