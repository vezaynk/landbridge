using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Docket.Contracts;
using Docket.Core;

namespace Docket.Runner;

/// <summary>
/// The outcome of a §10 process command. One type for start/stop/write because the callers
/// share a reply shape and a closed refusal vocabulary; the payload differs by which of the
/// three you asked for.
/// </summary>
public abstract record ProcessOutcome
{
    private ProcessOutcome() { }

    public sealed record StartedOk(string? LogPath) : ProcessOutcome;

    public sealed record StoppedOk(int? ExitCode) : ProcessOutcome;

    public sealed record WrittenOk(int Bytes) : ProcessOutcome;

    public sealed record RefusedOutcome(string Refusal, string Detail) : ProcessOutcome;

    public static ProcessOutcome Started(string? logPath) => new StartedOk(logPath);

    public static ProcessOutcome Stopped(int? exitCode) => new StoppedOk(exitCode);

    public static ProcessOutcome Written(int bytes) => new WrittenOk(bytes);

    public static ProcessOutcome Refused(string refusal, string detail) => new RefusedOutcome(refusal, detail);
}

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
    private readonly ConcurrentDictionary<Task, byte> _loops = new();
    private readonly ConcurrentDictionary<string, Task> _watchers = new(StringComparer.Ordinal);
    private readonly object _admission = new();

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
            Launch(service.Name);
        }
    }

    /// <summary>
    /// What the heartbeat reports (§10, §12): the machine's own view of each declared
    /// service. Ordered by name so the dashboard is stable between refreshes.
    /// </summary>
    public IReadOnlyList<ServiceStatus> Report() => ReportServices();

    /// <summary>
    /// §10/§12: agent-started <b>processes</b>, reported separately from services because they
    /// are a different thing — never restarted, so <c>exited</c> is a resting state, and
    /// machine-scoped rather than operator-declared.
    /// </summary>
    public IReadOnlyList<ProcessStatus> ReportProcesses()
    {
        var report = new List<ProcessStatus>();
        foreach (var (name, s) in _state.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (s.Owner is not { } owner)
                continue; // a config-declared service
            lock (s.Gate)
            {
                report.Add(new ProcessStatus(
                    name,
                    s.State,
                    owner.Value,
                    s.StartedAt,
                    s.LastExitCode,
                    s.LastFailureAt,
                    s.StdinOpen));
            }
        }
        return report;
    }

    private IReadOnlyList<ServiceStatus> ReportServices()
    {
        var report = new List<ServiceStatus>(_state.Count);
        foreach (var (key, s) in _state.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (s.Owner is not null)
                continue; // an agent-started process; reported separately
            lock (s.Gate)
            {
                report.Add(new ServiceStatus(
                    key,
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
            // Only config-declared services ever have a port. A §10 process declares none, so it
            // is invisible to refuse-at-dial by construction — and must stay so, or stopping a
            // process could start refusing dials for a listener Docket never tracked.
            if (s.Owner is not null)
                continue;
            var declared = s.Config.Port ?? s.Config.Readiness?.TcpPort;
            if (declared is null || declared != port)
                continue;
            lock (s.Gate)
                return s.State == ServiceState.Running;
        }
        return null;
    }

    /// <summary>
    /// §10: admit and start an agent-declared <b>process</b>, or refuse with a specific reason.
    /// Checks run in the order an operator would want them reported — policy, then shape, then
    /// resources — so the first refusal names the thing that actually needs changing.
    ///
    /// <para>Serialized on <see cref="_admission"/>: the name and port checks and the insert
    /// must be indivisible, or two concurrent calls could both see a name free and both take
    /// it. Config load can check uniqueness at rest; a wire declaration has to check it under
    /// a lock.</para>
    ///
    /// <para><b>No restart.</b> A process is a job, not a daemon: it is spawned, watched, and
    /// its exit recorded for the agent to act on. Hiding a crash behind a backoff ladder would
    /// throw away the one piece of information the agent needs.</para>
    /// </summary>
    public async Task<ProcessOutcome> StartProcessAsync(
        StartProcessCommand command, ProfileConfig profile, CancellationToken ct)
    {
        var policy = profile.ProcessPolicy;
        if (!policy.AgentInitiated)
        {
            return ProcessOutcome.Refused(
                ProcessRefusals.ProfileNotPermitted,
                $"profile '{profile.Name}' does not permit agent-started processes");
        }

        if (!RunnerConfig.IsValidServiceName(command.Name))
        {
            // The name arrives off the wire here, not from a file an operator wrote — which is
            // the case the validator exists for: it becomes a directory name, and the closed
            // path construction depends on it being unable to steer one.
            return ProcessOutcome.Refused(
                ProcessRefusals.InvalidName,
                "name must be 1-64 characters of a-z, A-Z, 0-9, '-' or '_'");
        }

        if (command.Spawn.Count == 0)
            return ProcessOutcome.Refused(ProcessRefusals.InvalidSpawn, "spawn argv is empty");

        ServiceConfig config;
        lock (_admission)
        {
            // One namespace across processes AND services, so a clash is always reported rather
            // than silently resolved. Names are machine-scoped because the agent that cleans up
            // is a continuation — a different task id — so a task-scoped name would be
            // unreachable by the very worker sent to tidy it.
            // Names are unique among LIVE entries only. An exited process has released its
            // name, so a retry — or a later task reusing the same name — is not blocked by a
            // corpse. A config-declared service always holds its name, exited or not, because
            // it will be restarted into it.
            if (_state.TryGetValue(command.Name, out var existing))
            {
                var live = existing.Owner is null || existing.State is not (ServiceState.Exited or ServiceState.Stopped);
                if (live)
                {
                    var kind = existing.Owner is null ? "service" : "process";
                    return ProcessOutcome.Refused(
                        ProcessRefusals.NameTaken,
                        $"the name '{command.Name}' is already held by a running {kind} on this machine");
                }

                _state.TryRemove(command.Name, out _); // reclaim the exited entry's name
            }

            // No port check: a process declares no port (§10). The admission lock now guards
            // machine-scoped NAME uniqueness only — still indivisible, because two concurrent
            // starts could otherwise both see a name free and both take it.
            var running = _state.Count(e =>
                e.Value.Owner is not null
                && e.Value.State is not (ServiceState.Exited or ServiceState.Stopped));
            if (running >= policy.Max)
            {
                return ProcessOutcome.Refused(
                    ProcessRefusals.CapReached,
                    $"this machine already holds {running} agent-started processes " +
                    $"(max {policy.Max})");
            }

            config = new ServiceConfig(
                command.Name,
                command.Spawn,
                command.WorkingDirectory,
                command.Env ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Port: null,
                Readiness: null, // a process is running once its OS process is up (§10)
                ServiceDefaults.MaxBackoff,
                // Capture on: the log path goes back in the reply so the declaring agent reads
                // its own output with file tools.
                new LogsConfig(null, null, Capture: true));

            _state[command.Name] = new SupervisedService(config)
            {
                State = ServiceState.Stopped,
                Owner = command.Task,
                StdinOpen = command.OpenStdin,
            };
        }

        var s = _state[command.Name];
        if (!await TryStartAsync(s, ct))
        {
            _state.TryRemove(command.Name, out _);
            Stop(s);
            // With no readiness check there is exactly one way to fail to come up.
            return ProcessOutcome.Refused(
                ProcessRefusals.SpawnFailed, $"'{command.Name}' could not be started");
        }

        // Watch for exit and record it. No restart: the exit code is the point.
        _watchers[command.Name] = Task.Run(() => WatchProcessAsync(command.Name, s, _cts.Token));

        return ProcessOutcome.Started(_logs is null ? null : Path.Combine(_logs.Root, command.Name));
    }

    /// <summary>
    /// §10: stop an agent-started process. <b>Machine-scoped</b> — any worker dispatched here
    /// may stop any process here, which is precisely what lets a Lead's cleanup continuation (a
    /// new task id) tidy up what an earlier task started. Cleanup is orchestration, not
    /// enforcement.
    /// </summary>
    public async Task<ProcessOutcome> StopProcessAsync(string name, CancellationToken ct)
    {
        if (!_state.TryGetValue(name, out var s) || s.Owner is null)
        {
            return ProcessOutcome.Refused(
                ProcessRefusals.NoSuchProcess, $"no agent-started process named '{name}' here");
        }

        Process? process;
        lock (s.Gate)
            process = s.Process;

        // Graceful first, then the kill — the same wind-down shape a message-mode worker stop
        // uses (§10/§11): close the held-open stdin so a child watching its input sees EOF and
        // can exit on its own terms, wait a bounded moment, and only then take the tree. A
        // build flushing its output deserves that; a wedged one does not get to refuse.
        //
        // That lever only exists when stdin was opened. For an open_stdin:false process there is
        // nothing to signal portably — signals do not cross to Windows, which is why there is no
        // signal_process — so its stop is the bounded wait and then the tree. Choosing closed
        // stdin is choosing a hard stop, and the skill says so.
        if (process is not null && !SafeHasExited(process) && s.StdinOpen)
        {
            try { process.StandardInput.Close(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // stdin already gone; the kill below is the whole story
            }

            try { await process.WaitForExitAsync(ct).WaitAsync(ServiceDefaults.StopWindDown, ct); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
                // did not go quietly
            }
        }

        _state.TryRemove(name, out _);
        Stop(s);

        int? exitCode;
        lock (s.Gate)
            exitCode = s.LastExitCode ?? (process is not null ? SafeExitCode(process) : null);

        _log?.Invoke($"docketd: process '{name}' stopped on request (exit {exitCode?.ToString() ?? "n/a"})");
        return ProcessOutcome.Stopped(exitCode);
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>
    /// §10: write to an agent-started process's stdin, reusing the same held-open pipe the
    /// message-mode worker stop injects a turn into.
    ///
    /// <para><b>It is a pipe, not a TTY.</b> Success means the pipe accepted the bytes — never
    /// that the program understood them, and never that a prompt was answered. A program that
    /// checks for a terminal behaves differently here, and one that reads <c>/dev/tty</c> never
    /// sees this at all. Whatever it says back appears in the log file.</para>
    /// </summary>
    public async Task<ProcessOutcome> WriteProcessAsync(
        string name, string data, bool appendNewline, CancellationToken ct)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(data) > ProcessStdin.MaxBytes)
        {
            return ProcessOutcome.Refused(
                ProcessRefusals.PayloadTooLarge,
                $"a single write is capped at {ProcessStdin.MaxBytes} bytes; send several");
        }

        if (!_state.TryGetValue(name, out var s) || s.Owner is null)
        {
            return ProcessOutcome.Refused(
                ProcessRefusals.NoSuchProcess, $"no agent-started process named '{name}' here");
        }

        Process? process;
        lock (s.Gate)
        {
            if (!s.StdinOpen)
            {
                return ProcessOutcome.Refused(
                    ProcessRefusals.StdinNotOpened,
                    $"'{name}' was started with open_stdin false, so it has no pipe to write to");
            }
            if (s.State != ServiceState.Running)
                return ProcessOutcome.Refused(ProcessRefusals.NotRunning, $"'{name}' is not running");
            process = s.Process;
        }

        if (process is null)
            return ProcessOutcome.Refused(ProcessRefusals.NotRunning, $"'{name}' is not running");

        try
        {
            var payload = appendNewline ? data + "\n" : data;
            await process.StandardInput.WriteAsync(payload.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            return ProcessOutcome.Written(System.Text.Encoding.UTF8.GetByteCount(payload));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return ProcessOutcome.Refused(
                ProcessRefusals.StdinUnavailable, $"'{name}' stdin is closed or broken: {e.Message}");
        }
    }

    /// <summary>
    /// Records an agent-started process's exit and stops there. No restart, no backoff: for a
    /// job, the exit code IS the result, and the agent — possibly a resumed one — decides.
    /// </summary>
    private async Task WatchProcessAsync(string name, SupervisedService s, CancellationToken ct)
    {
        await WaitForExitAsync(s, ct);
        if (ct.IsCancellationRequested)
            return;

        lock (s.Gate)
        {
            s.State = ServiceState.Exited;
            s.LastFailureAt = _clock.GetUtcNow();
            s.StartedAt = null;
        }
        _log?.Invoke($"docketd: process '{name}' exited (code {s.LastExitCode?.ToString() ?? "n/a"}); not restarted");
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
        foreach (var loop in _loops.Keys)
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
    /// <summary>Starts one supervision loop for an admitted service and tracks it.</summary>
    private void Launch(string name)
    {
        var loop = Task.Run(() => SuperviseAsync(name, _cts.Token));
        _loops[loop] = 0;
    }

    private async Task SuperviseAsync(string key, CancellationToken ct)
    {
        if (!_state.TryGetValue(key, out var s))
            return;
        var service = s.Config;
        var backoff = ServiceDefaults.InitialBackoff;

        while (!ct.IsCancellationRequested && _state.ContainsKey(key))
        {
            var started = await TryStartAsync(s, ct);
            if (started)
            {
                backoff = ServiceDefaults.InitialBackoff; // a good start resets the ladder
                await WaitForExitAsync(s, ct);
            }

            // A task-scoped service whose owner has gone is not "failed" — it was torn
            // down on purpose, and restarting it would resurrect a process whose authority
            // ended with its task.
            if (ct.IsCancellationRequested || !_state.ContainsKey(key))
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
            if (!_state.ContainsKey(key))
                return;

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
            // Always redirected: held open it is the dead-man's switch a task spawn relies on,
            // and closed immediately (below) it is the portable way to give a child a stdin that
            // returns EOF instead of blocking. Leaving it un-redirected would hand the child
            // whatever docketd inherited, which is not a defined thing to give it.
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
        // §10: machine id only, and DELIBERATELY no DOCKET_TASK_ID — for services and
        // agent-started processes alike. The task-id tag is what the per-task exit sweep reaps
        // by, so carrying it would kill a process the moment its declaring worker's turn ended:
        // exactly the loss this feature exists to prevent. Both kinds are therefore bound to
        // the machine generation and nothing smaller, and both are ended only by an explicit
        // stop, their own exit, or this daemon's restart sweep.
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

        // §10 open_stdin: false — close the pipe at once so the child's first read is EOF rather
        // than a wait for input nobody will send. It does NOT make stdin a terminal: isatty is
        // false either way, so this fixes blocking, not detection.
        if (!s.StdinOpen)
        {
            try { process.StandardInput.Close(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // nothing to close; the child simply never had a writable pipe
            }
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

    /// <summary>
    /// A service's machine-local identity. Scoped to the declaring task so two tasks may
    /// each hold a <c>dev</c> without colliding — what actually collides is the <b>port</b>,
    /// which admission checks separately. A null task is a config-declared service.
    /// </summary>
    private readonly record struct ServiceKey(TaskId? Task, string Name);

    /// <summary>One supervised service's mutable state, guarded by its own gate.</summary>
    private sealed class SupervisedService(ServiceConfig config)
    {
        public object Gate { get; } = new();
        public ServiceConfig Config { get; } = config;

        /// <summary>The task that declared this service, or null for a config-declared one.
        /// Provenance for a process; a config service has none.</summary>
        public TaskId? Owner { get; init; }

        /// <summary>§10: whether this process was given a usable stdin pipe. False means no
        /// <c>write_process</c> and no graceful EOF stop — choosing closed stdin is choosing a
        /// hard stop. Always true for a config-declared service (the dead-man pipe).</summary>
        public bool StdinOpen { get; init; } = true;
        public Process? Process { get; set; }
        public ServiceState State { get; set; } = ServiceState.Stopped;
        public DateTimeOffset? StartedAt { get; set; }
        public int Restarts { get; set; }
        public int? LastExitCode { get; set; }
        public DateTimeOffset? LastFailureAt { get; set; }
        internal WindowsJobObject? Job { get; set; }
    }
}
