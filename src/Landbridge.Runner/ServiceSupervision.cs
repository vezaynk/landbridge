using System.Collections.Concurrent;
using System.Diagnostics;
using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.Runner;

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
/// Supervises agent-started background processes (§10 <c>start_process</c>), as a
/// deliberate <b>sibling</b> of <see cref="ProcessSupervisor"/> rather than a mode of it.
///
/// <para><b>Why a process is landbridged's own child.</b> A process a worker starts from
/// its own shell is a descendant of the harness, so the session tree-kill takes it down
/// when that session ends, and it carries <c>LANDBRIDGE_*</c>, so the stray reaper takes
/// it down later if it escaped the group. Both are correct for a build step and wrong for
/// "keep the dev server up for the rest of this Team's work". Handing the process to the
/// machine's service manager solves it on Linux, but macOS has no clean transient
/// equivalent, a container has no init, and Windows has nothing user-level — so the only
/// answer that is the same everywhere is for landbridged to own the process itself. That
/// places it outside every session's tree by construction, with no <c>setsid</c> and no
/// environment scrubbing, and keeps the kill guarantee inside Landbridge. Always-on
/// fixtures that must survive a landbridged restart belong to systemd or launchd;
/// leftover <c>services[]</c> is refused at config load.</para>
///
/// <para><b>Restart equals reboot, here too.</b> Every process is tagged with
/// <c>LANDBRIDGE_MACHINE_ID</c> and <em>not</em> <c>LANDBRIDGE_SESSION_ID</c>. That combination is
/// load-bearing in both directions: the restart sweep
/// (<see cref="StrayReaper.Reap"/>, keyed on machine id) kills the previous
/// generation before this one starts anything, so a SIGKILLed daemon cannot
/// leave a port-holding orphan; and per-task exit cleanup
/// (<see cref="StrayReaper.ReapSession"/>, which requires a matching task id)
/// steps over them, so an ordinary task ending never takes a process down. No PID
/// registry, no re-adoption — a process is never restarted, so a landbridged restart
/// is a reboot of the generation.</para>
///
/// <para>Processes are not tasks. They have no per-task liveness clocks, they do not
/// count toward <see cref="ProcessSupervisor.RunningTotal"/>, and they consume load that
/// back-pressure already observes directly.</para>
/// </summary>
public sealed class ServiceSupervisor : IAsyncDisposable
{
    private readonly string _machineId;
    private readonly TimeProvider _clock;
    private readonly ServiceLogStore? _logs;
    private readonly Action<string>? _log;
    private readonly SpawnerThread _spawner = new();
    private readonly ConcurrentDictionary<string, SupervisedService> _state = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _admission = new();

    public ServiceSupervisor(
        string machineId,
        TimeProvider clock,
        ServiceLogStore? logs = null,
        Action<string>? log = null)
    {
        _machineId = machineId;
        _clock = clock;
        _logs = logs;
        _log = log;
    }

    /// <summary>
    /// §10/§12: agent-started <b>processes</b> — never restarted, so <c>exited</c> is a
    /// resting state, and machine-scoped.
    /// </summary>
    public IReadOnlyList<ProcessStatus> ReportProcesses()
    {
        var report = new List<ProcessStatus>();
        foreach (var (name, s) in _state.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            lock (s.Gate)
            {
                report.Add(new ProcessStatus(
                    name,
                    s.State,
                    s.Owner.Value,
                    s.StartedAt,
                    s.LastExitCode,
                    s.LastFailureAt,
                    s.StdinOpen));
            }
        }
        return report;
    }

