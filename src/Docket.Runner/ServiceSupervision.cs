using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Docket.Contracts;

namespace Docket.Runner;

/// <summary>
/// Supervises the operator's declared long-lived services (§10), as a deliberate
/// <b>sibling</b> of <see cref="ProcessSupervisor"/> rather than a mode of it.
///
/// <para><b>Why a service is docketd's own child.</b> A service a worker starts is a
/// descendant of the harness, so the task tree-kill takes it down when the task ends,
/// and it carries <c>DOCKET_*</c>, so the stray reaper takes it down later if it
/// escaped the group. Both are correct for a build step and wrong for "keep the dev
/// server up". Handing the process to the machine's service manager solves it on
/// Linux, but macOS has no clean transient equivalent, a container has no init, and
/// Windows has nothing user-level — so the only answer that is the same everywhere is
/// for docketd to own the process itself. That places it outside every task's tree by
/// construction, with no <c>setsid</c> and no environment scrubbing, and keeps the
/// kill guarantee inside Docket.
///
/// <para><b>Restart equals reboot, here too.</b> Every service is tagged with
/// <c>DOCKET_MACHINE_ID</c> and <em>not</em> <c>DOCKET_TASK_ID</c>. That combination is
/// load-bearing in both directions: the restart sweep
/// (<see cref="StrayReaper.Reap"/>, keyed on machine id) kills the previous
/// generation's services before this one starts them, so a SIGKILLed daemon cannot
/// leave a port-holding orphan that the new daemon then collides with; and per-task
/// exit cleanup (<see cref="StrayReaper.ReapTask"/>, which requires a matching task id)
/// steps over them, so an ordinary task ending never takes a service down. No PID
/// registry, no re-adoption — services are restartable, so restarting them is cheaper
/// and more predictable than reasoning about which survivors are still healthy.</para>
///
/// <para>Services are not tasks. They have no per-task liveness clocks, they do not
/// count toward <see cref="ProcessSupervisor.RunningTotal"/> or a profile's
/// <c>max_concurrent</c> (those gate task admission), and they consume load that
/// back-pressure already observes directly.</para>
/// </summary>
public sealed class ServiceSupervisor : IAsyncDisposable
{
    private readonly IReadOnlyList<ServiceConfig> _services;
    private readonly string _machineId;
    private readonly TimeProvider _clock;
    private readonly ServiceLogStore? _logs;
    private readonly Action<string>? _log;
    private readonly SpawnerThread _spawner = new();
    private readonly ConcurrentDictionary<string, SupervisedService> _state = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];

    /// <param name="probe">
    /// Readiness probe seam. Defaults to a real loopback TCP connect; a test supplies
    /// its own so it never has to bind a port.
    /// </param>
    public ServiceSupervisor(
        IReadOnlyList<ServiceConfig> services,
        string machineId,
        TimeProvider clock,
        ServiceLogStore? logs = null,
        Action<string>? log = null,
        Func<int, CancellationToken, Task<bool>>? probe = null)
    {
        _services = services;
        _machineId = machineId;
        _clock = clock;
        _logs = logs;
        _log = log;
        _probe = probe ?? TryConnectAsync;

        foreach (var service in services)
        {
            _state[service.Name] = new SupervisedService(service)
            {
                State = service.Enabled ? ServiceState.Stopped : ServiceState.Disabled,
            };
        }
    }

    private readonly Func<int, CancellationToken, Task<bool>> _probe;

    /// <summary>Starts one supervision loop per declared service. Returns immediately.</summary>
    public void Start()
    {
        foreach (var service in _services)
        {
            // `enabled: false` is the operator's declared "off". Nothing supervises it, so
            // there is no desired-state divergence to reconcile — which is exactly why the
            // stop lives in config rather than in a dashboard command a restart would undo.
            if (!service.Enabled)
                continue;
            _loops.Add(Task.Run(() => SuperviseAsync(service, _cts.Token)));
        }
    }

    /// <summary>
    /// What the heartbeat reports (§10, §12): the machine's own view of each declared
    /// service. Ordered by name so the dashboard is stable between refreshes.
    /// </summary>
    public IReadOnlyList<ServiceStatus> Report()
    {
        var report = new List<ServiceStatus>(_state.Count);
        foreach (var name in _state.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!_state.TryGetValue(name, out var s))
                continue;
            lock (s.Gate)
            {
                report.Add(new ServiceStatus(
                    name,
                    s.State,
                    s.Config.Port ?? s.Config.Readiness?.TcpPort ?? 0,
                    s.StartedAt,
                    s.Restarts,
                    s.LastExitCode,
                    s.LastFailureAt));
            }
        }
        return report;
    }

    /// <summary>
    /// Whether a declared service is currently up — the fact behind refuse-at-dial
    /// (§8.2/§8.3). Returns null when this machine declares no such service, which is
    /// a different answer from "declared but down": the caller must not refuse a dial
    /// for a port docketd knows nothing about, because that port may legitimately
    /// belong to a worker-started listener.
    /// </summary>
    public bool? IsServiceOnPort(int port)
    {
        foreach (var s in _state.Values)
        {
            var declared = s.Config.Port ?? s.Config.Readiness?.TcpPort;
            if (declared != port)
                continue;
            lock (s.Gate)
                return s.State == ServiceState.Running;
        }
        return null;
    }

    /// <summary>Kills every supervised service (§10 clean shutdown).</summary>
    public void KillAll()
    {
        foreach (var s in _state.Values)
            Stop(s);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        KillAll();
        foreach (var loop in _loops)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }
        _spawner.Close();
        _cts.Dispose();
    }

    /// <summary>
    /// One service's whole life: start, probe, watch, back off, start again. Exponential
    /// backoff is capped by <c>restart.max_backoff_seconds</c> so a service that cannot
    /// start (a bad path, a taken port) settles into a slow retry that stays visible in
    /// the heartbeat instead of hot-looping.
    /// </summary>
    private async Task SuperviseAsync(ServiceConfig service, CancellationToken ct)
    {
        var backoff = ServiceDefaults.InitialBackoff;
        var s = _state[service.Name];

        while (!ct.IsCancellationRequested)
        {
            var started = await TryStartAsync(s, ct);
            if (started)
            {
                backoff = ServiceDefaults.InitialBackoff; // a good start resets the ladder
                await WaitForExitAsync(s, ct);
            }

            if (ct.IsCancellationRequested)
                return;

            lock (s.Gate)
            {
                s.State = ServiceState.Failed;
                s.LastFailureAt = _clock.GetUtcNow();
                s.StartedAt = null;
            }

            _log?.Invoke(
                $"docketd: service '{service.Name}' is down (exit {s.LastExitCode?.ToString() ?? "n/a"}); " +
                $"restarting in {backoff.TotalSeconds:0.#}s");

            try { await Task.Delay(backoff, _clock, ct); }
            catch (OperationCanceledException) { return; }

            lock (s.Gate)
                s.Restarts++;
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, service.MaxBackoff.Ticks));
        }
    }

    private async Task<bool> TryStartAsync(SupervisedService s, CancellationToken ct)
    {
        var service = s.Config;
        var psi = new ProcessStartInfo
        {
            FileName = service.Spawn[0],
            // §10: argv, never a shell — the same rule as a task spawn.
            RedirectStandardOutput = _logs is not null && service.Logs.Capture,
            RedirectStandardError = _logs is not null && service.Logs.Capture,
            // Held open so the child sees a parent that is alive; closing it on our
            // death is the same dead-man's switch a task spawn relies on.
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        for (var i = 1; i < service.Spawn.Count; i++)
            psi.ArgumentList.Add(service.Spawn[i]);

        if (!string.IsNullOrWhiteSpace(service.WorkingDirectory))
            psi.WorkingDirectory = service.WorkingDirectory;

        foreach (var (k, v) in service.Env)
            psi.Environment[k] = v;

        // The tagging that makes restart-equals-reboot cover services: machine id so
        // the restart sweep reaps the previous generation, and deliberately NO task id
        // so per-task exit cleanup steps over them.
        psi.Environment["DOCKET_MACHINE_ID"] = _machineId;
        psi.Environment.Remove("DOCKET_TASK_ID");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            // PDEATHSIG thread affinity, exactly as for a task spawn: the fork must
            // happen on a thread that outlives the child, or Linux PDEATHSIG — keyed to
            // the forking thread — would kill a healthy service when a pool thread retired.
            _spawner.Run(() =>
            {
                process.Start();
                if (OperatingSystem.IsWindows())
                    s.Job = WindowsJobObject.TryCreateAndAssign(process.Handle, out _);
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            _log?.Invoke($"docketd: service '{s.Config.Name}' failed to start: {e.Message}");
            lock (s.Gate)
            {
                s.State = ServiceState.Failed;
                s.LastFailureAt = _clock.GetUtcNow();
                s.LastExitCode = null;
            }
            process.Dispose();
            return false;
        }

        lock (s.Gate)
        {
            s.Process = process;
            s.State = ServiceState.Starting;
            s.StartedAt = _clock.GetUtcNow();
        }

        if (_logs is not null && service.Logs.Capture)
            StartCapture(s, process);

        if (service.Readiness is { } readiness && !await ProbeAsync(s, readiness, ct))
        {
            _log?.Invoke(
                $"docketd: service '{s.Config.Name}' did not answer on 127.0.0.1:{readiness.TcpPort} " +
                $"within {readiness.Timeout.TotalSeconds:0.#}s");
            Stop(s);
            return false;
        }

        lock (s.Gate)
            s.State = ServiceState.Running;
        _log?.Invoke($"docketd: service '{s.Config.Name}' up (pid {process.Id})");
        return true;
    }

    /// <summary>Polls the readiness port until it answers or the timeout elapses. A real
    /// check: it is what makes "the port answers" true rather than assumed (§8.2).</summary>
    private async Task<bool> ProbeAsync(SupervisedService s, ReadinessConfig readiness, CancellationToken ct)
    {
        var deadline = _clock.GetUtcNow() + readiness.Timeout;
        while (_clock.GetUtcNow() < deadline && !ct.IsCancellationRequested)
        {
            lock (s.Gate)
            {
                if (s.Process is { } p && p.HasExited)
                    return false; // died during startup; no point probing
            }

            if (await _probe(readiness.TcpPort, ct))
                return true;

            try { await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static async Task<bool> TryConnectAsync(int port, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task WaitForExitAsync(SupervisedService s, CancellationToken ct)
    {
        Process? process;
        lock (s.Gate)
            process = s.Process;
        if (process is null)
            return;

        try
        {
            await process.WaitForExitAsync(ct);
            lock (s.Gate)
                s.LastExitCode = SafeExitCode(process);
        }
        catch (OperationCanceledException)
        {
            // shutting down; the caller checks ct
        }
    }

    /// <summary>
    /// Captures this service's stdout/stderr through the same writer a task transcript
    /// uses (§12) — a tee that never blocks and never kills: on reaching the byte cap it
    /// writes a truncation marker and keeps draining, because logging must not be able
    /// to affect the service.
    /// </summary>
    private void StartCapture(SupervisedService s, Process process)
    {
        var writer = _logs!.CreateWriter(s.Config.Name, s.Config.Logs.MaxBytes);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var stdout = TranscriptCapture.PumpLinesAsync(process.StandardOutput, writer.WriteStdoutLine, cts.Token);
        var stderr = TranscriptCapture.PumpLinesAsync(process.StandardError, writer.WriteStderrLine, cts.Token);
        _ = Task.WhenAll(stdout, stderr).ContinueWith(
            _ =>
            {
                writer.Dispose();
                cts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Stop(SupervisedService s)
    {
        Process? process;
        lock (s.Gate)
        {
            process = s.Process;
            s.Process = null;
            // Keep a disabled service reading Disabled through teardown; flattening it to
            // Stopped would lose the distinction the operator set it for.
            s.State = s.Config.Enabled ? ServiceState.Stopped : ServiceState.Disabled;
            s.StartedAt = null;
        }

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }

        if (OperatingSystem.IsWindows())
            s.Job?.TerminateAndClose();

        process.Dispose();
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>One supervised service's mutable state, guarded by its own gate.</summary>
    private sealed class SupervisedService(ServiceConfig config)
    {
        public object Gate { get; } = new();
        public ServiceConfig Config { get; } = config;
        public Process? Process { get; set; }
        public ServiceState State { get; set; } = ServiceState.Stopped;
        public DateTimeOffset? StartedAt { get; set; }
        public int Restarts { get; set; }
        public int? LastExitCode { get; set; }
        public DateTimeOffset? LastFailureAt { get; set; }
        internal WindowsJobObject? Job { get; set; }
    }
}
