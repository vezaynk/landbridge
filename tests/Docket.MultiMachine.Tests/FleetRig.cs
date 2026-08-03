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
/// </summary>
internal sealed class FleetRig(PostgresFixture pg, IReadOnlyList<string>? spawnArgv = null) : IAsyncDisposable
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
    /// ON for the real-<c>claude -p</c> tier — where a worker that ends its turn WITHOUT
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

    public TeamId Team { get; private set; }
    private string _leadToken = null!;

    public async Task StartAsync(CancellationToken ct)
    {
        // Pre-reserve the relay URL so the plane can be configured and the relay bound
        // to the same address without a build-order race (§8.3).
        var relayUrl = MultiMachineKit.ReserveLoopbackUrl();
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
            spawnArgv ?? [CollabHarnessPath(), "--mcp-config", "{mcp_config}"],
            new StopConfig(StopMode.Signal, Signal: null, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(30)),
            Resume: null,
            new EventsConfig(EventsSource.None, new Dictionary<string, string>()),
            new TelemetryConfig(Otel: false, Endpoint: null),
            // §12: capture on, so the fleet exercises the real capture → serve path end to
            // end. Pruning disabled (0) — a rig lives seconds and a sweep would only add
            // nondeterminism.
            new LogsConfig(Path: null, Format: null, Capture: true, PruneAfterDays: 0),
            MaxConcurrent: null);

        Team = TeamId.New();
        _leadToken = await MultiMachineKit.LeadTokenAsync(pg, Team, ct);
    }

    /// <summary>Enroll a machine: its own worker supervisor, its own relay data-plane
    /// daemon, and the registry send delegate that routes commands to the right one.</summary>
    public async Task AddMachineAsync(string machineId)
    {
        var workRoot = NewWorkRoot();
        var ring = new OutboundEventRing(capacity: 256);
        // §12: a real transcript store per machine, so capture writes real files and the
        // read path answers from them — the same store on both halves, as in production.
        var transcripts = new TranscriptStore(
            Path.Combine(workRoot, TranscriptDefaults.DirName), TimeSpan.Zero, TimeProvider.System);
        var supervisor = new ProcessSupervisor(
            new MachineConfig(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default),
            ring, TimeProvider.System, taskReaper: null, transcripts);
        var daemon = new DaemonHarness(machineId, new SinkForwardingChannel(_sink));
        await daemon.StartAsync();

        var machine = new MachineRig(machineId, supervisor, workRoot, daemon, ring);
        _machines[machineId] = machine;

        // Real-worker mode: drain this machine's worker-supervisor ring (started/exited)
        // into the plane's sink, so a worker that exits without reporting requeues its
        // still-working task and a retry can redispatch it. The scripted tier leaves the
        // ring undrained (its exits carry no signal) — nothing here changes for it.
        if (RealWorkerMode)
            _pumps.Add(Task.Run(() => PumpSupervisorRingAsync(ring, _pumpCts.Token)));

        // The §10 socket seam: dispatch → the real spawn, open-forward → the real
        // daemon standing up the relay data plane. Nothing else is exercised here.
        var reader = new TranscriptReader(transcripts);
        _registry.Register(machineId, new HashSet<string> { "default" }, (command, sendCt) => command switch
        {
            DispatchCommand d => Spawn(machine, d),
            OpenForwardCommand => machine.Daemon.Send(command, sendCt),
            // §12: the real reader answering off the real captured files, replying through
            // the plane's real sink — the same two hops production makes.
            ReadTranscriptCommand read => _sink.HandleAsync(reader.Read(read), sendCt),
            _ => Task.CompletedTask,
        });
    }

    private Task Spawn(MachineRig machine, DispatchCommand dispatch)
    {
        _ranOn[dispatch.Task] = machine.Id; // sticky: survives the worker's own exit/untrack
        machine.Supervisor.Spawn(dispatch, _profile, machine.Id);
        return Task.CompletedTask;
    }

    /// <summary>The plane's services — for the §12 transcript read path, which is plane-side
    /// (the relay service and the dashboard queries) rather than an MCP tool.</summary>
    public IServiceProvider PlaneServices => _plane.Services;

    /// <summary>
    /// Accept a verifying task over the real Lead MCP surface, driving it to <c>completed</c>
    /// — the terminal state a transcript is readable in (§12).
    /// </summary>
    public async Task AcceptAsync(TaskId task, CancellationToken ct)
    {
        await using var lead = await MultiMachineKit.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct);
        var verdict = await lead.CallToolAsync("submit_review", new Dictionary<string, object?>
        {
            ["taskId"] = task.Value.ToString(),
            ["verdict"] = "accept",
        }, cancellationToken: ct);
        Assert.NotEqual(true, verdict.IsError);
    }

    /// <summary>Create a task for this fleet's Team via the real Lead MCP surface.</summary>
    public async Task<TaskId> CreateTaskAsync(string description, CancellationToken ct)
    {
        await using var lead = await MultiMachineKit.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct);
        var created = await lead.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["description"] = description,
            ["completionCriteria"] = "the byte path holds",
            ["mode"] = "lead",
            ["profile"] = null,
            ["workspace"] = $"multimachine-{Guid.NewGuid():N}",
        }, cancellationToken: ct);
        Assert.NotEqual(true, created.IsError);
        return new TaskId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
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

    private void SetReady(string machineId, bool ready) =>
        _registry.ApplyHeartbeat(machineId, new MachineHeartbeat(
            machineId, Ready: ready, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningTasks: 0, ["default"], DateTimeOffset.UtcNow));

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
        return await new TaskStore(db, TimeProvider.System).GetStateAsync(id, ct);
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
    public async Task<bool> DispatchUntilVerifyingAsync(
        TaskId task, string machineId, int maxAttempts, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        var attempts = 0;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var state = await StateAsync(task, ct);
            if (state == TaskState.Verifying)
                return true;
            if (state == TaskState.Submitted && attempts < maxAttempts)
            {
                await DispatchToAsync(machineId, ct);
                attempts++;
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
            catch (OperationCanceledException) { break; }
        }
        return await StateAsync(task, ct) == TaskState.Verifying;
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
            var row = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == task.Value, ct);
            if (row is null)
            {
                sb.AppendLine("  (task row not found)");
            }
            else
            {
                sb.AppendLine(
                    $"  state={row.State} attempt={row.Attempt} infraRequeues={row.InfrastructureRequeues} " +
                    $"verificationFailures={row.VerificationFailures}");
                sb.AppendLine(
                    $"  resultReference={row.ResultReference ?? "(none)"} " +
                    $"currentInstance={row.CurrentInstanceId?.ToString() ?? "(none)"}");
            }

            sb.AppendLine($"  machineRanOn={MachineRanOn(task) ?? "(none)"} machineTrackedNow={MachineOf(task) ?? "(none)"}");

            var events = await db.TaskEvents.AsNoTracking()
                .Where(e => e.TaskId == task.Value)
                .OrderBy(e => e.OccurredAt)
                .ToListAsync(ct);
            sb.AppendLine($"  events ({events.Count}):");
            foreach (var e in events)
                sb.AppendLine(
                    $"    {e.OccurredAt:HH:mm:ss.fff} {e.Kind} {e.FromState?.ToString() ?? "-"}->{e.ToState?.ToString() ?? "-"}" +
                    (e.Detail is { Length: > 0 } d ? $" [{d}]" : ""));
        }

        if (_observations.TryGetValue(task, out var obs))
            sb.AppendLine($"  worker: spawns={obs.Starts} exits={obs.Exits} lastExitCode={obs.LastExitCode?.ToString() ?? "(none)"}");
        else
            sb.AppendLine("  worker: no spawn/exit observed on the ring (ring draining is real-worker-mode only)");

        foreach (var (id, m) in _machines)
            sb.AppendLine($"  ring[{id}] droppedEvents={m.Ring.DroppedCount}");

        sb.AppendLine(
            "  note: the claude worker's stdout/stderr (stream-json) is inherited to the test/CI job " +
            "console (EventsSource.None leaves it unredirected), not captured per task here.");
        return sb.ToString();
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

    public async Task<string?> ResultReferenceAsync(TaskId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value, ct)).ResultReference;
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
    /// false at the deadline. No fixed sleeps — the poll IS the synchronization.</summary>
    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(100);
        }
        return await condition();
    }

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
    private static string CollabHarnessPath()
    {
        const string stem = "Docket.CollabHarness";
        var testDir = System.IO.Path.GetDirectoryName(typeof(FleetRig).Assembly.Location)!;
        var harnessDir = testDir.Replace(
            System.IO.Path.Combine("Docket.MultiMachine.Tests", "bin"),
            System.IO.Path.Combine(stem, "bin"),
            StringComparison.Ordinal);
        var apphost = System.IO.Path.Combine(harnessDir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        return System.IO.File.Exists(apphost)
            ? apphost
            : throw new System.IO.FileNotFoundException(
                $"collaborator apphost not found at {apphost}; is Docket.CollabHarness built?");
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
        string Id, ProcessSupervisor Supervisor, string WorkRoot, DaemonHarness Daemon, OutboundEventRing Ring);
}