    /// <summary>
    /// §10: admit and start an agent-declared <b>process</b>, or refuse with a specific reason.
    /// Checks run in the order an operator would want them reported — policy, then shape, then
    /// resources — so the first refusal names the thing that actually needs changing.
    ///
    /// <para>Serialized on <see cref="_admission"/>: the name check and the insert must be
    /// indivisible, or two concurrent calls could both see a name free and both take it.
    /// Config load can check uniqueness at rest; a wire declaration has to check it under
    /// a lock. There is no port check here — a process declares no port (§10).</para>
    ///
    /// <para><b>No restart.</b> A process is a job, not a daemon: it is spawned, watched, and
    /// its exit recorded for the agent to act on. Hiding a crash behind a backoff ladder would
    /// throw away the one piece of information the agent needs.</para>
    /// </summary>
    public Task<ProcessOutcome> StartProcessAsync(
        StartProcessCommand command, ProfileConfig profile, CancellationToken ct)
    {
        var policy = profile.ProcessPolicy;
        if (!policy.AgentInitiated)
        {
            return Task.FromResult(ProcessOutcome.Refused(
                ProcessRefusals.ProfileNotPermitted,
                $"profile '{profile.Name}' does not permit agent-started processes"));
        }

        if (!RunnerConfig.IsValidServiceName(command.Name))
        {
            // The name arrives off the wire here, not from a file an operator wrote — which is
            // the case the validator exists for: it becomes a directory name, and the closed
            // path construction depends on it being unable to steer one.
            return Task.FromResult(ProcessOutcome.Refused(
                ProcessRefusals.InvalidName,
                "name must be 1-64 characters of a-z, A-Z, 0-9, '-' or '_'"));
        }

        if (command.Spawn.Count == 0)
        {
            return Task.FromResult(ProcessOutcome.Refused(
                ProcessRefusals.InvalidSpawn, "spawn argv is empty"));
        }

        ServiceConfig config;
        lock (_admission)
        {
            // Names are machine-scoped because the agent that cleans up is a continuation —
            // a different task id — so a task-scoped name would be unreachable by the very
            // worker sent to tidy it. Unique among LIVE entries only: an exited process has
            // released its name, so a retry — or a later task reusing the same name — is
            // not blocked by a corpse.
            if (_state.TryGetValue(command.Name, out var existing))
            {
                var live = existing.State is not (ServiceState.Exited or ServiceState.Stopped);
                if (live)
                {
                    return Task.FromResult(ProcessOutcome.Refused(
                        ProcessRefusals.NameTaken,
                        $"the name '{command.Name}' is already held by a running process on this machine"));
                }

                _state.TryRemove(command.Name, out _); // reclaim the exited entry's name
            }

            var running = _state.Count(e =>
                e.Value.State is not (ServiceState.Exited or ServiceState.Stopped));
            if (running >= policy.Max)
            {
                return Task.FromResult(ProcessOutcome.Refused(
                    ProcessRefusals.CapReached,
                    $"this machine already holds {running} agent-started processes " +
                    $"(max {policy.Max})"));
            }

            config = new ServiceConfig(
                command.Name,
                command.Spawn,
                command.WorkingDirectory,
                command.Env ?? new Dictionary<string, string>(StringComparer.Ordinal),
                // Capture on: the log path goes back in the reply so the declaring agent reads
                // its own output with file tools.
                new LogsConfig(Capture: true));

            _state[command.Name] = new SupervisedService(config, command.Session)
            {
                State = ServiceState.Stopped,
                StdinOpen = command.OpenStdin,
            };
        }

        var s = _state[command.Name];
        if (!TryStartAsync(s))
        {
            _state.TryRemove(command.Name, out _);
            Stop(s);
            return Task.FromResult(ProcessOutcome.Refused(
                ProcessRefusals.SpawnFailed, $"'{command.Name}' could not be started"));
        }

        // Watch for exit and record it. No restart: the exit code is the point. This
        // watcher only stamps the exit onto state a heartbeat later reports, so there is
        // nothing for disposal to wait on, and keeping a handle per process ever started
        // would retain one completed task forever.
        _ = Task.Run(() => WatchProcessAsync(command.Name, s, _cts.Token));

        return Task.FromResult(ProcessOutcome.Started(
            _logs is null ? null : Path.Combine(_logs.Root, command.Name)));
    }

