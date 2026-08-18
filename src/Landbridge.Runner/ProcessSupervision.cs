using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.Runner;

/// <summary>
/// What <c>landbridged</c> <em>did</em> when it handled a <c>stop</c> (§10) — never what the
/// agent did about it. §10 makes the runner transport: it can report the message it sent
/// and the deadline it armed, and it cannot report what the agent chose to do next.
/// </summary>
public enum StopDelivery
{
    /// <summary>
    /// <c>session/cancel</c> was sent on the worker's live ACP connection, and the hard kill
    /// armed at <c>min(ttl, wind_down)</c> behind it (§10, §11).
    ///
    /// <para><b>Sent, and specified to be honoured — but still not observed.</b> Cancel is a
    /// JSON-RPC <em>notification</em>: there is no reply, so nothing on this side sees the
    /// agent act on it. What differs from the stream mode this replaced is that the agent is
    /// now obliged to: the spec has it stop its model requests and tool calls and end the
    /// turn with a <c>cancelled</c> stop reason. The deadline is armed regardless, because
    /// an obligation is not an observation.</para>
    /// </summary>
    CancelSent,

    /// <summary>
    /// No cancel was sent — the session had not opened yet, or the connection was already
    /// gone — and the hard kill armed at the plane's full <c>ttl</c> (§10). The worker gets
    /// the whole TTL the Lead granted to notice its own exit conditions and leave cleanly
    /// first.
    /// </summary>
    DeadlineArmed,

    /// <summary><c>ttl == 0</c>: the process tree was killed outright — nothing sent,
    /// no deadline, no waiting for an ack (§9 check 12).</summary>
    ImmediateKill,

    /// <summary>No such task here, so nothing was done (§10 buffering — commands are
    /// best-effort).</summary>
    NotRunning,
}

/// <summary>
/// The runner's acknowledgement of a <c>stop</c>. <paramref name="Actioned"/> says the
/// runner held the task and acted on the command — <em>not</em> that anything reached the
/// agent; <paramref name="Delivery"/> names which action it took. The two are deliberately
/// named for runner-side facts (see <see cref="StopDelivery"/>).
/// </summary>
public readonly record struct StopAck(bool Actioned, StopDelivery Delivery);

/// <summary>Process supervision, spec §10 runner capabilities.</summary>
public interface IProcessSupervisor
{
    /// <summary>Spawns the harness for a dispatched task and emits <c>started</c>.</summary>
    SessionId Spawn(DispatchCommand dispatch, ProfileConfig profile, string machineId);

    /// <summary>
    /// Graceful stop (§10, §11). <c>session/cancel</c> goes to the worker's live ACP
    /// connection, and it is then given <c>min(ttl, wind_down)</c> to end its turn and exit
    /// before a hard tree-kill backstops it. A task whose session never opened has nothing
    /// to cancel, but the plane's TTL grace still stands: the full <c>ttl</c> to exit on its
    /// own before the kill. <c>ttl == 0</c> kills immediately (§9 check 12).
    /// <para>The returned <see cref="StopAck"/> reports the runner's own action and nothing
    /// more. See <see cref="StopDelivery"/>.</para>
    /// </summary>
    Task<StopAck> StopAsync(SessionId task, TimeSpan ttl, StopDisposition disposition, string? reason, CancellationToken ct);

    /// <summary>
    /// <c>prompt</c> — queue a follow-up turn on a task's live ACP session
    /// (<c>ideas/sessions.md</c> stage 1). False when this machine holds no such session:
    /// the task is not here, or its worker has ended.
    /// </summary>
    bool TryPrompt(SessionId task);

    /// <summary>Immediate group/tree kill of one task (§10).</summary>
    bool Kill(SessionId task);

    /// <summary>Clean shutdown: kill everything the runner started (§10 runner restart).</summary>
    void KillAll();

    int RunningFor(string profile);
    int RunningTotal { get; }
    IReadOnlyCollection<SessionId> RunningSessions { get; }

    /// <summary>Supervised tasks whose process is still alive — the source of the
    /// periodic <c>alive</c> event that carries process-alive to the plane (§10).</summary>
    IReadOnlyCollection<SessionId> LiveSessions { get; }

    /// <summary>The profile a supervised task runs under, or null if not held here (§10).</summary>
    string? ProfileFor(SessionId task);
}

