using System.Collections.Concurrent;
using System.Text;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// A standing multi-machine fleet for the collaboration scenarios (spec §8.3): one
/// real control plane + relay, and N real docketd rigs on distinct machine ids, each
/// spawning <c>Docket.CollabHarness</c>. It generalizes the increment-4 live-fleet
/// crown from a fixed producer/consumer pair to an arbitrary set of enrolled machines.
///
/// <para>Each machine is two seams the §10 socket would occupy: its registry send
/// delegate routes <see cref="DispatchCommand"/> → that machine's real
/// <see cref="ProcessSupervisor"/> spawn (running the scripted collaborator with the
/// injected <c>--mcp-config</c>, §13) and <see cref="OpenForwardCommand"/> → that
/// machine's real <see cref="RunnerDaemon"/>, whose forwarder splices bytes to the
/// relay and drains forward-opened/-closed back into the plane's
/// <see cref="RunnerEventSink"/>.</para>
///
/// <para>Dispatch is steered deterministically: a task is created, then a single
/// <see cref="DispatchService.RunDispatchPassAsync"/> is run with <em>only</em> the
/// chosen machine marked ready — so the SKIP-LOCKED claim can only land the one
/// submitted task on that one machine. No background loop, no timers, no
/// sleeps-as-sync: every wait is a bounded poll of committed control-plane state.</para>
///
/// <para><paramref name="spawnArgv"/> overrides what each machine's <c>default</c>
/// profile spawns. Left null (the scripted tier's use) it spawns the no-LLM
/// <c>Docket.CollabHarness</c>; the key-gated real-<c>claude -p</c> tier passes the
/// validated claude recipe instead (§10 config-only harness seam), so the very same
/// fleet drives a real agent with no code change on any surface below this line.</para>
///
/// <para>The remaining parameters shape the rest of that one profile:
/// <paramref name="prompt"/> is the worker's opening turn (an ACP agent takes none on
/// argv), <paramref name="followUp"/> the turn that wakes a live session when there is new
/// input on the assignment, <paramref name="stop"/> the wind-down budget behind
/// <c>session/cancel</c>, and <paramref name="agentProcesses"/> the per-profile gate the
/// machine applies to <c>start_process</c>. All default to the scripted tier's values.</para>
///
/// <para>Three parameters this rig used to need are gone with the stream protocol:
/// <c>resumeArgv</c> (resume is <c>session/load</c> on the connection the spawn opens),
/// <c>terminalEvents</c> plus <c>eventMapping</c> (the shapes are in the spec), and
/// <c>stdin</c> (stdin is the request channel, so there is no policy to choose).</para>
/// </summary>
internal sealed class FleetRig(
    PostgresFixture pg,
    IReadOnlyList<string>? spawnArgv = null,
    bool agentProcesses = false,
    StopConfig? stop = null,
    IReadOnlyList<ProfileFile>? files = null,
    IReadOnlyDictionary<string, string>? env = null,
    string? prompt = null,
    string? followUp = null) : IAsyncDisposable
{
    private const string RelayBearer = "multimachine-relay-shared-secret-under-test";

    private WebApplication _plane = null!;
    private WebApplication _relay = null!;
    private RunnerConnectionRegistry _registry = null!;
    private RunnerEventSink _sink = null!;
    private DispatchService _dispatch = null!;
    private ProfileConfig _profile = null!;
    private string _baseUrl = null!;

    private readonly Dictionary<string, MachineRig> _machines = new(StringComparer.Ordinal);

    /// <summary>Whether to drain each machine's worker-supervisor event ring into the
    /// plane's sink. Off for the scripted tier (its serve workers never exit mid-test
    /// and its consume workers report before exiting, so exits carry no signal there),
    /// ON for the real-harness tiers — where a worker that ends its turn WITHOUT
    /// reporting must requeue the still-<c>working</c> task so a retry can redispatch it,
    /// exactly as production docketd's socket loop does (§10). Gated on the spawn
    /// override so the scripted suite's behaviour is byte-for-byte unchanged.</summary>
    private bool RealWorkerMode => spawnArgv is not null;

    private readonly CancellationTokenSource _pumpCts = new();
    private readonly List<Task> _pumps = new();

    /// <summary>The machine each task was last spawned on — a sticky historical fact,
    /// unlike <see cref="MachineOf"/> which reflects live registry tracking and is cleared
    /// when a worker exits (requeue-on-exit untracks). The real tier asserts against this
    /// so "which machine ran it" survives the worker's own exit.</summary>
    private readonly ConcurrentDictionary<TaskId, string> _ranOn = new();

    /// <summary>Per-task spawn/exit tallies the ring pump captures (real-worker mode), so
    /// a timeout diagnostic can say whether the worker spawned, how many times, and with
    /// what last exit code — the difference between "never dispatched" and "spawned but
    /// never reported".</summary>
    private readonly ConcurrentDictionary<TaskId, WorkerObservation> _observations = new();

    /// <summary>How each task's last <c>stop</c> was delivered (§10) — message-injected or
    /// signal/kill. The distinction is the whole content of a graceful-stop assertion.</summary>
    private readonly ConcurrentDictionary<TaskId, StopAck> _stopAcks = new();

    /// <summary>Each machine's own process-supervision log. The <em>reason</em> a spawn failed
    /// lives only here — the agent is told a bare <c>spawn_failed</c> — so a process scenario's
    /// diagnostic is close to useless without it.</summary>
    private readonly ConcurrentQueue<string> _machineLog = new();

    public TeamId Team { get; private set; }
    private string _leadToken = null!;

    public async Task StartAsync(CancellationToken ct)
    {
        // Pre-reserve the relay URL so the plane can be configured and the relay bound
        // to the same address without a build-order race (§8.3).
        var relayUrl = PlaneProbe.ReserveLoopbackUrl();
        _plane = MultiMachineKit.BuildPlane(pg.ConnectionString, RelayBearer, relayUrl);
        await _plane.StartAsync(ct);
        _baseUrl = MultiMachineKit.HttpBaseUrl(_plane);
        _relay = MultiMachineKit.BuildRelay(MultiMachineKit.BaseUri(_plane).ToString(), RelayBearer, relayUrl);
        await _relay.StartAsync(ct);

        _registry = _plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        _sink = _plane.Services.GetRequiredService<RunnerEventSink>();
        _dispatch = new DispatchService(
            _plane.Services.GetRequiredService<IServiceScopeFactory>(),
            _registry, TimeProvider.System, NullLogger<DispatchService>.Instance,
            publicMcpUrl: _baseUrl);

        _profile = new ProfileConfig(
            "default",
            spawnArgv ?? [CollabHarnessPath(), "--acp"],
            stop ?? new StopConfig(WindDown: TimeSpan.FromSeconds(30)),
            new TelemetryConfig(Otel: false, Endpoint: null),
            // §12: capture on, so the fleet exercises the real capture → serve path end to
            // end. Pruning disabled (0) — a rig lives seconds and a sweep would only add
            // nondeterminism.
            new LogsConfig(Capture: true, PruneAfterDays: 0),
            MaxConcurrent: null,
            // §10: the machine owner's decision, enforced machine-side. Off unless a
            // scenario is about agent-started processes.
            Processes: agentProcesses ? new ProfileProcessesConfig(AgentInitiated: true) : null,
            Env: env,
            Files: files,
            Prompt: prompt ?? "Do the task you have been assigned.",
            FollowUp: followUp ?? "There is new input on your assignment. Read it, then continue.");

        Team = TeamId.New();
        await using (var db = pg.NewContext())
            _leadToken = await PlaneProbe.LeadTokenAsync(db, Team, ct);
    }

    /// <summary>Enroll a machine: its own worker supervisor, its own relay data-plane
    /// daemon, and the registry send delegate that routes commands to the right one.
    ///
    /// <para><paramref name="spawnArgv"/> / <paramref name="prompt"/> /
    /// <paramref name="followUp"/> override this machine's <c>default</c> profile
    /// so a mixed fleet can run two ACP entry points (and two tool spellings)
    /// under one plane. Null keeps the fleet-wide profile.</para></summary>
    public async Task AddMachineAsync(
        string machineId,
        IReadOnlyList<string>? spawnArgv = null,
        string? prompt = null,
        string? followUp = null)
    {
        var profile = spawnArgv is null && prompt is null && followUp is null
            ? _profile
            : _profile with
            {
                Spawn = spawnArgv ?? _profile.Spawn,
                Prompt = prompt ?? _profile.Prompt,
                FollowUp = followUp ?? _profile.FollowUp,
            };
        var workRoot = NewWorkRoot();
        var ring = new OutboundEventRing(capacity: 256);
        // §12: a real transcript store per machine, so capture writes real files and the
        // read path answers from them — the same store on both halves, as in production.
        var transcripts = new TranscriptStore(
            Path.Combine(workRoot, TranscriptDefaults.DirName), TimeSpan.Zero, TimeProvider.System);
        var machineConfig = new MachineConfig(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default);
        var supervisor = new ProcessSupervisor(
            machineConfig, ring, TimeProvider.System, taskReaper: null, transcripts);
        // Real-worker mode hands the daemon this machine's REAL worker supervisor and its
        // profile, so the §10 process commands can resolve the calling task's profile and
        // apply the gate on the machine. The daemon keeps its own ring (see DaemonHarness) and
        // its replies reach the plane through the channel. The scripted tier keeps the
        // forward-only daemon it always had.
        var daemon = RealWorkerMode
            ? new DaemonHarness(
                machineId, new SinkForwardingChannel(_sink),
                workerSupervisor: supervisor,
                workerConfig: new RunnerConfig(
                    machineConfig,
                    new Dictionary<string, ProfileConfig>(StringComparer.Ordinal) { ["default"] = profile }),
                log: line => _machineLog.Enqueue($"[{machineId}] {line}"))
            : new DaemonHarness(machineId, new SinkForwardingChannel(_sink));
        await daemon.StartAsync();

        var machine = new MachineRig(machineId, supervisor, workRoot, daemon, ring, profile);
        _machines[machineId] = machine;

        // Real-worker mode: drain this machine's worker-supervisor ring (started/exited)
        // into the plane's sink, so a worker that exits without reporting requeues its
        // still-working task and a retry can redispatch it. The scripted tier leaves the
        // ring undrained (its exits carry no signal) — nothing here changes for it.
        if (RealWorkerMode)
        {
            _pumps.Add(Task.Run(() => PumpSupervisorRingAsync(ring, _pumpCts.Token)));
            if (_pumps.Count == 1)
            {
                // One heartbeat pump and one permission pump for the whole fleet,
                // started with the first machine.
                _pumps.Add(Task.Run(() => PumpHeartbeatsAsync(_pumpCts.Token)));
                _pumps.Add(Task.Run(() => PumpPermissionsAsync(_pumpCts.Token)));
            }
        }

        // The §10 socket seam: dispatch → the real spawn, open-/close-forward → the real
        // daemon standing the relay data plane up and tearing it down (§8.3 — a splice's
        // authority ends when its owning task leaves working). Nothing else is exercised here.
        var reader = new TranscriptReader(transcripts);
        _registry.Register(machineId, new HashSet<string> { "default" }, (command, sendCt) => command switch
        {
            DispatchCommand d => Spawn(machine, d),
            OpenForwardCommand or CloseForwardCommand => machine.Daemon.Send(command, sendCt),
            // §12: the real reader answering off the real captured files, replying through
            // the plane's real sink — the same two hops production makes.
            ReadTranscriptCommand read => _sink.HandleAsync(reader.Read(read), sendCt),
            // §10/§11 stop: the real supervisor's delivery path — an injected turn on the
            // held-open stdin where the profile declares a message seam, otherwise the
            // bounded TTL kill. The ack is kept for the scenario that measures it.
            StopCommand s => StopOnMachineAsync(machine, s, sendCt),
            // Live-session wake: the plane doorbells the held ACP process. Same
            // supervisor that spawned it, so TryPrompt lands on the right session.
            PromptCommand p => PromptOnMachineAsync(machine, p),
            // §10 process commands go to the real daemon, which applies the profile gate on
            // the machine and replies on the shared ring.
            StartProcessCommand or StopProcessCommand or WriteProcessCommand =>
                machine.Daemon.Send(command, sendCt),
            _ => Task.CompletedTask,
        });
    }

    /// <summary>Deliver a <c>stop</c> through the machine's real supervisor, recording the
    /// delivery mode it acked so a scenario can assert on (and a diagnostic can report)
    /// whether the agent was handed an injected turn or merely a kill deadline.</summary>
    private async Task StopOnMachineAsync(MachineRig machine, StopCommand stop, CancellationToken ct)
    {
        var ack = await machine.Supervisor.StopAsync(stop.Task, stop.Ttl, stop.Disposition, stop.Reason, ct);
        _stopAcks[stop.Task] = ack;
    }

    private static Task PromptOnMachineAsync(MachineRig machine, PromptCommand prompt)
    {
        machine.Supervisor.TryPrompt(prompt.Task);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Send a real <see cref="StopCommand"/> to the machine holding <paramref name="task"/>,
    /// through the registry send directly — there is no Lead
    /// MCP tool for a graceful stop, so this is the plane-side path a scenario drives.
    /// Returns false when the machine is unreachable.
    /// </summary>
    public Task<bool> SendStopAsync(
        string machineId, TaskId task, TimeSpan ttl, StopDisposition disposition, string? reason,
        CancellationToken ct) =>
        _registry.SendAsync(machineId, new StopCommand(task, ttl, disposition, reason), ct);

    /// <summary>How the machine acked the last <c>stop</c> for this task (§10) — the honest
    /// answer to "was the agent told, or just killed".</summary>
    public StopAck? StopAckFor(TaskId task) => _stopAcks.TryGetValue(task, out var ack) ? ack : null;

    private Task Spawn(MachineRig machine, DispatchCommand dispatch)
    {
        _ranOn[dispatch.Task] = machine.Id; // sticky: survives the worker's own exit/untrack
        machine.Supervisor.Spawn(dispatch, machine.Profile, machine.Id);
        return Task.CompletedTask;
    }

    /// <summary>The plane's services — for the §12 transcript read path, which is plane-side
    /// (the relay service and the dashboard queries) rather than an MCP tool.</summary>
    public IServiceProvider PlaneServices => _plane.Services;

    /// <summary>
    /// This fleet's public MCP endpoint — the same URL <see cref="DispatchService"/> embeds in
    /// every generated <c>{mcp_config}</c>. Bound to a loopback port at
    /// <see cref="StartAsync"/>, so it is only known after the rig starts.
    ///
    /// <para>Exposed for a harness that cannot be handed an MCP config as an argv path and
    /// needs the URL written into a config file of its own shape instead — Codex CLI, whose
    /// only client-config surface is a <c>config.toml</c> under <c>CODEX_HOME</c>. A claude
    /// profile never needs this: <c>--mcp-config {mcp_config}</c> carries the URL for it.</para>
    /// </summary>
    public string McpUrl => _baseUrl;

    /// <summary>
    /// Accept a verifying task over the real Lead MCP surface, driving it to <c>completed</c>
    /// — the terminal state a transcript is readable in (§12).
    /// </summary>
    public async Task AcceptAsync(TaskId task, CancellationToken ct)
    {
        await using var lead = await PlaneProbe.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct);
        await PlaneProbe.AcceptAsync(lead, task, ct);
    }

    /// <summary>Create a task for this fleet's Team via the real Lead MCP surface.</summary>
    /// <param name="continues">
    /// §11 continuation: the prior task whose agent session this new task resumes — "talk to
    /// the agent that has the context". Seeds the new row's session ref and preferred machine
    /// from that task, so its first dispatch prefers the machine holding the transcript.
    /// </param>
    public async Task<TaskId> CreateTaskAsync(string description, CancellationToken ct, TaskId? continues = null)
    {
        await using var lead = await PlaneProbe.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct);
        return await PlaneProbe.CreateTaskAsync(
            lead, description,
            completionCriteria: "the byte path holds",
            workspace: $"multimachine-{Guid.NewGuid():N}",
            ct, continues: continues);
    }

    /// <summary>
    /// The question a worker asked with <c>request_input</c>, read over the real Lead MCP
    /// surface (<c>get_task_question</c>) — proof the ask crossed the wire, and the thing a
    /// Lead reads before answering.
    /// </summary>
    public async Task<string> QuestionAsync(TaskId task, CancellationToken ct)
    {
        await using var lead = await PlaneProbe.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct);
        var read = await lead.CallToolAsync("get_task_question", new Dictionary<string, object?>
        {
            ["taskId"] = task.Value.ToString(),
        }, cancellationToken: ct);
        Assert.NotEqual(true, read.IsError);
        return string.Concat(read.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    /// <summary>
    /// Release a live ACP session on purpose over the real Lead MCP surface
    /// (<c>park_task</c>, §11): the task parks, its worker-instance token is revoked and the
    /// machine gets a <c>preserve_and_park</c> stop. Waits for the process to actually go,
    /// because the park record commits before the stop is even sent — answering while the
    /// old session is still winding down would race a redispatch against its predecessor.
    ///
    /// <para>The counterpart to <see cref="AnswerAsync"/>, not a variant of it: since
    /// <c>ideas/sessions.md</c> stage 2 an answer to a <em>live</em> session continues it in
    /// place, so this is the only route left to the §11 park → <c>session/load</c> path.</para>
    /// </summary>
    public async Task ParkTaskAsync(TaskId task, CancellationToken ct)
    {
        var exitsBefore = WorkerObserved(task)?.Exits ?? 0;
        await using (var lead = await PlaneProbe.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct))
        {
            var parked = await lead.CallToolAsync("park_task", new Dictionary<string, object?>
            {
                ["taskId"] = task.Value.ToString(),
            }, cancellationToken: ct);
            Assert.NotEqual(true, parked.IsError);
        }

        Assert.True(
            await WaitUntilAsync(
                () => Task.FromResult((WorkerObserved(task)?.Exits ?? 0) > exitsBefore),
                TimeSpan.FromSeconds(60)),
            $"park_task did not bring down the worker for {task} within 60s — a redispatch from "
            + "here would race the session it is meant to replace.");
    }

    /// <summary>
    /// Answer a blocked task over the real Lead MCP surface (§11). A still-live ACP session
    /// continues in place on the same instance; a task whose session is gone — or one
    /// <see cref="ParkTaskAsync"/> already parked — is requeued for redispatch <em>with its
    /// transcript resumed</em>. The answer itself reaches the worker on its next
    /// <c>get_task</c> and never through spawn argv (§13).
    /// </summary>
    public async Task AnswerAsync(TaskId task, string answer, CancellationToken ct)
    {
        await using var lead = await PlaneProbe.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct);
        var answered = await lead.CallToolAsync("answer_input_request", new Dictionary<string, object?>
        {
            ["taskId"] = task.Value.ToString(),
            ["answer"] = answer,
        }, cancellationToken: ct);
        Assert.NotEqual(true, answered.IsError);
    }

    /// <summary>
    /// The pending permission request on <paramref name="task"/> (§11 permission bridge), or
    /// null when nothing is waiting on a verdict. Read off the row rather than over MCP so a
    /// polling loop can tell "blocked on a permission request" from "blocked on a question"
    /// without spending a Lead round trip per tick.
    /// </summary>
    public async Task<(string? Tool, string? Input)?> PendingPermissionAsync(TaskId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var row = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == task.Value, ct);
        return row is { State: TaskState.BlockedOnInput, InputKind: InputRequestKind.Permission }
            ? (row.PermissionTool, row.InputQuestion)
            : null;
    }

    /// <summary>
    /// Decide a pending permission request over the real Lead MCP surface (§11 permission
    /// bridge). Unlike <see cref="AnswerAsync"/> this does not requeue anything: the worker is
    /// still running, blocked inside the tool call the harness relayed, and the verdict resumes
    /// it in place.
    /// </summary>
    public async Task AnswerPermissionAsync(
        TaskId task, string verdict, string? message, CancellationToken ct)
    {
        await using var lead = await PlaneProbe.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct);
        var answered = await lead.CallToolAsync("answer_permission_request", new Dictionary<string, object?>
        {
            ["taskId"] = task.Value.ToString(),
            ["verdict"] = verdict,
            ["message"] = message,
        }, cancellationToken: ct);
        Assert.NotEqual(true, answered.IsError);
    }

    /// <summary>The opaque harness session ref stamped from the worker's own
    /// <c>session-started</c> (§11) — null until the harness reports one.</summary>
    public async Task<string?> HarnessSessionRefAsync(TaskId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Value, ct)).HarnessSessionRef;
    }

    /// <summary>
    /// The harness session id each captured instance of <paramref name="task"/> reported on its
    /// own <c>system/init</c> line (§12 capture, one file per instance, §11's session ref).
    /// Ordered by instance.
    ///
    /// <para>This is the direct, harness-side proof of a resume, and it is worth having
    /// separately from the task row: the row holds one ref and cannot distinguish "the second
    /// instance resumed the first" from "the second instance cold-started and stamped its own".
    /// Two instances reporting the <em>same</em> id can only happen if the second really resumed
    /// the first's transcript — a cold start mints a new one.</para>
    /// </summary>
    public IReadOnlyList<string> InstanceSessionIdsOn(
        string machineId, TaskId task, Func<string, string?>? sessionIdOfLine = null)
    {
        var extract = sessionIdOfLine ?? SessionIdOfInitLine;
        var dir = System.IO.Path.Combine(
            _machines[machineId].WorkRoot, TranscriptDefaults.DirName, task.ToString());
        if (!System.IO.Directory.Exists(dir))
            return [];

        var ids = new List<string>();
        foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*.ndjson")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var line in ReadLinesOrEmpty(file))
            {
                if (extract(line) is not { Length: > 0 } id)
                    continue;
                ids.Add(id);
                break; // the init line is the first one that carries it; the id is stable per run
            }
        }
        return ids;
    }

    private static string[] ReadLinesOrEmpty(string file)
    {
        try { return System.IO.File.ReadAllLines(file); }
        catch (IOException) { return []; } // mid-write; the caller polls again
    }

    /// <summary>The <c>session_id</c> of a claude <c>system</c>/<c>init</c> stream line, or null
    /// for any other line. Same shape the runner's own Terminal reader keys off.</summary>
    private static string? SessionIdOfInitLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            if (Text(root, "type") != "system" || Text(root, "subtype") != "init")
                return null;
            return Text(root, "session_id");
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // stray non-JSON harness output, exactly as the reader tolerates
        }

        static string? Text(System.Text.Json.JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
    }

    /// <summary>The park record's machine (§11), null when unparked, alongside the harness
    /// session ref a redispatch would resume — which lives on the task row rather than in the
    /// park record, and so is present whether or not the task ever parked.</summary>
    public async Task<(string? Machine, string? SessionRef)> ParkAsync(TaskId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Value, ct);
        return (row.ParkMachine, row.HarnessSessionRef);
    }

    /// <summary>
    /// Dispatch the one currently-submitted task onto <paramref name="machineId"/>: mark
    /// only that machine ready, then run a single dispatch pass. Deterministic while the
    /// caller keeps at most one task submitted at a time (the suite's invariant).
    /// </summary>
    public async Task DispatchToAsync(string machineId, CancellationToken ct)
    {
        foreach (var (id, _) in _machines)
            SetReady(id, ready: id == machineId);
        await _dispatch.RunDispatchPassAsync(ct);
    }

    /// <summary>Which machines the last <see cref="DispatchToAsync"/> left ready, so a
    /// re-beat carries the same readiness and never disturbs dispatch steering.</summary>
    private readonly ConcurrentDictionary<string, bool> _ready = new(StringComparer.Ordinal);

    private void SetReady(string machineId, bool ready)
    {
        _ready[machineId] = ready;
        Beat(machineId);
    }

    /// <summary>
    /// One machine heartbeat, carrying what that machine is currently running (§10, §12).
    /// The process list matters: <c>list_processes</c> answers from what a machine last
    /// <em>reported</em> — the plane holds no process state of its own — so without a beat
    /// after a start, a cleanup agent cannot discover the survivor it was sent to stop.
    /// </summary>
    private void Beat(string machineId)
    {
        var machine = _machines[machineId];
        _registry.ApplyHeartbeat(machineId, new MachineHeartbeat(
            machineId, Ready: _ready.GetValueOrDefault(machineId), UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningTasks: machine.Supervisor.RunningTotal, ["default"],
            DateTimeOffset.UtcNow,
            Processes: machine.Daemon.ReportProcesses()));
    }

    /// <summary>What the machine reports it is running (§10) — the rig-side read behind
    /// <c>list_processes</c>, for assertions and diagnostics.</summary>
    public IReadOnlyList<ProcessStatus> ProcessesOn(string machineId) =>
        _machines[machineId].Daemon.ReportProcesses();

    /// <summary>
    /// Beats every machine on a fixed cadence, as a real docketd's heartbeat timer does.
    /// Real-worker mode only, and the reason it exists is <c>list_processes</c>: an agent
    /// that starts a process and then looks for it must see a heartbeat newer than the
    /// start, and no test should have to know that.
    /// </summary>
    private async Task PumpHeartbeatsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var id in _machines.Keys.ToArray())
                {
                    try { Beat(id); }
                    catch (KeyNotFoundException) { /* machine removed mid-sweep */ }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
    }

    /// <summary>
    /// Auto-allow ACP <c>session/request_permission</c> on this fleet. Real workers
    /// block inside the tool call until a Lead verdict; without this pump the
    /// first MCP tool hangs the turn. Questions are left for the scenario to
    /// answer — only <see cref="InputRequestKind.Permission"/> is decided here.
    /// </summary>
    private async Task PumpPermissionsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var db = pg.NewContext();
                    var pending = await db.Tasks.AsNoTracking()
                        .Where(t => t.TeamId == Team.Value
                            && t.State == TaskState.BlockedOnInput
                            && t.InputKind == InputRequestKind.Permission)
                        .Select(t => t.Id)
                        .ToListAsync(ct);
                    foreach (var id in pending)
                    {
                        try
                        {
                            await AnswerPermissionAsync(new TaskId(id), "allow", "e2e auto-allow", ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { /* already answered, parked, or tearing down */ }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* plane teardown / scope disposed */ }

                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
    }

    // ── Bounded reads of committed control-plane state ──────────────────────────

    public async Task<bool> ServiceExistsAsync(string name, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await db.RegisteredServices.AsNoTracking()
            .AnyAsync(s => s.TeamId == Team.Value && s.Name == name, ct);
    }

    public async Task<TaskState?> StateAsync(TaskId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await PlaneProbe.StateAsync(db, id, ct);
    }

    /// <summary>The machine a task is currently tracked as dispatched to (§10) — the
    /// fan-out scenario asserts the set of these spans ≥2 distinct machines.</summary>
    public string? MachineOf(TaskId task) => _registry.MachineFor(task);

    /// <summary>The machine a task was last spawned on — sticky, so it survives the
    /// worker's own exit (which untracks the live binding). The real tier asserts on this.</summary>
    public string? MachineRanOn(TaskId task) => _ranOn.TryGetValue(task, out var m) ? m : null;

    /// <summary>
    /// Drive a task to <see cref="TaskState.Verifying"/> on <paramref name="machineId"/>,
    /// tolerant of a worker that ends its turn without reporting. Each time the task is
    /// claimable (the initial submit, and every requeue-on-exit surfaced by the drained
    /// ring), a fresh worker is spawned — up to <paramref name="maxAttempts"/> — within
    /// <paramref name="budget"/>. Returns true once verifying, false at the budget
    /// deadline. This is the harness standing in for production's background dispatch
    /// loop, which would redispatch a requeued task on its own; a real haiku worker that
    /// flakes one turn no longer reds the whole opt-in job.
    /// </summary>
    public Task<bool> DispatchUntilVerifyingAsync(
        TaskId task, string machineId, int maxAttempts, TimeSpan budget, CancellationToken ct) =>
        DispatchUntilAsync(
            task, machineId, async () => await StateAsync(task, ct) == TaskState.Verifying,
            maxAttempts, budget, ct);

    /// <summary>
    /// The general form: drive <paramref name="task"/> on <paramref name="machineId"/> until
    /// <paramref name="done"/> holds, respawning a fresh worker each time the task is claimable
    /// again — the initial submit, and every requeue-on-exit — up to
    /// <paramref name="maxAttempts"/> within <paramref name="budget"/>.
    ///
    /// <para>Every scenario needs this rather than a plain poll, because "the worker ended its
    /// turn without doing the thing" is a real (if uncommon) haiku outcome, and the requeue that
    /// follows is only useful if something redispatches — which in production is the background
    /// dispatch loop and here is this method. A scenario whose goal is not <c>verifying</c> —
    /// blocking on a question, registering a service — needs the same tolerance, so the
    /// completion test is a predicate rather than a fixed state.</para>
    /// </summary>
    public async Task<bool> DispatchUntilAsync(
        TaskId task, string machineId, Func<Task<bool>> done, int maxAttempts, TimeSpan budget,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        var attempts = 0;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await done())
                return true;
            if (await StateAsync(task, ct) == TaskState.Submitted && attempts < maxAttempts)
            {
                await DispatchToAsync(machineId, ct);
                attempts++;
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
            catch (OperationCanceledException) { break; }
        }
        return await done();
    }

    /// <summary>
    /// A self-explanatory timeout dump for a real-worker task: the committed task row
    /// (state, attempt, requeue/verification counters, result ref, current instance), its
    /// sticky and live machine bindings, the ordered control-plane event log, and the
    /// ring-observed spawn/exit tallies with the last exit code. Distinguishes "never
    /// dispatched" (no spawn, still submitted) from "spawned but never reported" (a
    /// started+exited pair with the task requeued) from "reported something rejected"
    /// (verifying with an unexpected result ref) — so the NEXT CI failure needs no guesswork.
    /// </summary>
    public async Task<string> RealWorkerDiagnosticsAsync(TaskId task, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"REAL-WORKER DIAGNOSTICS for task {task}:");
        await using (var db = pg.NewContext())
        {
            // The committed row, its worker instances, and the ordered event log — the half
            // of a timeout dump that is the same question in every rig (PlaneProbe).
            await PlaneProbe.AppendTaskFactsAsync(sb, db, task, "  ", ct);
        }

        // Rig-specific, and the reason this dump exists on top of the shared facts: which
        // machine actually ran the task (sticky, survives the worker's exit) versus which the
        // registry tracks now (cleared when a worker exits and untracks).
        sb.AppendLine($"  machineRanOn={MachineRanOn(task) ?? "(none)"} machineTrackedNow={MachineOf(task) ?? "(none)"}");

        if (_observations.TryGetValue(task, out var obs))
            sb.AppendLine($"  worker: spawns={obs.Starts} exits={obs.Exits} lastExitCode={obs.LastExitCode?.ToString() ?? "(none)"}");
        else
            sb.AppendLine("  worker: no spawn/exit observed on the ring (ring draining is real-worker-mode only)");

        // What the machine did with the last stop, when one was sent: wrote a wind-down turn
        // to the harness's stdin, or only armed a deadline. Neither says the harness read
        // anything — that is unobservable (see StopDelivery) — but knowing which action was
        // taken is what tells a stop timeout apart from a turn nobody consumed.
        sb.AppendLine(_stopAcks.TryGetValue(task, out var ack)
            ? $"  stop: actioned={ack.Actioned} as {ack.Delivery}"
            : "  stop: none sent for this task");

        foreach (var (id, m) in _machines)
        {
            sb.AppendLine($"  ring[{id}] droppedEvents={m.Ring.DroppedCount}");
            // §10 processes: what the machine reports it is running. The cleanup scenarios
            // fail on the absence or the survival of an entry here, so a timeout must show it.
            var processes = m.Daemon.ReportProcesses();
            sb.AppendLine(processes.Count == 0
                ? $"  processes[{id}] (none)"
                : $"  processes[{id}] " + string.Join(", ", processes.Select(p =>
                    $"{p.Name}={p.State.ToString().ToLowerInvariant()}" +
                    $"(exit={p.ExitCode?.ToString() ?? "-"},stdin={p.StdinOpen})")));
        }

        if (!_machineLog.IsEmpty)
        {
            sb.AppendLine("  machine process log:");
            foreach (var line in _machineLog)
                sb.AppendLine($"    {line}");
        }

        // §12 capture is on for every rig profile, so the worker's own stdout (stream-json) and
        // stderr are on disk per task — the only place the harness's side of the story lives.
        // A tail of it is what distinguishes "the agent refused", "the agent never started a
        // turn", and "the process was killed mid-turn", none of which the plane's event log can
        // tell apart on its own.
        foreach (var (id, m) in _machines)
            AppendTranscriptTail(sb, id, m.WorkRoot, task);
        return sb.ToString();
    }

    /// <summary>Tail of a task's captured harness output on one machine (§12), bounded so a
    /// failure message stays readable — a stream-json line can be kilobytes.</summary>
    private static void AppendTranscriptTail(StringBuilder sb, string machineId, string workRoot, TaskId task)
    {
        var dir = System.IO.Path.Combine(workRoot, TranscriptDefaults.DirName, task.ToString());
        if (!System.IO.Directory.Exists(dir))
        {
            sb.AppendLine($"  transcript[{machineId}] (nothing captured)");
            return;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
        {
            string[] lines;
            try { lines = System.IO.File.ReadAllLines(file); }
            catch (IOException) { continue; } // being written; the rest of the dump still stands
            // Deliberately generous, and it was not always. Four 240-char lines was the right
            // budget for a stream transcript, where the last few NDJSON records held the whole
            // story. An ACP transcript is a conversation: the reason a turn went wrong is a
            // refused client request, a tool call that never happened, or a capability missing
            // from the handshake — none of which are in the last four lines, and one of which
            // (grok returning `cancelled` after 11k tokens, 2026-08-16) cost a 24-minute paid
            // run that produced no diagnosis at all. A whole ACP turn is tens of lines, so a
            // dump that covers one costs nothing and is the difference between reading the
            // failure and re-running it.
            const int TailLines = 60;
            const int LineBudget = 2000;
            var tail = lines.Reverse().Take(TailLines).Reverse()
                .Select(l => l.Length > LineBudget ? l[..LineBudget] + "…" : l);
            sb.AppendLine(
                $"  transcript[{machineId}] {System.IO.Path.GetFileName(file)} "
                + $"({lines.Length} lines, last {Math.Min(lines.Length, TailLines)}):");
            foreach (var line in tail)
                sb.AppendLine($"    {line}");
        }
    }

    /// <summary>Drains a machine's worker-supervisor ring into the plane sink (real-worker
    /// mode), recording spawn/exit tallies for diagnostics on the way through. Ends when
    /// the pump CTS cancels (disposal). Sink calls that race plane teardown are swallowed.</summary>
    private async Task PumpSupervisorRingAsync(OutboundEventRing ring, CancellationToken ct)
    {
        try
        {
            await foreach (var item in ring.ReadAllAsync(ct))
            {
                Observe(item.Event);
                try { await _sink.HandleAsync(item.Event, ct); }
                catch (OperationCanceledException) { break; }
                catch { /* teardown race: plane stopping / scope disposed — best effort */ }
            }
        }
        catch (OperationCanceledException) { /* disposing */ }
    }

    private void Observe(RunnerEvent evt)
    {
        switch (evt)
        {
            case StartedEvent s:
                _observations.GetOrAdd(s.Task, static _ => new WorkerObservation()).Starts++;
                break;
            case ExitedEvent e:
                var o = _observations.GetOrAdd(e.Task, static _ => new WorkerObservation());
                o.Exits++;
                o.LastExitCode = e.ExitCode;
                break;
        }
    }

    private sealed class WorkerObservation
    {
        public int Starts;
        public int Exits;
        public int? LastExitCode;
    }

    /// <summary>
    /// What the ring saw of this task's worker process (real-worker mode): how many times it
    /// spawned, how many times it ended, and how it ended last. The exit <em>code</em> is the
    /// fact a stop scenario turns on — a voluntary wind-down exits 0, a wind-down deadline kill
    /// does not — so it is read here rather than inferred from the task's state.
    /// </summary>
    public (int Starts, int Exits, int? LastExitCode)? WorkerObserved(TaskId task) =>
        _observations.TryGetValue(task, out var o) ? (o.Starts, o.Exits, o.LastExitCode) : null;

    public async Task<string?> ResultReferenceAsync(TaskId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value, ct)).ResultReference;
    }

    /// <summary>
    /// Every usage report a task's harness sent, as <b>committed plane state</b> (§10 telemetry
    /// ingest) — one row per model the harness named.
    ///
    /// <para>Reading the persisted rows rather than the parsed stream is the whole point of the
    /// fact that uses this: the parser is covered against captured fixtures already, so what is
    /// unproven is the leg BETWEEN the layers — docketd encoding the event, the wire carrying it,
    /// the plane's sink storing it. That leg is exactly where a bug hid once, with every layer
    /// green in isolation and nothing sending anything (the <c>EncodeEvent</c> arm that was never
    /// registered), and only a query against the committed row can catch its return.</para>
    ///
    /// <para>Empty is a legitimate answer and callers must poll rather than assert immediately:
    /// claude prints its <c>result</c> line as the run ENDS, which is after the
    /// <c>report_result</c> that moved the task to <c>verifying</c> — so a row that is not there
    /// yet may simply be a second away.</para>
    ///
    /// <para><b>Returns the view, not the row, and that is load-bearing.</b> Storage spells the
    /// unnamed model as the empty string because a composite primary key cannot hold a NULL
    /// (<c>DocketDbContext</c>), and <see cref="TaskUsageView.From"/> is the one place that
    /// convention is undone. Handing a test the raw <see cref="TaskUsageRow"/> routed around that
    /// single place, so an unattributed report surfaced as <c>""</c> here while every production
    /// reader saw <c>null</c> — which cost the first real-OpenCode dispatch a red on an otherwise
    /// green run, because OpenCode names no model and was the first harness to exercise this path.
    /// Reading through the view means a test asserts the same <c>model is null ⟺ unnamed</c>
    /// invariant the §12 surfaces do, rather than a storage detail.</para>
    /// </summary>
    public async Task<IReadOnlyList<TaskUsageView>> ReportedUsageAsync(TaskId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var rows = await db.TaskUsage.AsNoTracking()
            .Where(u => u.TaskId == id.Value)
            .ToListAsync(ct);
        return [.. rows.Select(TaskUsageView.From)];
    }

    /// <summary>Read a collaborator marker from the machine's work dir for a task, or null.</summary>
    public async Task<string?> ReadMarkerAsync(string machineId, TaskId task, string markerName, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(_machines[machineId].WorkRoot, task.ToString(), markerName);
        if (!System.IO.File.Exists(path))
            return null;
        try { return await System.IO.File.ReadAllTextAsync(path, ct); }
        catch (IOException) { return null; } // mid-rename; the caller polls again
    }

    /// <summary>Any harness diagnostic a failing collaborator left, across every machine.</summary>
    public async Task<string> DiagnoseAsync(TaskId task, CancellationToken ct)
    {
        foreach (var (id, machine) in _machines)
        {
            var errPath = System.IO.Path.Combine(machine.WorkRoot, task.ToString(), "harness_error.txt");
            if (System.IO.File.Exists(errPath))
                return $"Harness diagnostic on {id}:\n" + await System.IO.File.ReadAllTextAsync(errPath, ct);
        }
        return "(no harness_error.txt on any machine)";
    }

    /// <summary>Bounded poll: succeeds as soon as <paramref name="condition"/> holds, or
    /// false at the deadline. No fixed sleeps — the poll IS the synchronization. Kept as the
    /// name eighteen call sites already read; the implementation is
    /// <see cref="PlaneProbe.WaitUntilAsync"/>, shared with the chaos rig.</summary>
    public static Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout) =>
        PlaneProbe.WaitUntilAsync(condition, timeout);

    public async ValueTask DisposeAsync()
    {
        // Stop the ring pumps before tearing anything down, so a worker killed below
        // can't drive a requeue through the sink while the plane is stopping.
        await _pumpCts.CancelAsync();
        foreach (var pump in _pumps)
        {
            try { await pump; } catch { /* cancelled / best effort */ }
        }
        _pumpCts.Dispose();

        foreach (var machine in _machines.Values)
        {
            machine.Supervisor.KillAll();
            await machine.Daemon.StopAsync();
            TryDeleteRoot(machine.WorkRoot);
        }
        if (_relay is not null) await _relay.StopAsync();
        if (_plane is not null) await _plane.StopAsync();
    }

    // ── Harness path resolution (mirrors the live-fleet crown) ──────────────────

    /// <summary>
    /// The built <see cref="Docket.CollabHarness"/> apphost, resolved from its own bin
    /// (not the copy beside this test) — its MCP-client closure is copied local only
    /// there, so the copy beside the test cannot start.
    /// </summary>
    private static string CollabHarnessPath() => SiblingApphostPath("Docket.CollabHarness");

    /// <summary>
    /// The built <see cref="Docket.Runner.TestHarness"/> apphost — an absolute path to a real
    /// binary on every platform, which is what a worker's <c>start_process</c> argv needs
    /// (§10: argv, never a shell, and the process gets docketd's environment rather than a
    /// shell's PATH). Its <c>http-serve</c> mode is the listener the §8.3 scenario forwards to.
    /// </summary>
    public static string TestHarnessPath() => SiblingApphostPath("Docket.Runner.TestHarness");

    /// <summary>
    /// The <c>DOTNET_ROOT</c> a spawned .NET apphost needs, derived from the runtime this test
    /// is itself running on. An agent-started process gets docketd's environment rather than a
    /// shell's (§10), and an apphost that cannot find a runtime fails at launch with no output —
    /// so a scenario that spawns one has to pass this explicitly through <c>start_process</c>'s
    /// <c>env</c>, which is the same thing the worker skill tells agents to do for <c>PATH</c>.
    /// It matters locally more than in CI: a side-by-side install (<c>~/.dotnet</c>) is in none
    /// of the default search locations.
    /// </summary>
    public static string DotnetRoot()
    {
        // The runtime directory is {root}/shared/Microsoft.NETCore.App/{version}/.
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var root = new System.IO.DirectoryInfo(runtime).Parent?.Parent?.Parent?.FullName;
        return root ?? runtime;
    }

    /// <summary>
    /// Put <see cref="DotnetRoot"/> on this process's environment, which an agent-started
    /// process inherits (docketd's environment is the child's base, §10). Belt and braces
    /// alongside telling the agent to pass it in <c>env</c>: the <c>env</c> argument is what the
    /// worker skill documents and is worth exercising, but a scenario should not go red because
    /// a model dropped one optional argument — the interesting failure is a refusal or a missing
    /// process, not a runtime the harness could not locate.
    /// </summary>
    public static void PublishDotnetRootForSpawnedApphosts()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
            Environment.SetEnvironmentVariable("DOTNET_ROOT", DotnetRoot());
    }

    /// <summary>
    /// Resolve a sibling harness apphost from <em>its own</em> bin (not the copy beside this
    /// test): an MCP-client closure is copied local only there, so the copy beside the test
    /// cannot start.
    /// </summary>
    private static string SiblingApphostPath(string stem)
    {
        var testDir = System.IO.Path.GetDirectoryName(typeof(FleetRig).Assembly.Location)!;
        var harnessDir = testDir.Replace(
            System.IO.Path.Combine("Docket.MultiMachine.Tests", "bin"),
            System.IO.Path.Combine(stem, "bin"),
            StringComparison.Ordinal);
        var apphost = System.IO.Path.Combine(harnessDir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        return System.IO.File.Exists(apphost)
            ? apphost
            : throw new System.IO.FileNotFoundException(
                $"{stem} apphost not found at {apphost}; is {stem} built?");
    }

    private static string NewWorkRoot()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "docket-multimachine-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteRoot(string dir)
    {
        try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed record MachineRig(
        string Id, ProcessSupervisor Supervisor, string WorkRoot, DaemonHarness Daemon, OutboundEventRing Ring,
        ProfileConfig Profile);
}
