using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Docket.Contracts;
using Docket.Core;

namespace Docket.Runner;

/// <summary>How a <c>stop</c> was delivered (§10). Reported back so an operator
/// can confirm message-delivery actually reached the agent — the enroll skill's
/// smoke test checks exactly this, and the §11 conformance run would automate it
/// if it were built.</summary>
public enum StopDelivery
{
    /// <summary>Injected as a turn the agent reads and honours (§10). A bounded
    /// wind-down window then backs it with a hard kill on expiry (§11).</summary>
    Message,

    /// <summary>
    /// No wind-down turn could be delivered — the profile has no message seam
    /// (<see cref="StopMode.Signal"/>) or the stdin pipe was already gone — so nothing
    /// was injected. A signal cannot carry the disposition, but the plane's TTL grace
    /// still governs: the worker gets the full TTL to exit on its own before the hard
    /// kill backs it (§10).
    /// </summary>
    Signal,

    /// <summary><c>ttl == 0</c>: killed immediately without waiting for ack (§9 check 12).</summary>
    ImmediateKill,

    /// <summary>No such task here; the command is moot (§10 buffering — commands are best-effort).</summary>
    NotRunning,
}

/// <summary>The runner's acknowledgement of a <c>stop</c>.</summary>
public readonly record struct StopAck(bool Delivered, StopDelivery Delivery);

/// <summary>Process supervision, spec §10 runner capabilities.</summary>
public interface IProcessSupervisor
{
    /// <summary>Spawns the harness for a dispatched task and emits <c>started</c>.</summary>
    TaskId Spawn(DispatchCommand dispatch, ProfileConfig profile, string machineId);

    /// <summary>
    /// Graceful stop (§10, §11). A profile whose harness reads stream-json turns on
    /// the held-open stdin (<see cref="StopMode.Message"/>) is sent a wind-down turn
    /// carrying the disposition, then given <c>min(ttl, wind_down)</c> to persist and
    /// exit on its own before a hard tree-kill backstops it. A profile with no such
    /// seam injects nothing, but the plane's TTL grace still stands: the worker gets
    /// the full <c>ttl</c> to exit on its own before the kill (<c>wind_down</c> is the
    /// message-path budget and does not apply). <c>ttl == 0</c> kills immediately
    /// without injecting anything (§9 check 12).
    /// </summary>
    Task<StopAck> StopAsync(TaskId task, TimeSpan ttl, StopDisposition disposition, string? reason, CancellationToken ct);

    /// <summary>Immediate group/tree kill of one task (§10).</summary>
    bool Kill(TaskId task);

    /// <summary>Clean shutdown: kill everything the runner started (§10 runner restart).</summary>
    void KillAll();

    int RunningFor(string profile);
    int RunningTotal { get; }
    IReadOnlyCollection<TaskId> RunningTasks { get; }

    /// <summary>Supervised tasks whose process is still alive — the source of the
    /// periodic <c>alive</c> event that carries process-alive to the plane (§10).</summary>
    IReadOnlyCollection<TaskId> LiveTasks { get; }

    /// <summary>The profile a supervised task runs under, or null if not held here (§10).</summary>
    string? ProfileFor(TaskId task);
}

/// <summary>One supervised harness process and the per-task state around it.</summary>
public sealed class SupervisedTask
{
    public required TaskId Task { get; init; }
    public required string Profile { get; init; }
    public required Process Process { get; init; }
    public required string WorkDir { get; init; }
    public required StopConfig Stop { get; init; }

    /// <summary>Last per-task liveness signal (started/tool-call), §10.</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>
    /// §11 resume: the harness session id captured from the events stream (claude
    /// <c>system/init</c>), set by the <see cref="TerminalEventReader"/> when
    /// <see cref="EventsSource.Terminal"/> is configured; null otherwise. On
    /// capture the supervisor also emits a <see cref="SessionStartedEvent"/> so the
    /// plane stamps the ref onto the task row (opaque metadata) — a later park
    /// carries it and redispatch resumes the transcript. Also the one-per-task
    /// guard: non-null means the ref has already been emitted.
    /// </summary>
    public string? SessionId { get; set; }

    public bool StopRequested { get; set; }
    internal ITimer? TtlTimer { get; set; }