    /// <summary>
    /// §10: stop an agent-started process. <b>Machine-scoped</b> — any worker dispatched here
    /// may stop any process here, which is precisely what lets a Lead's cleanup continuation (a
    /// new task id) tidy up what an earlier task started. Cleanup is orchestration, not
    /// enforcement.
    /// </summary>
    public async Task<ProcessOutcome> StopProcessAsync(string name, CancellationToken ct)
    {
        if (!_state.TryGetValue(name, out var s))
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

        _log?.Invoke($"landbridged: process '{name}' stopped on request (exit {exitCode?.ToString() ?? "n/a"})");
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

        if (!_state.TryGetValue(name, out var s))
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
                    $"'{name}' was started without a stdin pipe (the default), so there is nothing to " +
                    "write to — restart it with open_stdin true if you need to talk to it");
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
        _log?.Invoke($"landbridged: process '{name}' exited (code {s.LastExitCode?.ToString() ?? "n/a"}); not restarted");
    }

    /// <summary>Kills every supervised process (§10 clean shutdown).</summary>
    public void KillAll()
    {
        foreach (var s in _state.Values)
            Stop(s);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        KillAll();
        _spawner.Close();
        _cts.Dispose();
    }

    private bool TryStartAsync(SupervisedService s)
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
            // whatever landbridged inherited, which is not a defined thing to give it.
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        for (var i = 1; i < service.Spawn.Count; i++)
            psi.ArgumentList.Add(service.Spawn[i]);

        if (!string.IsNullOrWhiteSpace(service.WorkingDirectory))
            psi.WorkingDirectory = service.WorkingDirectory;

        foreach (var (k, v) in service.Env)
            psi.Environment[k] = v;

        // The tagging that makes restart-equals-reboot cover processes: machine id so
        // the restart sweep reaps the previous generation, and deliberately NO task id
        // so per-task exit cleanup steps over them.
        psi.Environment["LANDBRIDGE_MACHINE_ID"] = _machineId;
        // §10: machine id only, and DELIBERATELY no LANDBRIDGE_SESSION_ID. The task-id
        // tag is what the per-task exit sweep reaps by, so carrying it would kill a
        // process the moment its declaring worker's turn ended: exactly the loss this
        // feature exists to prevent. Bound to the machine generation and nothing
        // smaller; ended only by an explicit stop, its own exit, or this daemon's
        // restart sweep.
        psi.Environment.Remove("LANDBRIDGE_SESSION_ID");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            // PDEATHSIG thread affinity, exactly as for a task spawn: the fork must
            // happen on a thread that outlives the child, or Linux PDEATHSIG — keyed to
            // the forking thread — would kill a healthy process when a pool thread retired.
            _spawner.Run(() =>
            {
                process.Start();
                if (OperatingSystem.IsWindows())
                    s.Job = WindowsJobObject.TryCreateAndAssign(process.Handle, out _);
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ObjectDisposedException)
        {
            _log?.Invoke($"landbridged: process '{s.Config.Name}' failed to start: {e.Message}");
            process.Dispose();
            return false;
        }

        lock (s.Gate)
        {
            s.Process = process;
            s.State = ServiceState.Running;
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

        _log?.Invoke($"landbridged: process '{s.Config.Name}' up (pid {process.Id})");
        return true;
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
    /// Captures this process's stdout/stderr through the same writer a task transcript
    /// uses (§12) — a tee that never blocks and never kills: on reaching the byte cap it
    /// writes a truncation marker and keeps draining, because logging must not be able
    /// to affect the process.
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
            s.State = ServiceState.Stopped;
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

    /// <summary>One supervised process's mutable state, guarded by its own gate.</summary>
    private sealed class SupervisedService(ServiceConfig config, SessionId owner)
    {
        public object Gate { get; } = new();
        public ServiceConfig Config { get; } = config;

        /// <summary>The task that started this process. Provenance, not ownership.</summary>
        public SessionId Owner { get; } = owner;

        /// <summary>
        /// §10: whether this entry has a usable stdin pipe. Whatever the caller asked for;
        /// the default is <b>false</b> — no <c>write_process</c>, and no graceful EOF stop.
        /// </summary>
        public bool StdinOpen { get; init; }
        public Process? Process { get; set; }
        public ServiceState State { get; set; } = ServiceState.Stopped;
        public DateTimeOffset? StartedAt { get; set; }
        public int? LastExitCode { get; set; }
        public DateTimeOffset? LastFailureAt { get; set; }
        internal WindowsJobObject? Job { get; set; }
    }
}