/// <summary>One supervised harness process and the per-task state around it.</summary>
public sealed class SupervisedSession
{
    public required SessionId Session { get; init; }
    public required string Profile { get; init; }
    public required Process Process { get; init; }
    public required string WorkDir { get; init; }
    public required StopConfig Stop { get; init; }

    /// <summary>Argv for <c>hooks.after_exit</c>, captured at spawn so OnExited
    /// does not have to keep the profile. Empty means no hook.</summary>
    public IReadOnlyList<string> AfterExit { get; init; } = [];

    /// <summary><c>profiles[].env</c> as of spawn, so <c>after_exit</c> sees the
    /// same map the worker and <c>before_spawn</c> did.</summary>
    public IReadOnlyDictionary<string, string> ProfileEnv { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// §11 resume: the harness session id, taken from the <c>session/new</c> result by
    /// <see cref="AcpClient"/> — a protocol value, not something fished out of a log line. On
    /// capture the supervisor also emits a <see cref="SessionStartedEvent"/> so the
    /// plane stamps the ref onto the task row (opaque metadata) — a later park
    /// carries it and redispatch resumes the transcript. Also the one-per-task
    /// guard: non-null means the ref has already been emitted.
    /// </summary>
    public string? SessionId { get; set; }

    public bool StopRequested { get; set; }

    /// <summary>
    /// §11 resume (#102): this instance has been replaced by a later dispatch of the
    /// same task on this machine, so it is no longer the task's worker. Set before the
    /// successor is registered, and it is what keeps a predecessor's death from acting
    /// on its successor — see <see cref="ProcessSupervisor.Spawn"/> for why the two
    /// overlap at all, and <c>OnExited</c> for the three things this suppresses.
    /// </summary>
    internal bool Superseded { get; set; }

    internal ITimer? TtlTimer { get; set; }

    /// <summary>
    /// Cancels the background stdout drain — the <see cref="AcpClient"/> read loop — on
    /// teardown. Never null: every worker is an ACP conversation and every conversation is
    /// drained for the process's whole lifetime.
    /// </summary>
    internal CancellationTokenSource? EventReaderCts { get; set; }

    /// <summary>The stdout-drain task itself, for a clean join on shutdown.</summary>
    internal Task? EventReaderTask { get; set; }

    /// <summary>
    /// The live ACP conversation with this worker, held so a stop can be delivered as
    /// <c>session/cancel</c> on the connection that is already open, and so a follow-up turn
    /// can be queued onto it (<c>ideas/sessions.md</c>).
    /// </summary>
    internal AcpClient? Acp { get; set; }

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
    /// landbridged dies by any cause the OS closes this handle and the kernel kills every
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
/// (§10). Every spawn is stamped with <c>LANDBRIDGE_MACHINE_ID</c> and
/// <c>LANDBRIDGE_SESSION_ID</c> (§10, not configurable), started in
/// <c>{work_root}/{session_id}</c> — or in the predecessor's directory when the
/// dispatch names a <see cref="DispatchCommand.WorkDirSession"/>, which every
/// continuation does (§7, §11) — and killed as a whole tree so children —
/// subagents, dev servers — go down with the parent (§10 process groups). The portable tree-kill (<see cref="Process.Kill(bool)"/>)
/// is the group-kill baseline the conventions call for; each task is its own
/// tree, so a kill leaves siblings untouched.
///
/// <para>Every worker <see cref="Process.Start()"/> is marshalled onto one dedicated
/// long-lived OS thread (<see cref="SpawnerThread"/>, PDEATHSIG thread affinity): the Linux harness arms
/// PDEATHSIG, which the kernel keys to the forking <em>thread</em>, so forking from a
/// transient thread-pool thread would spuriously SIGKILL a healthy worker once the
/// pool retired that thread. On Windows each worker is additionally sealed into a
/// kill-on-close Job Object (<see cref="WindowsJobObject"/>, §10 Windows containment) so landbridged's death
/// takes the whole tree down with no discovery needed.</para>
/// </summary>
public sealed class ProcessSupervisor : IProcessSupervisor
{
    private readonly MachineConfig _machine;
    private readonly OutboundEventRing _ring;
    private readonly TimeProvider _clock;
    private readonly StrayReaper? _taskReaper;

    private readonly TranscriptStore? _transcripts;
    private readonly ConcurrentDictionary<SessionId, SupervisedSession> _tasks = new();
    private readonly SpawnerThread _spawner = new();

    /// <summary>Profiles already warned about §10 telemetry with no destination — once each, not once per spawn.</summary>
    private readonly ConcurrentDictionary<string, bool> _telemetryWarnedProfileNames = new(StringComparer.Ordinal);

    private int _supersededExits;

    /// <summary>
    /// Test/observability seam (§11 resume, #102): how many superseded instances have died
    /// here without their exit being reported as the task's. A park-resume that overlaps
    /// its predecessor increments this by one; a machine that never overlaps stays at zero.
    /// </summary>
    internal int SupersededExits => Volatile.Read(ref _supersededExits);

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

    public IReadOnlyCollection<SessionId> RunningSessions => _tasks.Keys.ToArray();

    /// <summary>
    /// The supervised tasks whose OS process is still alive (§10 process-alive). The
    /// source for the periodic <c>alive</c> event: this is the one fact landbridged knows
    /// and the plane cannot observe, so it is the fact that has to travel. Narrower
    /// than <see cref="RunningSessions"/>, which still lists a task between its process
    /// exiting and its bookkeeping being torn down — reporting one of those as alive
    /// would hold off a requeue that should happen.
    /// </summary>
    public IReadOnlyCollection<SessionId> LiveSessions =>
        _tasks.Values.Where(t => t.ProcessAlive).Select(t => t.Session).ToArray();

    public int RunningFor(string profile) =>
        _tasks.Values.Count(t => string.Equals(t.Profile, profile, StringComparison.Ordinal));

    public bool TryGet(SessionId task, out SupervisedSession supervised) => _tasks.TryGetValue(task, out supervised!);

    /// <summary>
    /// The profile name a supervised task is running under, or null if this machine holds no
    /// such task. §10 agent-started processes consult it: the policy for what a task may
    /// start lives on its profile, and only the machine knows which profile that is.
    /// </summary>
    public string? ProfileFor(SessionId task) => _tasks.TryGetValue(task, out var t) ? t.Profile : null;

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

    public SessionId Spawn(DispatchCommand dispatch, ProfileConfig profile, string machineId)
    {
        // §11 resume needs no second argv: the ref travels in the AcpSessionRequest below
        // and becomes `session/load` on the connection this spawn is about to open. One
        // argv, cold start or resume.
        var spawnArgv = profile.Spawn;
        if (spawnArgv.Count == 0)
            throw new InvalidOperationException($"profile '{profile.Name}' has an empty spawn argv");

        // §7/§11: the harness runs in the dispatch's named work dir task when there is one,
        // which is how a continuation works where its predecessor worked. Unconditional —
        // NOT gated on resuming — because directory inheritance is a property of
        // continuation itself: a cold-started continuation still needs the worktree and
        // artifacts the predecessor left, and the workspace is the work. Transcript resume
        // additionally needs it (a harness session is directory-local, so Claude Code
        // resumes only from the directory that created the session), but does not define it.
        var dirTask = dispatch.WorkDirSession ?? dispatch.Session;
        var inheritedDir = dirTask != dispatch.Session;
        var workDir = Path.Combine(_machine.WorkRoot, dirTask.ToString());
        Directory.CreateDirectory(workDir);

        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["session_id"] = dispatch.Session.ToString(),
            ["machine_id"] = machineId,
            ["work_dir"] = workDir,
        };
        // So a files[] body can write Claude's --mcp-config JSON without the
        // plane's BuildWorkerMcpConfig helper. Tokens are lbr_<class>_<64 hex>
        // and are safe to splice into JSON. Empty when the dispatch carries none
        // (the same tests that omit McpConfigJson).
        if (dispatch.WorkerToken.Length > 0)
            substitutions["worker_token"] = dispatch.WorkerToken;
        // §11 resume: the ref is still offered as a {session_id} substitution — files[] and
        // env may legitimately want it — but it no longer selects an argv. What resumes the
        // transcript is `session/load`, on the connection the spawn below opens.
        if (dispatch.ResumeSessionRef is { Length: > 0 } resumeRef)
            substitutions["harness_session_ref"] = resumeRef;
        if (dispatch.SpawnSubstitutions is not null)
            foreach (var (key, value) in dispatch.SpawnSubstitutions)
                substitutions[key] = value;

        // §13 / #112 G11: write Claude's mcp.json only when this argv actually
        // names {mcp_config}. The plane still sends McpConfigJson on every
        // dispatch (it cannot see the profile); a Grok/Codex/OpenCode spawn that
        // never references the token must not leave a live bearer on disk.
        if (dispatch.McpConfigJson is not null && ArgvReferences(spawnArgv, "mcp_config"))
        {
            // In an inherited dir the plain name is already taken by the task that owns the
            // dir, and that task's own worker may still be running (a continuation of a task
            // that has not finished is allowed — §11 forks and chains). Overwriting its
            // config would hand it this task's credential, so a borrowed dir gets a
            // task-scoped name. The task's own dir keeps the documented mcp.json.
            var mcpPath = Path.Combine(
                workDir, inheritedDir ? $"mcp-{dispatch.Session}.json" : "mcp.json");
            File.WriteAllText(mcpPath, dispatch.McpConfigJson);
            SetOwnerOnly(mcpPath);
            substitutions["mcp_config"] = mcpPath;
        }

        WriteProfileFiles(profile, workDir, substitutions);
        RunProfileHook(
            profile.Hooks.BeforeSpawn, substitutions, profile.Env, workDir, machineId, "before_spawn", failClosed: true);

        var argv = spawnArgv.Select(a => Substitute(a, substitutions)).ToArray();

        // §10 event relay: the Terminal events source redirects stdout so the reader
        // can map it to events. §12 capture also needs stdout (to tee the transcript)
        // and additionally stderr. Either reason redirects the stream; whatever is
        // redirected MUST be drained continuously (see below) or a full OS pipe buffer
        // blocks the worker's writes. Hooks/Otel still arrive out-of-band; a
        // non-Terminal, non-capturing profile leaves both streams inherited exactly as
        // before.
        // An ACP profile always drains: stdout carries the agent's half of the JSON-RPC
        // conversation, so it is not merely a source of events but the only way the
        // handshake completes at all.
        // Always: stdout carries the agent's half of the JSON-RPC conversation, so draining
        // it is not merely how events arrive, it is how the handshake completes at all.
        const bool drainStdout = true;
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
            // stdin is redirected for EVERY spawn — including a `stdin: closed` profile,
            // which is redirected precisely so there is a pipe to close (below) rather
            // than landbridged's own inherited stdin, whatever that happens to be.
            //
            // Under the default `deadman` policy the write end is then held open for the
            // child's lifetime (we never close StandardInput). That held pipe IS the
            // dead-man's signal: if landbridged dies — even under SIGKILL — the OS closes the
            // write end and a well-behaved harness sees EOF on stdin and kills its own
            // process tree. This is the cooperative first line of defence the StrayReaper
            // only backstops on restart (§10). Message-mode stop reuses the SAME pipe to
            // inject a stop turn (see StopAsync), so the two uses are compatible.
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

        psi.Environment["LANDBRIDGE_MACHINE_ID"] = machineId;
        psi.Environment["LANDBRIDGE_SESSION_ID"] = dispatch.Session.ToString();
        if (dispatch.WorkerToken.Length > 0)
            psi.Environment["LANDBRIDGE_WORKER_TOKEN"] = dispatch.WorkerToken;
        if (substitutions.TryGetValue("mcp_url", out var mcpUrl) && mcpUrl.Length > 0)
            psi.Environment["LANDBRIDGE_MCP_URL"] = mcpUrl;

        // §1 tracing: hand the worker the current handle span's W3C id so its root
        // span — and its MCP calls back to the plane — continue the one trace.
        // Null when nothing is tracing. The child inherits the rest of landbridged's
        // environment as a copy (ProcessStartInfo does not clear it under
        // UseShellExecute=false), so OTEL_EXPORTER_OTLP_ENDPOINT/…_PROTOCOL flow
        // through unchanged and the worker exports to the same collector.
        if (Activity.Current?.Id is { } traceparent)
            psi.Environment["LANDBRIDGE_TRACEPARENT"] = traceparent;

        // #112 G3: the profile's own env, substituted with the same tokens as spawn.
        // Applied after the reserved LANDBRIDGE_* stamps (which it cannot overwrite) and
        // before telemetry, so telemetry.env still overlays OTEL_* when otel is on.
        ApplyProfileEnvironment(psi, profile, substitutions);

        // §10 telemetry ingest: when this profile opts in, turn the harness's own
        // exporter on and stamp landbridge.session_id onto everything it emits, so the
        // operator's collector can bucket token/cost per task (visibility only —
        // nothing here meters or caps, and the plane ingests none of it). Inheritance
        // above is what carries everything not named in the resolved set.
        ApplyHarnessTelemetry(psi, profile, dispatch.Session.ToString(), machineId);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var supervised = new SupervisedSession
        {
            Session = dispatch.Session,
            Profile = profile.Name,
            Process = process,
            WorkDir = workDir,
            Stop = profile.Stop,
            AfterExit = profile.Hooks.AfterExit,
            ProfileEnv = profile.Env,
        };
        process.Exited += (_, _) => OnExited(supervised, machineId);

        // §11 resume (#102): a dispatch can arrive for a task this machine is STILL
        // running. The plane has finished with the previous instance by then — the
        // transition that redispatches revokes that instance's token (§5) — but revoking
        // a token does not end a process: a headless worker that asked a question ends
        // its turn and exits on its own schedule, and the Lead's answer redispatches
        // within milliseconds of it. So the predecessor is superseded in place, and
        // taken down before the successor starts, which is what §11 already asks for
        // ("resuming a transcript a zombie process still holds interleaves two writers
        // into one session file and corrupts the recovery substrate itself"). The flag
        // is what makes its imminent death harmless: a worker instance is identified
        // here by task id alone, so without it the predecessor's exit reaps its
        // successor's process tree by LANDBRIDGE_SESSION_ID, reports the successor's death to
        // the plane (which requeues the task and revokes the successor's token), and
        // drops the successor out of supervision entirely.
        if (_tasks.TryGetValue(dispatch.Session, out var predecessor))
        {
            predecessor.Superseded = true;
            KillTree(predecessor);
        }
        _tasks[dispatch.Session] = supervised;

        try
        {
            // PDEATHSIG thread affinity: marshal the actual Process.Start onto the one dedicated spawner
            // thread. The psi construction and SupervisedSession bookkeeping stay on the
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
            _tasks.TryRemove(dispatch.Session, out _);
            if (OperatingSystem.IsWindows())
                supervised.Job?.Close();
            process.Dispose();
            throw;
        }

        // §12 capture: open the per-instance writer now that the worker is up (files
        // are created lazily on the first line, so a silent worker leaves none). The
        // stdout tee and the stderr pump feed it; it is flushed and closed once both
        // end (CaptureDone below).
        var writer = captureEnabled ? _transcripts!.CreateWriter(dispatch.Session, profile.Logs.MaxBytes) : null;
        supervised.Transcript = writer;
        var feeders = new List<Task>();

        // §10 Terminal events source: start the stdout drain now that the worker is
        // up. It runs for the process's whole lifetime, mapping NDJSON lines to
        // events — a `tool-call` on the ring is what moves the plane's clocks. Running
        // continuously is the anti-deadlock requirement — a redirected-but-undrained
        // stdout blocks the worker once the pipe buffer fills. EOF (worker exit) or
        // the CTS (teardown) ends it cleanly; OnExited cancels the CTS as a backstop.
        // When §12 capture is also on, the SAME drain tees each verbatim line to the
        // transcript via rawLineSink — one read of stdout serves both.
        {

            // §10 ACP: the same background drain shape as the terminal reader — it runs for
            // the process's whole lifetime and is what keeps the stdout pipe from filling —
            // except it also WRITES, driving initialize → session → prompt on the stdin the
            // deadman policy is already holding open. One task, one conversation.
            var cts = new CancellationTokenSource();
            supervised.EventReaderCts = cts;
            var mcpServers = AcpMcpServers.FromGeneratedConfig(dispatch.McpConfigJson);
            var client = new AcpClient(
                dispatch.Session,
                _ring,
                _clock,
                new AcpSessionRequest(
                    workDir,
                    Substitute(profile.Prompt ?? "", substitutions),
                    Substitute(profile.FollowUpTurn, substitutions),
                    mcpServers,
                    dispatch.ResumeSessionRef,
                    profile.AuthMethod,
                    profile.ConfigOptions,
                    profile.SessionMode),
                onSessionId: sessionId =>
                {
                    // Same one-per-task guard and the same reason as the stream path: the
                    // plane needs the ref on the task row before any park can carry it.
                    if (supervised.SessionId is not null)
                        return;
                    supervised.SessionId = sessionId;
                    _ring.Enqueue(new SessionStartedEvent(dispatch.Session, sessionId, _clock.GetUtcNow()));
                },
                rawLineSink: writer is null ? null : writer.WriteStdoutLine,
                requestPermission: (ask, permissionCt) =>
                    PlanePermissionClient.AskAsync(mcpServers, ask, permissionCt));

            // Held so StopAsync can send session/cancel: an ACP stop is a protocol message
            // on this connection, not a signal and not a line of free text.
            supervised.Acp = client;
            supervised.EventReaderTask = Task
                .Run(
                    () => client.RunAsync(process.StandardOutput, process.StandardInput, cts.Token),
                    CancellationToken.None)
                .ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    cts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (writer is not null)
                feeders.Add(supervised.EventReaderTask);
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
        // is landbridged's own observation; a richer events.source refines it upstream.
        _ring.Enqueue(new StartedEvent(dispatch.Session, _clock.GetUtcNow()));
        return dispatch.Session;
    }

    public async Task<StopAck> StopAsync(
        SessionId task, TimeSpan ttl, StopDisposition disposition, string? reason, CancellationToken ct)
    {
        if (!_tasks.TryGetValue(task, out var supervised))
            return new StopAck(false, StopDelivery.NotRunning);

        supervised.StopRequested = true;

        // §9 check 12 / §11: TTL=0 kills immediately without waiting for ack — no
        // wind-down turn is written, even for a message-mode profile.
        if (ttl <= TimeSpan.Zero)
        {
            KillTree(supervised);
            return new StopAck(true, StopDelivery.ImmediateKill);
        }

        // §10/§11 stop: `session/cancel` on the connection already open, then a deadline.
        //
        // This is the place the protocol change bought the most. The mode it replaced wrote
        // a free-text wind-down turn to the worker's stdin and could only ever ack that the
        // bytes were WRITTEN — no harness this repo supports reads a mid-task turn, so every
        // stream profile ended up declaring `signal` and a stop meant "the TTL, then a
        // tree-kill, and no final report". Cancel is a message the agent is specified to
        // honour: stop the model requests and the tool calls, end the turn `cancelled`.
        //
        // The deadline is armed behind it regardless, and the ack still says only that the
        // cancel was SENT. Cancel is a notification with no reply, so an obligation is the
        // most this side can know about — and an agent that ignores it must still stop.
        if (supervised.Acp is { } acp)
        {
            var sent = await acp.CancelAsync(ct).ConfigureAwait(false);
            var windDown = ttl < supervised.Stop.WindDown ? ttl : supervised.Stop.WindDown;
            supervised.TtlTimer = _clock.CreateTimer(
                _ => KillTree(supervised), null, sent ? windDown : ttl, Timeout.InfiniteTimeSpan);
            return new StopAck(true, sent ? StopDelivery.CancelSent : StopDelivery.DeadlineArmed);
        }

        // No live session to cancel — it never opened, or the connection is already gone.
        // The plane granted a TTL>0 grace on the wire (only ttl=0 is immediate, §9 check 12),
        // so a nearly-done worker still gets the full TTL to finish and exit on its own
        // before the hard kill.
        supervised.TtlTimer = _clock.CreateTimer(_ => KillTree(supervised), null, ttl, Timeout.InfiniteTimeSpan);
        return new StopAck(true, StopDelivery.DeadlineArmed);
    }

    public bool TryPrompt(SessionId task) =>
        _tasks.TryGetValue(task, out var supervised)
        && supervised.Acp is { } acp
        && acp.TryQueueFollowUp();

    public bool Kill(SessionId task)
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

    private void OnExited(SupervisedSession supervised, string machineId)
    {
        // §11 resume (#102): remove only if this instance is still the task's — a
        // compare-and-remove, so a superseded predecessor dying mid-resume cannot
        // evict the successor that replaced it in the map. Losing that entry would
        // leave the successor unsupervised: no `alive` events, no stop delivery, no
        // kill, and a stray at teardown.
        ((ICollection<KeyValuePair<SessionId, SupervisedSession>>)_tasks)
            .Remove(new KeyValuePair<SessionId, SupervisedSession>(supervised.Session, supervised));
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

        // §11 resume (#102): a superseded instance's death is not the task's. The wire
        // names only the task (§10, frozen), so an exit reported for one is read as the
        // death of whatever instance is current — and after a park the current instance
        // is the freshly resumed successor, which the plane would then requeue and whose
        // token it would revoke, leaving a live worker holding a 401'd bearer. Nothing is
        // lost by staying quiet: this instance's own §12 transcript ends where it died,
        // the plane's event log already holds the park and the redispatch that replaced
        // it, and the successor's exit is reported normally when it comes.
        if (supervised.Superseded)
        {
            Interlocked.Increment(ref _supersededExits);
            return;
        }

        _ring.Enqueue(new ExitedEvent(supervised.Session, exitCode, _clock.GetUtcNow()));

        // §10: task-exit stray cleanup keyed by LANDBRIDGE_SESSION_ID, best-effort. Every
        // instance of a task carries the same LANDBRIDGE_SESSION_ID, so this is reachable only
        // for the current one — a superseded predecessor returned above rather than
        // reaping the successor it was replaced by (#102).
        try { _taskReaper?.ReapSession(machineId, supervised.Session.ToString()); }
        catch { /* best effort */ }

        // After reap so a hook that accidentally carried LANDBRIDGE_SESSION_ID is not
        // killed mid-run. Best-effort: a failed after_exit must not rewrite the
        // exit the plane already recorded.
        try
        {
            var afterSubs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["session_id"] = supervised.Session.ToString(),
                ["machine_id"] = machineId,
                ["work_dir"] = supervised.WorkDir,
            };
            RunProfileHook(
                supervised.AfterExit, afterSubs, supervised.ProfileEnv, supervised.WorkDir, machineId, "after_exit", failClosed: false);
        }
        catch { /* best effort */ }
    }

    private static void KillTree(SupervisedSession supervised)
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
    /// logs to stderr (landbridged's log sink), and returns null so the spawn survives.
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
            Console.Error.WriteLine($"landbridged: Job Object containment degraded for a worker: {failure}");
        return job;
    }

