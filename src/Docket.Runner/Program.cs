using System.Runtime.InteropServices;
using Docket.Contracts;

namespace Docket.Runner;

/// <summary>
/// The <c>docketd</c> entrypoint (spec §10). Loads and validates a config,
/// wires the supervisor / back-pressure / heartbeat / ring, and runs until a
/// termination signal. The control-plane wire transport is a real outbound
/// WebSocket (<see cref="WebSocketControlPlaneChannel"/>, §10 — the frozen
/// interface's real bytes): it dials the plane with a fixed env token or the
/// enrolled machine credentials, and falls back to a
/// <see cref="ConsoleControlPlaneChannel"/> that mirrors events to the console
/// only when neither a URL nor credentials are configured. The daemon logic —
/// stray reaping, reboot announcement, back-pressure gating, heartbeat cadence —
/// is real and runnable.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // §5 Bootstrap: `docketd --enroll --control-url <https://plane>` is a
        // one-shot mode — exchange the human-issued enrollment token for machine
        // credentials, persist them (atomic, 0600), print the machine id, and exit.
        // It never starts the daemon. The token is read from --enroll-token-file or
        // stdin, NEVER argv (§13: an argument lands in shell history and the process
        // list); see RunEnrollAsync.
        if (HasFlag(args, "--enroll"))
            return await RunEnrollAsync(args);

        var configPath = ArgValue(args, "--config");
        if (configPath is null)
        {
            Console.Error.WriteLine("usage: docketd --config <path> [--machine-id <id>] [--state-dir <dir>]");
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

        // §1 tracing: stand up OTLP export when the environment provides a
        // collector (the Aspire dev loop does; a standalone runner leaves it unset
        // and this no-ops). Disposed last so the handle spans flush on shutdown.
        using var otelExport = RunnerTelemetry.TryStartOtelExport();

        var clock = TimeProvider.System;
        var ring = new OutboundEventRing(capacity: 1024);
        var reaper = new StrayReaper(ProcessInventory.ForCurrentPlatform(), Environment.ProcessId);

        // §12 machine-local transcript capture lives under the state dir (alongside
        // credentials.json), not the per-task work_root scratch — it must survive a
        // task teardown and a docketd restart (the §11 resume substrate). Local
        // retention is machine hygiene: the most generous prune_after_days any profile
        // asked for wins, and any profile opting out (0) keeps everything.
        var stateDir = CredentialStore.ResolveStateDir(ArgValue(args, "--state-dir"));
        var pruneDays = config.Profiles.Values.Select(p => p.Logs.PruneAfterDays).ToArray();
        var retention = pruneDays.Any(d => d <= 0)
            ? TimeSpan.Zero
            : TimeSpan.FromDays(pruneDays.Max());
        var transcripts = new TranscriptStore(
            Path.Combine(stateDir, TranscriptDefaults.DirName), retention, clock);

        var supervisor = new ProcessSupervisor(config.Machine, ring, clock, reaper, transcripts);
        var backPressure = new BackPressureMonitor(
            SystemLoadReader.ForCurrentPlatform(config.Machine.WorkRoot), config.Machine.BackPressure);

        // §10 back-pressure: Linux/macOS/Windows get a real CPU-observing reader
        // (max_cpu_load enforced). Only an unknown platform falls back to the
        // portable reader that can't observe CPU — surface that max_cpu_load is
        // inert there rather than letting it look enforced; memory and disk still gate.
        if (!backPressure.ObservesCpu)
            Console.WriteLine("docketd: cpu back-pressure unavailable on this platform; max_cpu_load is inert (memory and disk still gate).");

        // §10 event relay: terminal is the only implemented source. A profile
        // declaring hooks/otel/none emits one event ever (started, at spawn), so the
        // plane's per-task liveness window requeues everything it runs that outlives
        // that window. Silent degradation here reads as a hung harness, so say it
        // loudly at startup — same posture as the inert-max_cpu_load line above.
        foreach (var warning in config.EventRelayWarnings())
            Console.WriteLine(warning);

        // §10 + §5: dial the control plane outbound. Credential source, in
        // priority order:
        //   1. DOCKET_MACHINE_TOKEN env — the Aspire dev loop's fixed token, NEVER
        //      refreshed (unchanged behaviour). DOCKET_CONTROL_URL is the ws(s) URL.
        //   2. else credentials.json in the state dir — the enrolled machine's
        //      access/refresh pair. The access token is refreshed at half-life and
        //      on a 401 reconnect (§13); the ws URL is DOCKET_CONTROL_URL when set,
        //      else derived from the enrolled HTTP base (http→ws, https→wss, /runner).
        //   3. else the console placeholder.
        var controlUrl = Environment.GetEnvironmentVariable("DOCKET_CONTROL_URL");
        var envMachineToken = Environment.GetEnvironmentVariable("DOCKET_MACHINE_TOKEN");

        WebSocketControlPlaneChannel? wsChannel = null;
        MachineTokenRefresher? refresher = null;
        HttpClient? refreshHttp = null;
        IControlPlaneChannel channel;
        string channelMode;

        if (!string.IsNullOrWhiteSpace(envMachineToken))
        {
            if (string.IsNullOrWhiteSpace(controlUrl))
            {
                channel = new ConsoleControlPlaneChannel();
                channelMode = "console";
            }
            else
            {
                wsChannel = new WebSocketControlPlaneChannel(new Uri(controlUrl), envMachineToken, clock, Console.WriteLine);
                channel = wsChannel;
                channelMode = controlUrl;
            }
        }
        else if (CredentialStore.Load(stateDir) is { } creds)
        {
            machineId = creds.MachineId;
            refreshHttp = new HttpClient();
            refresher = new MachineTokenRefresher(
                creds,
                (refreshToken, refreshCt) => CredentialStore.RefreshAsync(refreshHttp, creds.ControlUrl, refreshToken, refreshCt),
                updated => CredentialStore.Save(stateDir, updated),
                clock,
                Console.WriteLine);

            var wsUri = !string.IsNullOrWhiteSpace(controlUrl)
                ? new Uri(controlUrl)
                : CredentialStore.DeriveRunnerWsUrl(creds.ControlUrl);
            wsChannel = new WebSocketControlPlaneChannel(
                wsUri, () => refresher.CurrentAccessToken, clock, Console.WriteLine,
                onAuthRejected: refresher.RefreshOnceAsync);
            channel = wsChannel;
            channelMode = wsUri.ToString();
            refresher.Start();
        }
        else
        {
            channel = new ConsoleControlPlaneChannel();
            channelMode = "console";
        }

        // §12 serving: the same store capture writes to, read side. Always wired — the
        // gate on who may read a transcript is the plane's (human operator, terminal task
        // only), not something docketd second-guesses; a machine that captured nothing
        // simply answers with an empty inventory.
        var daemon = new RunnerDaemon(
            machineId, config, supervisor, backPressure, channel, ring, reaper, clock,
            transcripts: new TranscriptReader(transcripts));
        await daemon.StartAsync();

        // Outbound-only: the receive loop runs on the socket docketd dialed, not
        // a listener (§10). Commands arriving on it drive the daemon.
        wsChannel?.Start((command, ct) => daemon.HandleAsync(command, ct));

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
        if (refresher is not null)
            await refresher.DisposeAsync();
        refreshHttp?.Dispose();
        return 0;
    }

    /// <summary>
    /// The <c>--enroll</c> one-shot (§5 Bootstrap). Exchanges the enrollment token
    /// for machine credentials over HTTP and persists them; prints the machine id
    /// to stdout (scriptable) and a human note to stderr. Purpose/name/permission
    /// level are declared here and bound server-side (§13); os is auto-filled.
    /// </summary>
    private static async Task<int> RunEnrollAsync(string[] args)
    {
        var controlUrl = ArgValue(args, "--control-url");
        if (string.IsNullOrWhiteSpace(controlUrl))
        {
            Console.Error.WriteLine(
                "usage: docketd --enroll --control-url <https://plane> " +
                "[--enroll-token-file <path>] [--state-dir <dir>] [--name <n>] [--purpose <p>] [--permission-level <l>]\n" +
                "  The enrollment token is read from --enroll-token-file, or from stdin if omitted " +
                "(`docketd --enroll < token` or an interactive prompt) — never from argv (§13).");
            return 2;
        }

        // §13: enrollment tokens must NOT be passed as arguments — an argv token
        // lands in shell history and is world-readable in the process list, and the
        // machine token it yields is the highest-value secret on the machine. Read
        // it from a file or from stdin instead.
        var enrollmentToken = await ReadEnrollmentTokenAsync(ArgValue(args, "--enroll-token-file"));
        if (enrollmentToken is null)
            return 2; // the reader already explained why
        if (string.IsNullOrWhiteSpace(enrollmentToken))
        {
            Console.Error.WriteLine(
                "enrollment token is empty; provide --enroll-token-file <path> or pipe/enter it on stdin");
            return 2;
        }

        var stateDir = CredentialStore.ResolveStateDir(ArgValue(args, "--state-dir"));
        var request = new EnrollRequest(
            EnrollmentToken: enrollmentToken,
            Name: ArgValue(args, "--name") ?? Environment.MachineName,
            Purpose: ArgValue(args, "--purpose") ?? "general",
            Os: RuntimeInformation.OSDescription,
            PermissionLevel: ArgValue(args, "--permission-level") ?? "standard");

        using var http = new HttpClient();
        try
        {
            var creds = await CredentialStore.EnrollAsync(http, controlUrl, request, CancellationToken.None);
            CredentialStore.Save(stateDir, creds);
            Console.WriteLine(creds.MachineId);
            Console.Error.WriteLine(
                $"docketd enrolled: machine={creds.MachineId} credentials={CredentialStore.PathIn(stateDir)}");
            return 0;
        }
        catch (EnrollmentException e)
        {
            Console.Error.WriteLine($"enrollment failed: {e.Message}");
            return 1;
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"enrollment failed: could not reach {controlUrl}: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Reads the enrollment token from a file (<paramref name="tokenFile"/>) or,
    /// when none is given, one line from stdin — a prompt to stderr when interactive,
    /// a clean read when piped (`docketd --enroll &lt; token`). Never argv (§13).
    /// Returns null (having printed why) when a named file can't be read.
    /// </summary>
    internal static async Task<string?> ReadEnrollmentTokenAsync(string? tokenFile)
    {
        if (tokenFile is not null)
        {
            try { return (await File.ReadAllTextAsync(tokenFile)).Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"enrollment failed: cannot read token file {tokenFile}: {e.Message}");
                return null;
            }
        }

        // Prompt only on a TTY, so a piped `< token` stays clean and scriptable.
        if (!Console.IsInputRedirected)
            Console.Error.Write("Enrollment token: ");
        return (await Console.In.ReadLineAsync())?.Trim() ?? "";
    }

    private static bool HasFlag(string[] args, string flag) => Array.IndexOf(args, flag) >= 0;

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