    /// <summary>
    /// §10 <see cref="EventsSource.Terminal"/>: cancels the background stdout
    /// drain (<see cref="TerminalEventReader"/>) on teardown. Null for every other
    /// events source, where stdout is left unredirected and no drain runs.
    /// </summary>
    internal CancellationTokenSource? EventReaderCts { get; set; }

    /// <summary>The stdout-drain task itself, for a clean join on shutdown.</summary>
    internal Task? EventReaderTask { get; set; }

    /// <summary>
    /// §12 transcript capture: the per-instance writer teeing this worker's stdout and
    /// stderr to <c>&lt;state&gt;/transcripts</c>. Null when capture is off for the
    /// profile or the supervisor has no <see cref="TranscriptStore"/>. Flushed and
    /// closed by <see cref="CaptureDone"/> once both feeding drains end.
    /// </summary>
    internal TranscriptWriter? Transcript { get; set; }

    /// <summary>
    /// §12 capture: cancels the capture drains (the capture-only stdout pump and the
    /// stderr pump) on teardown. Null when capture is off. The Terminal stdout tee
    /// rides the event reader's own CTS, not this one.
    /// </summary>
    internal CancellationTokenSource? CaptureCts { get; set; }

    /// <summary>
    /// §12 capture: completes once every stream feeding <see cref="Transcript"/> has
    /// drained, then flushes and closes the writer and disposes <see cref="CaptureCts"/>.
    /// Null when capture is off.
    /// </summary>
    internal Task? CaptureDone { get; set; }

    /// <summary>
    /// §10 Windows containment: the kill-on-close Job Object this worker's whole process tree is
    /// sealed into, or null off Windows / when assignment degraded (an incompatible
    /// nested outer job). Owned here and closed on the kill/exit cleanup paths. When
    /// docketd dies by any cause the OS closes this handle and the kernel kills every
    /// process in the job — detached grandchildren included — which is the Windows
    /// containment guarantee the <see cref="StrayReaper"/> reconstructs by discovery
    /// on other platforms.
    /// </summary>
    internal WindowsJobObject? Job { get; set; }