    /// <summary>
    /// #112 G3: stamp <see cref="ProfileConfig.Env"/> onto the child. Reserved names
    /// are skipped even if they somehow reached the record — load validation is the
    /// operator-facing refusal; this is the belt.
    /// </summary>
    private static void ApplyProfileEnvironment(
        ProcessStartInfo psi, ProfileConfig profile, IReadOnlyDictionary<string, string> substitutions)
        => ApplyProfileEnvironment(psi, profile.Env, substitutions);

    private static void ApplyProfileEnvironment(
        ProcessStartInfo psi,
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, string> substitutions)
    {
        foreach (var (key, value) in env)
        {
            if (string.IsNullOrWhiteSpace(key) || HarnessTelemetry.IsReserved(key))
                continue;
            psi.Environment[key] = Substitute(value, substitutions);
        }
    }

    internal static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// #112 G2: write <see cref="ProfileConfig.Files"/> into the work dir. Paths
    /// that escape after substitution fail the spawn.
    /// </summary>
    private static void WriteProfileFiles(
        ProfileConfig profile, string workDir, IReadOnlyDictionary<string, string> substitutions)
    {
        foreach (var file in profile.Files)
        {
            var path = Substitute(file.Path, substitutions);
            if (!IsUnderWorkDir(workDir, path))
                throw new InvalidOperationException(
                    $"profile '{profile.Name}' file '{file.Path}' resolves outside the work dir");
            var full = ResolveUnderWorkDir(workDir, path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(full, Substitute(file.Contents, substitutions));
            ApplyDeclaredMode(full, file.Mode);
        }
    }

    internal static bool IsUnderWorkDir(string workDir, string path)
    {
        var root = Path.GetFullPath(workDir);
        var full = ResolveUnderWorkDir(workDir, path);
        if (string.Equals(root, full, StringComparison.Ordinal))
            return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolve a <c>files[]</c> path against the work dir so a relative
    /// <c>.grok/config.toml</c> lands in the clone, not landbridged's cwd.
    /// </summary>
    internal static string ResolveUnderWorkDir(string workDir, string path)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(workDir, path));

    private static void ApplyDeclaredMode(string path, string? mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (mode is null)
            {
                SetOwnerOnly(path);
                return;
            }

            var octal = mode.Trim();
            if (octal.Length == 4 && octal[0] == '0')
                octal = octal[1..];
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(octal, 8));
        }
    }

    /// <summary>
    /// Run a profile hook as argv (never a shell). <paramref name="failClosed"/> is
    /// <c>before_spawn</c>: a non-zero, timeout, or start failure throws so the
    /// harness never starts. <c>after_exit</c> logs and returns.
    /// </summary>
    private static void RunProfileHook(
        IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlyDictionary<string, string> profileEnv,
        string workDir,
        string machineId,
        string hookName,
        bool failClosed)
    {
        if (argv.Count == 0)
            return;

        var resolved = argv.Select(a => Substitute(a, substitutions)).ToArray();
        if (string.IsNullOrWhiteSpace(resolved[0]))
        {
            if (failClosed)
                throw new InvalidOperationException($"profile hook '{hookName}' has an empty argv[0]");
            return;
        }

        var psi = new ProcessStartInfo(resolved[0])
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        for (var i = 1; i < resolved.Length; i++)
            psi.ArgumentList.Add(resolved[i]);

        ApplyProfileEnvironment(psi, profileEnv, substitutions);
        psi.Environment["LANDBRIDGE_MACHINE_ID"] = machineId;
        psi.Environment["LANDBRIDGE_HOOK"] = hookName;
        psi.Environment.Remove("LANDBRIDGE_SESSION_ID");
        psi.Environment.Remove("LANDBRIDGE_WORKER_TOKEN");

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"profile hook '{hookName}' Process.Start returned false");
        }
        catch (Exception e) when (failClosed)
        {
            throw new InvalidOperationException($"profile hook '{hookName}' failed to start: {e.Message}", e);
        }
        catch
        {
            Console.Error.WriteLine($"landbridged: profile hook '{hookName}' failed to start");
            return;
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)HookTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(1)); } catch { /* drain */ }
            var message = $"profile hook '{hookName}' timed out after {HookTimeout.TotalSeconds:0}s";
            if (failClosed)
                throw new InvalidOperationException(message);
            Console.Error.WriteLine("landbridged: " + message);
            return;
        }

        try { Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(1)); } catch { /* drain */ }

        if (process.ExitCode != 0)
        {
            var message = $"profile hook '{hookName}' exited {process.ExitCode}";
            if (failClosed)
                throw new InvalidOperationException(message);
            Console.Error.WriteLine("landbridged: " + message);
        }
    }

    /// <summary>
    /// §10 telemetry ingest: applies the profile's resolved harness-telemetry
    /// variables to the spawn (see <see cref="HarnessTelemetry"/>). A profile that
    /// asks for telemetry with no destination — none configured and none inherited —
    /// gets nothing set and one warning per profile, since a silent no-op is exactly
    /// the failure an operator would spend an afternoon on.
    /// </summary>
    private void ApplyHarnessTelemetry(ProcessStartInfo psi, ProfileConfig profile, string sessionId, string machineId)
    {
        var telemetry = HarnessTelemetry.SpawnEnvironment(
            profile.Telemetry,
            sessionId,
            machineId,
            Environment.GetEnvironmentVariable,
            out var requestedWithoutEndpoint);

        foreach (var (key, value) in telemetry)
            psi.Environment[key] = value;

        if (requestedWithoutEndpoint && _telemetryWarnedProfileNames.TryAdd(profile.Name, true))
            Console.Error.WriteLine(
                $"landbridged: profile '{profile.Name}' requests harness telemetry (telemetry.otel) but no endpoint " +
                $"resolved — set telemetry.endpoint or {HarnessTelemetry.EndpointVar} on landbridged. No telemetry " +
                "variables were set on the worker.");
    }

    internal static bool ArgvReferences(IReadOnlyList<string> argv, string token)
    {
        var needle = "{" + token + "}";
        foreach (var arg in argv)
            if (arg.Contains(needle, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static string Substitute(string arg, IReadOnlyDictionary<string, string> substitutions)
    {
        if (!arg.Contains('{', StringComparison.Ordinal))
            return arg;
        foreach (var (key, value) in substitutions)
            arg = arg.Replace("{" + key + "}", value, StringComparison.Ordinal);
        return arg;
    }

    private static void SetOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