    public bool ProcessAlive
    {
        get
        {
            try { return !Process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }
}

/// <summary>
/// Spawns and supervises harness processes, spec §10. <b>No shell, ever</b>:
/// <c>command</c> is argv passed to <see cref="ProcessStartInfo.ArgumentList"/>
/// (§10). Every spawn is stamped with <c>DOCKET_MACHINE_ID</c> and
/// <c>DOCKET_TASK_ID</c> (§10, not configurable), started in
/// <c>{work_root}/{task_id}</c> (§10 work_root note), and killed as a whole
/// tree so children — subagents, dev servers — go down with the parent (§10
/// process groups). The portable tree-kill (<see cref="Process.Kill(bool)"/>)
/// is the group-kill baseline the conventions call for; each task is its own
/// tree, so a kill leaves siblings untouched.
///
/// <para>Every worker <see cref="Process.Start()"/> is marshalled onto one dedicated
/// long-lived OS thread (<see cref="SpawnerThread"/>, PDEATHSIG thread affinity): the Linux harness arms
/// PDEATHSIG, which the kernel keys to the forking <em>thread</em>, so forking from a
/// transient thread-pool thread would spuriously SIGKILL a healthy worker once the
/// pool retired that thread. On Windows each worker is additionally sealed into a
/// kill-on-close Job Object (<see cref="WindowsJobObject"/>, §10 Windows containment) so docketd's death
/// takes the whole tree down with no discovery needed.</para>
/// </summary>
public sealed class ProcessSupervisor : IProcessSupervisor
{
    private readonly MachineConfig _machine;
    private readonly OutboundEventRing _ring;
    private readonly TimeProvider _clock;
    private readonly StrayReaper? _taskReaper;

    private readonly TranscriptStore? _transcripts;
    private readonly ConcurrentDictionary<TaskId, SupervisedTask> _tasks = new();
    private readonly SpawnerThread _spawner = new();

    /// <summary>Profiles already warned about §10 telemetry with no destination — once each, not once per spawn.</summary>
    private readonly ConcurrentDictionary<string, bool> _telemetryWarnedProfileNames = new(StringComparer.Ordinal);

    /// <param name="transcripts">
    /// §12 machine-local transcript capture. When supplied, a profile with
    /// <see cref="LogsConfig.Capture"/> set tees its worker's stdout/stderr here. Null
    /// (the default, and what most unit tests pass) disables capture regardless of the
    /// profile flag — Program always supplies one derived from the state dir.
    /// </param>
    public ProcessSupervisor(
        MachineConfig machine,
        OutboundEventRing ring,
        TimeProvider clock,
        StrayReaper? taskReaper = null,
        TranscriptStore? transcripts = null)
    {
        _machine = machine;
        _ring = ring;
        _clock = clock;
        _taskReaper = taskReaper;
        _transcripts = transcripts;
    }

    public int RunningTotal => _tasks.Count;

    public IReadOnlyCollection<TaskId> RunningTasks => _tasks.Keys.ToArray();

    /// <summary>
    /// The supervised tasks whose OS process is still alive (§10 process-alive). The
    /// source for the periodic <c>alive</c> event: this is the one fact docketd knows
    /// and the plane cannot observe, so it is the fact that has to travel. Narrower
    /// than <see cref="RunningTasks"/>, which still lists a task between its process
    /// exiting and its bookkeeping being torn down — reporting one of those as alive
    /// would hold off a requeue that should happen.
    /// </summary>
    public IReadOnlyCollection<TaskId> LiveTasks =>
        _tasks.Values.Where(t => t.ProcessAlive).Select(t => t.Task).ToArray();

    public int RunningFor(string profile) =>
        _tasks.Values.Count(t => string.Equals(t.Profile, profile, StringComparison.Ordinal));

    public bool TryGet(TaskId task, out SupervisedTask supervised) => _tasks.TryGetValue(task, out supervised!);

    /// <summary>
    /// The profile name a supervised task is running under, or null if this machine holds no
    /// such task. §10 agent-started processes consult it: the policy for what a task may
    /// start lives on its profile, and only the machine knows which profile that is.
    /// </summary>
    public string? ProfileFor(TaskId task) => _tasks.TryGetValue(task, out var t) ? t.Profile : null;

    /// <summary>Identity of the thread that executed a spawn's <c>Process.Start</c>.</summary>
    internal readonly record struct SpawnThreadObservation(int ManagedThreadId, bool IsThreadPoolThread);

    /// <summary>
    /// Test seam (PDEATHSIG thread affinity): captured inside the marshalled spawn delegate on the last
    /// spawn, so a test can prove the fork ran on the dedicated, non-thread-pool
    /// spawner thread rather than inline on the caller. Null until the first spawn.
    /// </summary>
    internal SpawnThreadObservation? LastSpawnThreadObservation { get; private set; }

    /// <summary>Test seam (PDEATHSIG thread affinity): managed id of the one dedicated spawner thread.</summary>
    internal int? SpawnerManagedThreadId => _spawner.ManagedThreadId;

    /// <summary>
    /// Test/observability seam (§10 Windows containment): the reason the last Windows Job Object
    /// assignment degraded (e.g. an incompatible nested outer job), or null if the
    /// most recent Windows spawn was contained successfully.
    /// </summary>
    internal string? LastJobAssignmentFailure { get; private set; }

    public TaskId Spawn(DispatchCommand dispatch, ProfileConfig profile, string machineId)
    {
        // §11 resume: continue a parked transcript when the plane hands back a
        // harness session ref for a task that was worked before AND this profile
        // declares how to resume (resume.args). A ref with no resume config, or no
        // ref at all, falls back to a normal spawn — a documented cold start (§11).
        var resuming = dispatch.ResumeSessionRef is { Length: > 0 } && profile.Resume is not null;
        var spawnArgv = resuming ? profile.Resume!.Args : profile.Spawn;
        if (spawnArgv.Count == 0)
            throw new InvalidOperationException(
                $"profile '{profile.Name}' has an empty {(resuming ? "resume" : "spawn")} argv");

        var workDir = Path.Combine(_machine.WorkRoot, dispatch.Task.ToString());
        Directory.CreateDirectory(workDir);

        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["task_id"] = dispatch.Task.ToString(),
            ["machine_id"] = machineId,
            ["work_dir"] = workDir,
            ["budget"] = dispatch.BudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "",
        };
        // §11 resume: the opaque session ref fills the {session_id} placeholder in
        // resume.args (e.g. `--resume {session_id}`). Only present when resuming;
        // a cold-start argv carries no {session_id}.
        if (resuming)
            substitutions["session_id"] = dispatch.ResumeSessionRef!;
        if (dispatch.SpawnSubstitutions is not null)
            foreach (var (key, value) in dispatch.SpawnSubstitutions)
                substitutions[key] = value;

        // §13: the worker token's generated MCP config lives in the task dir, 0600.
        // A resumed claude still needs its MCP config (and a fresh worker token in
        // the env below), so {mcp_config} substitutes on the resume path too.
        if (dispatch.McpConfigJson is not null)
        {
            var mcpPath = Path.Combine(workDir, "mcp.json");
            File.WriteAllText(mcpPath, dispatch.McpConfigJson);
            SetOwnerOnly(mcpPath);
            substitutions["mcp_config"] = mcpPath;
        }

        var argv = spawnArgv.Select(a => Substitute(a, substitutions)).ToArray();

        // §10 event relay: the Terminal events source redirects stdout so the reader
        // can map it to events. §12 capture also needs stdout (to tee the transcript)
        // and additionally stderr. Either reason redirects the stream; whatever is
        // redirected MUST be drained continuously (see below) or a full OS pipe buffer
        // blocks the worker's writes. Hooks/Otel still arrive out-of-band; a
        // non-Terminal, non-capturing profile leaves both streams inherited exactly as
        // before.
        var drainStdout = profile.Events.Source == EventsSource.Terminal;
        var captureEnabled = profile.Logs.Capture && _transcripts is not null;
        var redirectStdout = drainStdout || captureEnabled;
        var redirectStderr = captureEnabled;

        // §12 hygiene: a cheap opportunistic sweep on the natural cadence (a spawn is
        // when new transcript disk is about to be consumed), so a long-lived machine
        // that never restarts still prunes. Best-effort; never blocks the spawn.
        if (captureEnabled)
            try { _transcripts!.Prune(); } catch { /* best effort */ }

        var psi = new ProcessStartInfo(argv[0])
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            // stdin is redirected for EVERY spawn and its write end is held open for
            // the child's lifetime (we never close StandardInput here). That held
            // pipe IS the dead-man's signal: if docketd dies — even under SIGKILL —
            // the OS closes the write end and a well-behaved harness sees EOF on
            // stdin and kills its own process tree. This is the cooperative first
            // line of defence the StrayReaper only backstops on restart (§10).
            // Message-mode stop reuses the SAME pipe to inject a stop turn (see
            // StopAsync), so the two uses are compatible.
            RedirectStandardInput = true,

            // Redirect stdout when the Terminal reader needs it and/or §12 capture is
            // teeing the transcript; redirect stderr only for capture. Anything
            // redirected is drained for the process's whole lifetime (below) so a full
            // OS pipe buffer never blocks the worker.
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = redirectStderr,
        };
        for (var i = 1; i < argv.Length; i++)
            psi.ArgumentList.Add(argv[i]);

        psi.Environment["DOCKET_MACHINE_ID"] = machineId;
        psi.Environment["DOCKET_TASK_ID"] = dispatch.Task.ToString();
        if (dispatch.WorkerToken.Length > 0)
            psi.Environment["DOCKET_WORKER_TOKEN"] = dispatch.WorkerToken;

        // §1 tracing: hand the worker the current handle span's W3C id so its root
        // span — and its MCP calls back to the plane — continue the one trace.
        // Null when nothing is tracing. The child inherits the rest of docketd's
        // environment as a copy (ProcessStartInfo does not clear it under
        // UseShellExecute=false), so OTEL_EXPORTER_OTLP_ENDPOINT/…_PROTOCOL flow
        // through unchanged and the worker exports to the same collector.
        if (Activity.Current?.Id is { } traceparent)
            psi.Environment["DOCKET_TRACEPARENT"] = traceparent;

        // §10 telemetry ingest: when this profile opts in, turn the harness's own
        // exporter on and stamp docket.task_id onto everything it emits, so the
        // operator's collector can bucket token/cost per task (visibility only —
        // nothing here meters or caps, and the plane ingests none of it). Inheritance
        // above is what carries everything not named in the resolved set.
        ApplyHarnessTelemetry(psi, profile, dispatch.Task.ToString(), machineId);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var supervised = new SupervisedTask
        {
            Task = dispatch.Task,
            Profile = profile.Name,
            Process = process,
            WorkDir = workDir,
            Stop = profile.Stop,
            LastActivityAt = _clock.GetUtcNow(),
        };
        process.Exited += (_, _) => OnExited(supervised, machineId);
        _tasks[dispatch.Task] = supervised;

        try
        {
            // PDEATHSIG thread affinity: marshal the actual Process.Start onto the one dedicated spawner
            // thread. The psi construction and SupervisedTask bookkeeping stay on the
            // caller; only the fork must run on a thread that outlives the worker, so
            // Linux PDEATHSIG — keyed to the forking thread — is never tripped by a
            // retiring thread-pool thread. Run blocks and re-throws in the caller's
            // context, so the failure handling below is unchanged.
            _spawner.Run(() =>
            {
                // Test seam (PDEATHSIG thread affinity): captured on the thread that actually forks, so a
                // test can prove the Start ran on the dedicated non-pool thread.
                LastSpawnThreadObservation = new SpawnThreadObservation(
                    Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread);

                if (!process.Start())
                    throw new InvalidOperationException("Process.Start returned false");

                // §10 Windows containment: seal the worker into a kill-on-close Job Object right
                // after Start (the tiny, standard create→assign race is accepted).
                if (OperatingSystem.IsWindows())
                    supervised.Job = CreateJobForWorker(process);
            });
        }
        catch
        {
            _tasks.TryRemove(dispatch.Task, out _);
            if (OperatingSystem.IsWindows())
                supervised.Job?.Close();
            process.Dispose();
            throw;
        }

        // §12 capture: open the per-instance writer now that the worker is up (files
        // are created lazily on the first line, so a silent worker leaves none). The
        // stdout tee and the stderr pump feed it; it is flushed and closed once both
        // end (CaptureDone below).
        var writer = captureEnabled ? _transcripts!.CreateWriter(dispatch.Task, profile.Logs.MaxBytes) : null;
        supervised.Transcript = writer;
        var feeders = new List<Task>();

        // §10 Terminal events source: start the stdout drain now that the worker is
        // up. It runs for the process's whole lifetime, mapping NDJSON lines to
        // events and bumping this task's liveness on every well-formed line. Running
        // continuously is the anti-deadlock requirement — a redirected-but-undrained
        // stdout blocks the worker once the pipe buffer fills. EOF (worker exit) or
        // the CTS (teardown) ends it cleanly; OnExited cancels the CTS as a backstop.
        // When §12 capture is also on, the SAME drain tees each verbatim line to the
        // transcript via rawLineSink — one read of stdout serves both.
        if (drainStdout)
        {
            var cts = new CancellationTokenSource();
            supervised.EventReaderCts = cts;
            var reader = new TerminalEventReader(
                dispatch.Task,
                _ring,
                RecordActivity,
                profile.Events.Mapping,
                _clock,
                onSessionId: sessionId =>
                {
                    // §11 resume: emit the ref to the plane the moment it is captured,
                    // so the task row carries it before any park. Once per task — the
                    // reader only fires this on the first system/init, and this guard
                    // makes a re-emit impossible even if that ever changed.
                    if (supervised.SessionId is not null)
                        return;
                    supervised.SessionId = sessionId;
                    _ring.Enqueue(new SessionStartedEvent(dispatch.Task, sessionId, _clock.GetUtcNow()));
                },
                rawLineSink: writer is null ? null : writer.WriteStdoutLine);
            // The CTS is disposed by the reader task's own completion, so OnExited's
            // backstop cancel never races a live-then-freed handle.
            supervised.EventReaderTask = Task
                .Run(() => reader.ReadToEndAsync(process.StandardOutput, cts.Token), CancellationToken.None)
                .ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    cts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (writer is not null)
                feeders.Add(supervised.EventReaderTask);
        }
        else if (captureEnabled)
        {
            // §12 capture without event mapping (a non-Terminal profile): drain stdout
            // straight to the transcript. Same anti-deadlock requirement — this pump IS
            // the drain that keeps the redirected pipe from filling.
            var cts = supervised.CaptureCts ??= new CancellationTokenSource();
            feeders.Add(Task.Run(
                () => TranscriptCapture.PumpLinesAsync(process.StandardOutput, writer!.WriteStdoutLine, cts.Token),
                CancellationToken.None));
        }

        // §12 capture: stderr is only ever redirected for capture, and when it is it
        // MUST be drained too. It is captured, never mapped to events.
        if (captureEnabled)
        {
            var cts = supervised.CaptureCts ??= new CancellationTokenSource();
            feeders.Add(Task.Run(
                () => TranscriptCapture.PumpLinesAsync(process.StandardError, writer!.WriteStderrLine, cts.Token),
                CancellationToken.None));
        }

        // §12 capture: flush and close the writer once every feeding drain has ended
        // (EOF or teardown), then dispose the capture CTS. Runs off the drains'
        // completion, so no writes race the close.
        if (writer is not null)
        {
            var captureCts = supervised.CaptureCts;
            supervised.CaptureDone = Task.WhenAll(feeders).ContinueWith(
                _ =>
                {
                    writer.Dispose();
                    captureCts?.Dispose();
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        // §10: started (harness up) is distinct from dispatch ack. Process-spawned
        // is docketd's own observation; a richer events.source refines it upstream.
        _ring.Enqueue(new StartedEvent(dispatch.Task, _clock.GetUtcNow()));
        return dispatch.Task;
    }

    /// <summary>Records a per-task liveness signal from the events source (§10).</summary>
    public void RecordActivity(TaskId task)
    {
        if (_tasks.TryGetValue(task, out var supervised))
            supervised.LastActivityAt = _clock.GetUtcNow();
    }

    /// <summary>
    /// The runner-local view of per-task liveness (§10): process alive <em>and</em> a
    /// local signal within <paramref name="timeout"/>. Diagnostic only — the control
    /// plane does <b>not</b> consult this, and cannot: it is a runner-side read with
    /// no wire representation. Per-task liveness on the plane is decided from the
    /// events it receives — <see cref="LiveTasks"/> feeds the periodic <c>alive</c>
    /// event that carries the process-alive half upstream. Do not mistake this for
    /// the mechanism §10 promises; conflating the two is what left process-alive
    /// unwired while appearing implemented.
    /// </summary>
    public bool IsTaskLive(TaskId task, TimeSpan timeout)
    {
        if (!_tasks.TryGetValue(task, out var supervised) || !supervised.ProcessAlive)
            return false;
        return _clock.GetUtcNow() - supervised.LastActivityAt <= timeout;
    }

    public async Task<StopAck> StopAsync(
        TaskId task, TimeSpan ttl, StopDisposition disposition, string? reason, CancellationToken ct)
    {
        if (!_tasks.TryGetValue(task, out var supervised))
            return new StopAck(false, StopDelivery.NotRunning);

        supervised.StopRequested = true;

        // §9 check 12 / §11: TTL=0 kills immediately without waiting for ack — no
        // wind-down turn is injected, even for a message-mode profile.
        if (ttl <= TimeSpan.Zero)
        {
            KillTree(supervised);
            return new StopAck(true, StopDelivery.ImmediateKill);
        }

        // §10, §11 graceful wind-down: only a profile whose harness reads stream-json
        // turns on the held-open stdin (StopMode.Message + the redirected-stdin seam,
        // config-driven, never harness-specific in code) can be told to wind down. On
        // a successful inject, give the agent a bounded window — min(ttl, wind_down) —
        // to persist and exit on its own before the hard kill backstops it. A
        // voluntary exit before then disposes the timer in OnExited, so the kill never
        // fires.
        if (supervised.Stop.Mode == StopMode.Message && supervised.Process.StartInfo.RedirectStandardInput)
        {
            var message = BuildStopMessage(supervised.Stop, disposition, ttl, reason);
            try
            {
                await supervised.Process.StandardInput.WriteLineAsync(message.AsMemory(), ct);
                await supervised.Process.StandardInput.FlushAsync(ct);

                var windDown = ttl < supervised.Stop.WindDown ? ttl : supervised.Stop.WindDown;
                // FakeTimeProvider drives this deterministically in tests.
                supervised.TtlTimer = _clock.CreateTimer(
                    _ => KillTree(supervised), null, windDown, Timeout.InfiniteTimeSpan);
                return new StopAck(true, StopDelivery.Message);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The stdin pipe is already gone (the worker exited or closed it), so
                // the wind-down turn cannot be delivered. Fall through to the TTL kill.
            }
        }

        // §10, §11: no message seam to honour (a signal-mode profile, or the inject
        // just failed). Nothing is injected, but the plane granted a TTL>0 grace on
        // the wire — an explicit window the Lead chose (only ttl=0 is immediate, §9
        // check 12) — so a nearly-done worker still gets the full TTL to finish and
        // exit on its own before the hard kill. wind_down is the message-path budget
        // and does not apply here; the plane's ttl alone governs this grace. Signals
        // are reserved for exactly this: TTL expiry and kill.
        supervised.TtlTimer = _clock.CreateTimer(_ => KillTree(supervised), null, ttl, Timeout.InfiniteTimeSpan);
        return new StopAck(true, StopDelivery.Signal);
    }

    public bool Kill(TaskId task)
    {
        if (!_tasks.TryGetValue(task, out var supervised))
            return false;
        KillTree(supervised);
        return true;
    }

    public void KillAll()
    {
        foreach (var supervised in _tasks.Values)
            KillTree(supervised);
    }

    private void OnExited(SupervisedTask supervised, string machineId)
    {
        _tasks.TryRemove(supervised.Task, out _);
        supervised.TtlTimer?.Dispose();

        // §10 Terminal events source: the worker is gone, so its stdout is at (or
        // draining toward) EOF and the reader is unwinding on its own. Arm a short
        // grace cancel rather than cancelling now: an immediate cancel would make a
        // ReadLineAsync with buffered lines still waiting throw instead of returning
        // them, truncating the worker's final events. The grace also backstops the
        // rare case where a detached grandchild inherited stdout and holds the pipe
        // open past the worker's exit. Disposal is owned by the reader task's
        // continuation, so a double-dispose here is impossible. No-op for every
        // other source (EventReaderCts is null there).
        if (supervised.EventReaderCts is { } readerCts)
        {
            try { readerCts.CancelAfter(TimeSpan.FromSeconds(5)); }
            catch (ObjectDisposedException) { /* reader already finished and freed it */ }
        }

        // §12 capture: same short grace for the capture drains (capture-only stdout
        // and stderr). EOF ends them on the worker's exit; the grace backstops a
        // detached grandchild holding a pipe open, and lets buffered final lines land
        // rather than throwing them away. CaptureDone flushes and closes the writer and
        // disposes this CTS once the drains end, so a double-dispose is impossible.
        if (supervised.CaptureCts is { } captureCts)
        {
            try { captureCts.CancelAfter(TimeSpan.FromSeconds(5)); }
            catch (ObjectDisposedException) { /* drains already finished and freed it */ }
        }

        // §10 Windows containment: close the job handle now the process is gone. Kill-on-close
        // sweeps up any grandchild that outlived the parent. Idempotent with an
        // explicit kill's TerminateAndClose.
        if (OperatingSystem.IsWindows())
            supervised.Job?.Close();

        int exitCode;
        try { exitCode = supervised.Process.ExitCode; }
        catch (InvalidOperationException) { exitCode = -1; }

        _ring.Enqueue(new ExitedEvent(supervised.Task, exitCode, _clock.GetUtcNow()));

        // §10: task-exit stray cleanup keyed by DOCKET_TASK_ID, best-effort.
        try { _taskReaper?.ReapTask(machineId, supervised.Task.ToString()); }
        catch { /* best effort */ }
    }

    private static void KillTree(SupervisedTask supervised)
    {
        supervised.TtlTimer?.Dispose();
        try
        {
            if (!supervised.Process.HasExited)
                supervised.Process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited, or the OS refused — either way the task is gone.
        }

        // §10 Windows containment: on top of the portable tree-kill, terminate the job so any
        // process that escaped the managed tree walk (detached/reparented) is swept
        // up and the exit code is deterministic. No-op off Windows (Job is only ever
        // set there). OnExited's Close is idempotent with this.
        if (OperatingSystem.IsWindows())
            supervised.Job?.TerminateAndClose();
    }

    /// <summary>
    /// §10 Windows containment: creates a kill-on-close Job Object and assigns the freshly-started worker
    /// to it. Never throws — on creation/assignment failure it records the reason,
    /// logs to stderr (docketd's log sink), and returns null so the spawn survives.
    /// CI runners wrap processes in their own Job Objects; Windows 8+ nests jobs, but
    /// an incompatible outer job can still refuse the assignment, and the held-stdin
    /// dead-man pipe plus the portable tree-kill still cover those cases. The job is
    /// the Windows <em>extra</em>, never the only guarantee.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private WindowsJobObject? CreateJobForWorker(Process process)
    {
        var job = WindowsJobObject.TryCreateAndAssign(process.Handle, out var failure);
        LastJobAssignmentFailure = failure;
        if (job is null)
            Console.Error.WriteLine($"docketd: Job Object containment degraded for a worker: {failure}");
        return job;
    }

    /// <summary>
    /// §10 telemetry ingest: applies the profile's resolved harness-telemetry
    /// variables to the spawn (see <see cref="HarnessTelemetry"/>). A profile that
    /// asks for telemetry with no destination — none configured and none inherited —
    /// gets nothing set and one warning per profile, since a silent no-op is exactly
    /// the failure an operator would spend an afternoon on.
    /// </summary>
    private void ApplyHarnessTelemetry(ProcessStartInfo psi, ProfileConfig profile, string taskId, string machineId)
    {
        var telemetry = HarnessTelemetry.SpawnEnvironment(
            profile.Telemetry,
            taskId,
            machineId,
            Environment.GetEnvironmentVariable,
            out var requestedWithoutEndpoint);

        foreach (var (key, value) in telemetry)
            psi.Environment[key] = value;

        if (requestedWithoutEndpoint && _telemetryWarnedProfileNames.TryAdd(profile.Name, true))
            Console.Error.WriteLine(
                $"docketd: profile '{profile.Name}' requests harness telemetry (telemetry.otel) but no endpoint " +
                $"resolved — set telemetry.endpoint or {HarnessTelemetry.EndpointVar} on docketd. No telemetry " +
                "variables were set on the worker.");
    }

    private static string Substitute(string arg, IReadOnlyDictionary<string, string> substitutions)
    {
        if (!arg.Contains('{', StringComparison.Ordinal))
            return arg;
        foreach (var (key, value) in substitutions)
            arg = arg.Replace("{" + key + "}", value, StringComparison.Ordinal);
        return arg;
    }

    private static string BuildStopMessage(StopConfig stop, StopDisposition disposition, TimeSpan ttl, string? reason)
    {
        var dispositionName = disposition switch
        {
            StopDisposition.Preserve => "preserve",
            StopDisposition.Discard => "discard",
            StopDisposition.PreserveAndPark => "preserve_and_park",
            _ => "preserve",
        };
        if (!string.IsNullOrEmpty(stop.MessageTemplate))
        {
            return stop.MessageTemplate
                .Replace("{disposition}", dispositionName, StringComparison.Ordinal)
                .Replace("{ttl_seconds}", ((int)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{reason}", reason ?? "", StringComparison.Ordinal);
        }

        // Default: a compact JSON stop turn. The frozen vocabulary names the
        // command; the harness config names the exact transport shape (§10).
        return System.Text.Json.JsonSerializer.Serialize(
            new StopMessage("stop", dispositionName, (int)ttl.TotalSeconds, reason),
            RunnerStopMessageContext.Default.StopMessage);
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>The default injected stop-turn payload (§10, §11).</summary>
internal sealed record StopMessage(string Type, string Disposition, int TtlSeconds, string? Reason);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower)]
[System.Text.Json.Serialization.JsonSerializable(typeof(StopMessage))]
internal sealed partial class RunnerStopMessageContext : System.Text.Json.Serialization.JsonSerializerContext;
