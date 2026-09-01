using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;

namespace Landbridge.Chaos.Tests;

/// <summary>
/// The §17.8 chaos rig: a real control plane and a real <c>landbridged</c> as separate
/// OS processes over a real Postgres, with real worker binaries underneath. Nothing
/// here is a seam — where <c>Landbridge.MultiMachine.Tests</c>' FleetRig stands in
/// for the §10 socket with an in-process delegate, this dials the actual
/// <c>/runner</c> WebSocket, so the processes can be SIGKILLed and restarted the way
/// §17.8 asks ("kill a runner mid-task", "SIGKILL landbridged and restart it").
///
/// <para>Three deliberate choices make the scenarios deterministic and bounded:</para>
/// <list type="bullet">
/// <item><b>Ephemeral ports.</b> Both the plane's URL and every reservation come from
/// an OS-assigned port, so parallel CI legs cannot collide.</item>
/// <item><b>Shrunk liveness windows.</b> The plane's aliveness / no-progress clocks
/// and landbridged's heartbeat cadence are configuration (§10), so a scenario that would
/// otherwise wait 30 real minutes for the no-progress ceiling waits seconds instead.
/// No test here sleeps for a fixed duration as a synchronization device: every wait
/// is a bounded poll of committed control-plane state with a hard deadline.</item>
/// <item><b>Secrets off argv.</b> The machine token reaches landbridged through the
/// environment and the worker token through the injected <c>mcp.json</c>, never as an
/// argument (§13).</item>
/// </list>
/// </summary>
internal sealed class ChaosFleet(PostgresFixture pg, ChaosFleetOptions options) : IAsyncDisposable
{
    private readonly List<ChaosProcess> _strays = new();
    private readonly List<string> _timeline = new();

    private ChaosProcess? _plane;
    private ChaosProcess? _landbridged;
    private HttpClient _http = null!;

    private string _planeUrl = null!;
    private string _workRoot = null!;
    private string _stateDir = null!;
    private string _configPath = null!;
    private string _machineToken = null!;
    private string _leadToken = null!;

    public TeamId Team { get; private set; }

    /// <summary>The enrolled machine's id — also the tag the §10 restart sweep keys on.</summary>
    public string MachineId { get; private set; } = null!;

    public string WorkRoot => _workRoot;
    public ChaosProcess Landbridged => _landbridged ?? throw new InvalidOperationException("landbridged is not running");

    // ── Bring-up ────────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        await StartPlaneOnlyAsync(ct);
        await StartLandbridgedAsync(ct);
        Note($"fleet up: plane={_planeUrl} machine={MachineId}");
    }

    /// <summary>
    /// Everything except <c>landbridged</c>: the credentials, the plane, and landbridged's config
    /// written and ready. For the one scenario that needs to hold a <c>/runner</c> connection
    /// open BEFORE the daemon dials in, so the daemon's connection is the one that supersedes
    /// (§17.8 "close a laptop and reattach", #94). Every other scenario wants
    /// <see cref="StartAsync"/>.
    /// </summary>
    public async Task StartPlaneOnlyAsync(CancellationToken ct)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _workRoot = NewTempDir("work");
        // §13/§10: landbridged's own state dir must be a temp path. On a box that has ever
        // run `landbridged --enroll`, a real ~/.landbridge/credentials.json would override the
        // machine id we hand it — and point it at a real control plane.
        _stateDir = NewTempDir("state");

        // Mint the machine identity and the Lead credential directly against the store, the
        // idiom the existing suites use (RunnerSpineEndToEndTests for the machine,
        // PlaneProbe.LeadTokenAsync for the Lead). The machine half stays here: this is the
        // only rig that enrolls one, because it is the only one with a real landbridged to enroll.
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var enrollment = await tokens.IssueEnrollmentTokenAsync(ct);
            var credentials = await tokens.ExchangeEnrollmentAsync(
                enrollment.Token,
                new MachineDeclaration("chaos-box", "linux"),
                ct)
                ?? throw new InvalidOperationException("chaos rig: enrollment exchange returned null");
            MachineId = credentials.MachineId.ToString();
            _machineToken = credentials.Access.Token;

            Team = TeamId.New();
            _leadToken = await PlaneProbe.LeadTokenAsync(db, Team, ct);
        }

        _planeUrl = PlaneProbe.ReserveLoopbackUrl();
        await StartPlaneAsync(ct);

        _configPath = WriteLandbridgedConfig();
    }

    private async Task StartPlaneAsync(CancellationToken ct)
    {
        var binary = ChaosBinaries.ControlPlane();
        _plane = ChaosProcess.Start("plane", binary, [], new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = _planeUrl,
            // ServiceDefaults maps /health only in Development — that endpoint is this
            // rig's readiness gate, exactly as it is the AppHost's.
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__Landbridge"] = pg.ConnectionString,
            // The URL the plane writes into every worker's injected mcp.json. Without
            // this the workers would dial the 127.0.0.1:5050 default and never find us.
            ["Landbridge__PublicMcpUrl"] = _planeUrl,
            // The PostgresFixture already migrated; a second migrate would only race.
            ["Landbridge__MigrateOnStartup"] = "false",
            // §10 two-clock liveness, shrunk. Note PerTaskLivenessWindow doubles as the
            // sweep PERIOD, and that these parse as TimeSpan — a bare number would mean
            // DAYS, so they are always written out in full.
            ["Landbridge__PerTaskLivenessWindow"] = Fmt(options.PerTaskLivenessWindow),
            ["Landbridge__NoProgressCeiling"] = Fmt(options.NoProgressCeiling),
            // Keep Landbridge's own categories verbose — the liveness warning is the only
            // record of which clock reclaimed a task — but silence framework chatter.
            // EF Core logs every dispatch query at Information, and the dispatch loop
            // runs on every heartbeat, so at default levels the plane emits hundreds of
            // SQL lines a minute: they bury the lines a scenario waits on and make the
            // failure dump useless.
            ["Logging__LogLevel__Default"] = "Information",
            ["Logging__LogLevel__Microsoft"] = "Warning",
        },
        // A host's content root defaults to the CURRENT directory, so it has to run
        // from its own bin or it would look for appsettings.json beside this test.
        workingDirectory: Path.GetDirectoryName(binary));

        var deadline = DateTime.UtcNow + options.StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!_plane.Alive)
                throw new InvalidOperationException("the control plane exited during startup:\n" + _plane.Tail());
            try
            {
                using var response = await _http.GetAsync(_planeUrl + "/health", ct);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // not listening yet
            }
            await Task.Delay(100, ct);
        }
        throw new TimeoutException(
            $"the control plane never served /health within {options.StartupTimeout}:\n" + _plane.Tail());
    }

    /// <summary>
    /// Starts landbridged and waits for its <c>landbridged up:</c> announcement — which §10
    /// guarantees is printed only AFTER the restart sweep has run, so seeing the line
    /// is what makes "the sweep completed before this daemon accepted any dispatch"
    /// an observable fact. Returns the announcement line.
    /// </summary>
    public async Task<string> StartLandbridgedAsync(CancellationToken ct)
    {
        _landbridged = ChaosProcess.Start("landbridged", ChaosBinaries.Landbridged(),
            ["--config", _configPath, "--state-dir", _stateDir],
            new Dictionary<string, string>
            {
                ["LANDBRIDGE_CONTROL_URL"] = WsRunnerUrl(_planeUrl),
                // §13: the machine token travels in the environment, never in argv.
                ["LANDBRIDGE_MACHINE_TOKEN"] = _machineToken,
                ["LANDBRIDGE_MACHINE_ID"] = MachineId,
            });

        var up = await _landbridged.WaitForLineAsync(l => l.Contains("landbridged up:", StringComparison.Ordinal),
            options.StartupTimeout);
        if (up is null)
            throw new TimeoutException(
                $"landbridged never announced itself within {options.StartupTimeout}:\n" + _landbridged.Tail());
        Note("landbridged: " + up.Trim());
        // `up` is printed as soon as the dial task is started, not after the
        // socket is live. Creating work against a machine that has not yet
        // heartbeated leaves the first dispatch pass with nothing ready; wait
        // for the channel so the stale-token scenario is not racing the dial.
        var connected = await _landbridged.WaitForLineAsync(
            l => l.Contains("control plane connected:", StringComparison.Ordinal),
            options.StartupTimeout);
        if (connected is null)
            throw new TimeoutException(
                $"landbridged never connected to the plane within {options.StartupTimeout}:\n" + _landbridged.Tail());
        return up;
    }

    /// <summary>
    /// SIGKILL landbridged — no handler, no flush, no child cleanup (§17.8). The plane
    /// notices the dropped socket and requeues everything the machine held; asserting
    /// that is the caller's job.
    /// </summary>
    public async Task SigkillLandbridgedAsync()
    {
        var victim = _landbridged ?? throw new InvalidOperationException("landbridged is not running");
        Note($"SIGKILL landbridged pid={victim.Id}");
        victim.Sigkill();
        await victim.WaitForExitAsync(TimeSpan.FromSeconds(15));
        victim.Dispose();
        _landbridged = null;
    }

    /// <summary>
    /// SIGKILL the plane, then bring an identically-configured one back up on the same
    /// URL and the same database — a plane restart under live work, which is exactly what
    /// leaves in-flight tasks with no in-memory tracking behind them (§17.8, #86).
    /// landbridged is left running and reconnects on its own backoff.
    /// </summary>
    public async Task RestartPlaneAsync(CancellationToken ct)
    {
        var victim = _plane ?? throw new InvalidOperationException("the plane is not running");
        Note($"SIGKILL plane pid={victim.Id}");
        victim.Sigkill();
        await victim.WaitForExitAsync(TimeSpan.FromSeconds(15));
        victim.Dispose();
        _plane = null;
        await StartPlaneAsync(ct);
        Note("plane back up");
    }

    /// <summary>
    /// Waits for a line from the CURRENT plane process. After
    /// <see cref="RestartPlaneAsync"/> that process is brand new and its retained output
    /// starts empty, so a line matched here unambiguously came from the plane that came
    /// back — which is what lets a scenario observe post-restart behaviour directly
    /// instead of inferring it from committed state alone. Returns the line, or null at
    /// the deadline.
    /// </summary>
    public Task<string?> WaitForPlaneLineAsync(Func<string, bool> match, TimeSpan timeout) =>
        (_plane ?? throw new InvalidOperationException("the plane is not running"))
            .WaitForLineAsync(match, timeout);

    /// <summary>
    /// Plants a tagged process tree that will OUTLIVE landbridged, standing in for the
    /// escaped grandchild §10 describes (a dev server that <c>setsid</c>ed out of the
    /// task's process group and still holds its port). Two mechanisms have to be
    /// defeated for it to survive, exactly as <c>LandbridgedStrayReapEndToEndTests</c>
    /// does: this rig holds its stdin open (so the harness's dead-man watch never sees
    /// EOF) and disables PDEATHSIG (armed per-thread, so an xunit pool thread retiring
    /// would otherwise kill it early). A real stray survives because NOTHING fires —
    /// pinning both to no-mechanism reproduces that, and leaves the restart sweep as
    /// the only thing that can reap it.
    /// </summary>
    public async Task<StrayTree> PlantStrayAsync(CancellationToken ct)
    {
        var strayDir = Path.Combine(_workRoot, "planted-stray-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(strayDir);
        var stray = ChaosProcess.Start("planted-stray", ChaosBinaries.RunnerTestHarness(), ["spawn-child"],
            new Dictionary<string, string>
            {
                // The tag the sweep matches on (§10). Set ONLY on this child: were it
                // ever exported into the test process's own environment, a restarted
                // landbridged would kill the test runner itself.
                ["LANDBRIDGE_MACHINE_ID"] = MachineId,
                ["LANDBRIDGE_SESSION_ID"] = Guid.NewGuid().ToString(),
                ["LANDBRIDGE_TEST_DISABLE_PDEATHSIG"] = "1",
            },
            workingDirectory: strayDir,
            redirectStdin: true);
        _strays.Add(stray);

        var pidPath = Path.Combine(strayDir, "child.pid");
        if (!await WaitUntilAsync(() => Task.FromResult(File.Exists(pidPath)), options.StartupTimeout, ct))
            throw new TimeoutException("the planted stray never recorded its grandchild pid:\n" + stray.Tail());
        var grandchildPid = int.Parse(await File.ReadAllTextAsync(pidPath, ct));

        if (!await WaitUntilAsync(
                () => Task.FromResult(ChaosProcess.PidAlive(stray.Id) && ChaosProcess.PidAlive(grandchildPid)),
                TimeSpan.FromSeconds(15), ct))
            throw new TimeoutException("the planted stray tree never came alive:\n" + stray.Tail());

        Note($"planted stray tree: pid={stray.Id} grandchild={grandchildPid}");
        return new StrayTree(stray.Id, grandchildPid);
    }

    // ── Driving work over the real MCP surface ──────────────────────────────────

    /// <summary>
    /// Creates a task as the Lead. <paramref name="profile"/> selects the runner
    /// profile (§10 exact-match routing): <c>null</c> is the reporting worker that
    /// drives a task to a report, <see cref="ChaosProfiles.Wedge"/> is the
    /// worker that emits nothing and reports nothing.
    /// </summary>
    public async Task<SessionId> CreateSessionAsync(string description, string? profile, CancellationToken ct)
    {
        await using var lead = await ConnectLeadAsync(ct);
        var task = await PlaneProbe.CreateSessionAsync(
            lead, description, ct, Team.Value.ToString(), profile: profile ?? "default");
        Note($"created task {task} profile={profile ?? "default"}");
        return task;
    }

    /// <summary>Accept a reported session as the Lead, driving it to <c>completed</c> (§7).</summary>
    public async Task AcceptAsync(SessionId task, CancellationToken ct)
    {
        await using var lead = await ConnectLeadAsync(ct);
        await PlaneProbe.AcceptAsync(lead, task, Team.Value.ToString(), ct);
    }

    /// <summary>
    /// Lead resume of a failed attempt. The plane no longer requeues; the bar
    /// simulates the Lead waking the park so dispatch can place it again.
    /// </summary>
    public async Task ResumeFailedAsync(SessionId task, CancellationToken ct)
    {
        // Occupancy catch-up from the death that just failed the row races this
        // write; Conflict means someone else already moved xmin. Reload and
        // retry, and treat an already-healthy working row as the resume landing.
        for (var attempt = 0; ; attempt++)
        {
            await using var db = pg.NewContext();
            var store = new SessionStore(db, TimeProvider.System);
            // Empty note: the wedge never pulls MCP, and a non-empty answer would
            // leave awaiting_pull on a process that cannot receipt it.
            var result = await store.ApplyAsync(task, new WakeParked(), ct);
            if (result is StoreResult.Applied)
            {
                Note($"resumed failed task {task}");
                return;
            }

            var facts = await FactsAsync(task, ct);
            if (facts is { State: SessionState.Working })
            {
                Note($"resume of {task} already landed ({result.GetType().Name})");
                return;
            }

            if (result is not StoreResult.Conflict || attempt >= 8)
                throw new InvalidOperationException($"resume of {task} did not apply: {result.GetType().Name}");
            await Task.Delay(50, ct);
        }
    }

    private Task<McpClient> ConnectLeadAsync(CancellationToken ct) => ConnectMcpAsync(_leadToken, ct);

    /// <summary>
    /// Connects to the plane's real MCP endpoint with <paramref name="bearer"/>. Used
    /// for the Lead, and for replaying a dead worker's token (§17.8: "replay a stale
    /// worker-instance token"), where the connection itself is expected to be refused.
    /// </summary>
    public Task<McpClient> ConnectMcpAsync(string bearer, CancellationToken ct) =>
        PlaneProbe.ConnectMcpAsync(new Uri(_planeUrl + "/"), bearer, ct);

    /// <summary>
    /// Whether the worker process for <paramref name="task"/> has actually started on the
    /// machine — the wedge/<c>run</c> harness writes an atomic <c>started</c> marker into
    /// its working directory (<c>{work_root}/{task}</c>) as its first act, so this is the
    /// process existing, not merely the plane having decided it should.
    ///
    /// <para>That is a strictly stronger fact than the task being committed <c>working</c>,
    /// and a scenario that kills something "mid-task" needs the stronger one. The store
    /// commits submitted→working and mints the worker instance <em>before</em> the
    /// DispatchCommand is sent (deliberately — a failed send then requeues a now-working
    /// task instead of losing it), so there is a real window in which the row says
    /// <c>working</c> while the command is still in flight and no worker exists anywhere.
    /// Killing a process inside that window tests a lost dispatch, which is a different
    /// scenario with a different correct outcome: the task has no live process, so the
    /// aliveness clock reclaims it as <c>LivenessTimeout</c> and it never was mid-task.</para>
    /// </summary>
    public bool WorkerStarted(SessionId task) =>
        File.Exists(Path.Combine(_workRoot, task.ToString(), "started"));

    /// <summary>
    /// The OS pid of the worker process currently running <paramref name="task"/>, from the
    /// marker the harness writes before its <c>started</c> one, or null until it exists.
    ///
    /// <para>The only handle a scenario has on "that process is gone" rather than merely "it
    /// stopped saying anything": a killed process writes no marker on its way out, and the
    /// task row records the plane's decision, not the machine's execution of it. Redispatch
    /// overwrites the marker in place — the work dir is per task, not per attempt — so a
    /// scenario that wants a particular attempt's pid reads it while that attempt is live.</para>
    /// </summary>
    public int? WorkerPid(SessionId task)
    {
        try
        {
            return int.TryParse(
                File.ReadAllText(Path.Combine(_workRoot, task.ToString(), "pid")), out var pid)
                ? pid
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // not written yet, or caught mid-rename; the caller polls again
        }
    }

    /// <summary>
    /// Dials the plane's real <c>/runner</c> WebSocket with the machine's own credential and
    /// hands back the socket, without sending anything on it (§10, §13: the token travels in
    /// the header, as <c>landbridged</c>'s own channel sends it).
    ///
    /// <para>This is the closest deterministic stand-in for the half-open socket §17.8's
    /// closed-laptop case produces. A genuinely half-open TCP connection needs packets
    /// dropped in the network — root-only and unportable — but what the PLANE sees is the
    /// thing under test: an accepted <c>/runner</c> connection, authenticated as this machine,
    /// that is registered and then never carries another byte. Sending no heartbeat is what
    /// makes it faithful as well as convenient: a stale connection reports nothing, so it
    /// never becomes ready and dispatch never considers it.</para>
    /// </summary>
    public async Task<ClientWebSocket> DialRunnerAsync(CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_machineToken}");
        await socket.ConnectAsync(new Uri(WsRunnerUrl(_planeUrl)), ct);
        Note("dialed a second /runner connection for the same machine");
        return socket;
    }

    /// <summary>
    /// The worker token landbridged injected for the CURRENT dispatch of
    /// <paramref name="task"/>, read out of the generated <c>mcp.json</c> in the task's
    /// work dir (§13) — the very token the live worker is authenticating with. Null
    /// until the file exists. Redispatch overwrites it in place, so a scenario that
    /// wants the PREDECESSOR's token has to read it before the requeue.
    /// </summary>
    public async Task<string?> InjectedWorkerTokenAsync(SessionId task, CancellationToken ct)
    {
        var path = Path.Combine(_workRoot, task.ToString(), "mcp.json");
        if (!File.Exists(path))
            return null;
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct));
            var server = root?["mcpServers"]?.AsObject()?.First().Value;
            var authorization = (string?)server?["headers"]?["Authorization"];
            return authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true
                ? authorization["Bearer ".Length..]
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null; // mid-write; the caller polls again
        }
    }

    // ── Bounded reads of committed control-plane state ──────────────────────────

    public async Task<SessionState?> StateAsync(SessionId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await PlaneProbe.StateAsync(db, task, ct);
    }

    public async Task<MessageState?> MessageStateAsync(SessionId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await PlaneProbe.MessageStateAsync(db, task, ct);
    }

    /// <summary>The committed row facts the scenarios assert on, in one read.</summary>
    public async Task<TaskFacts?> FactsAsync(SessionId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var row = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == task.Value, ct);
        if (row is null)
            return null;
        var liveInstances = await db.WorkerInstances.AsNoTracking()
            .CountAsync(w => w.SessionId == task.Value && !w.Revoked, ct);
        return new TaskFacts(
            row.State, row.Attempt, row.InfrastructureRequeues, row.CurrentInstanceId, row.ResultReference,
            liveInstances, row.LastRequeueReason, row.InfrastructureRequeueLimit);
    }

    /// <summary>
    /// Every requeue this task has taken, in order, with the reason each one carries
    /// (<c>task_events.liveness_reason</c>). Since #73 the reason is committed rather
    /// than living only in a log line, so a scenario can assert WHICH signal reclaimed a
    /// task from durable state — and, just as usefully, that the other signals did not
    /// fire. The event kind is the command's type name, so a requeue row is a
    /// <c>LivenessLost</c>.
    /// </summary>
    public async Task<IReadOnlyList<LivenessLossReason?>> RequeueReasonsAsync(
        SessionId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == task.Value && e.Kind == nameof(LivenessLost))
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.LivenessReason)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Bounded poll: true as soon as <paramref name="condition"/> holds, false at the
    /// deadline. The poll IS the synchronization — no scenario sleeps for a fixed duration
    /// and hopes. Kept as the name twelve scenarios already read; the implementation is
    /// <see cref="PlaneProbe.WaitUntilAsync"/>, shared with the multi-machine rig.
    /// </summary>
    public static Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct) =>
        PlaneProbe.WaitUntilAsync(condition, timeout, ct);

    public Task<bool> WaitForStateAsync(SessionId task, SessionState state, TimeSpan timeout, CancellationToken ct) =>
        WaitUntilAsync(async () => await StateAsync(task, ct) == state, timeout, ct);

    public Task<bool> WaitForReportAsync(SessionId task, TimeSpan timeout, CancellationToken ct) =>
        WaitUntilAsync(async () => (await FactsAsync(task, ct))?.ResultReference is { Length: > 0 }, timeout, ct);

    // ── Diagnostics ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything needed to debug a failure from the CI log alone: what the rig did and
    /// when, each process's retained output tail, and — per task — the committed row,
    /// its live worker instances, and its ordered control-plane event log. Every
    /// deadline in this suite routes its failure message through here.
    /// </summary>
    public async Task<string> DiagnoseAsync(IEnumerable<SessionId> tasks, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔═ CHAOS DIAGNOSTICS ═══════════════════════════════════════════");
        sb.AppendLine($"║ plane={_planeUrl} machine={MachineId} workRoot={_workRoot}");
        sb.AppendLine("╠═ timeline ════════════════════════════════════════════════════");
        lock (_timeline)
            foreach (var entry in _timeline)
                sb.AppendLine("║ " + entry);

        sb.AppendLine("╠═ tasks ═══════════════════════════════════════════════════════");
        await using (var db = pg.NewContext())
        {
            // The committed row, its worker instances, and the ordered event log, rendered
            // inside this rig's box (PlaneProbe, shared with the multi-machine rig).
            foreach (var task in tasks)
                await PlaneProbe.AppendTaskFactsAsync(sb, db, task, "║ ", ct);
        }

        sb.AppendLine("╠═ processes ═══════════════════════════════════════════════════");
        foreach (var process in new[] { _plane, _landbridged }.Concat(_strays).OfType<ChaosProcess>())
            sb.Append(Indent(process.Tail()));
        sb.AppendLine("╚═══════════════════════════════════════════════════════════════");
        return sb.ToString();
    }

    private static string Indent(string block) =>
        string.Concat(block.Split('\n').Select(l => l.Length == 0 ? "" : "║ " + l + "\n"));

    private void Note(string entry)
    {
        lock (_timeline)
            _timeline.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {entry}");
    }

    // ── Config + wiring ─────────────────────────────────────────────────────────

    /// <summary>
    /// landbridged's config (§10). Two profiles, both spawning REAL binaries by absolute
    /// path with argv only:
    /// <list type="bullet">
    /// <item><c>default</c> — <c>Landbridge.WorkerHarness</c>, which dials the plane with
    /// its injected token, calls <c>get_session</c>, reports a result and exits: a task
    /// that mails a report on its own.</item>
    /// <item><c>wedge</c> — <c>Landbridge.Runner.TestHarness run</c>, which writes a marker
    /// and then only watches stdin. It never speaks MCP, so it registers no service and
    /// makes no progress, while landbridged keeps emitting <c>alive</c> for it every
    /// heartbeat. That is precisely the wedged agent the two clocks exist to separate:
    /// aliveness stays fresh, so only the no-progress ceiling can reclaim it.</item>
    /// </list>
    /// <para><b>Only the wedge profile names <c>{mcp_config}</c>, and it must keep doing so
    /// even though its harness ignores every argument past <c>run</c>.</b> Since #112 G11
    /// landbridged writes the 0600 <c>mcp.json</c> only when the argv references it, so this is
    /// what puts a real worker-instance token on disk — the credential
    /// <see cref="InjectedWorkerTokenAsync"/> reads and the stale-token replay scenario
    /// (§17.8) presents again after a requeue. Drop it and that scenario stops finding a
    /// token to replay and fails on the fixture rather than on the behaviour.</para>
    ///
    /// <para>The <c>default</c> profile deliberately does <em>not</em>: a real ACP worker
    /// takes the plane's MCP server as a <c>session/new</c> parameter, so no config file is
    /// written and no live bearer token sits in the work dir for the length of the task.
    /// That asymmetry is the point — the fixture buys a credential on the one profile that
    /// runs no real agent, and the production path keeps the property.</para>
    /// The heartbeat is deliberately short — it drives both the <c>alive</c> cadence and
    /// the dispatch signal, and it must stay well inside the plane's aliveness window or
    /// a perfectly healthy task would be requeued.
    /// </summary>
    private string WriteLandbridgedConfig()
    {
        var config = new JsonObject
        {
            ["machine"] = new JsonObject
            {
                ["work_root"] = _workRoot,
                ["heartbeat_seconds"] = (int)options.HeartbeatInterval.TotalSeconds,
            },
            ["profiles"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "default",
                    // The worker harness as an ACP agent: it takes the plane's MCP server
                    // from session/new rather than a --mcp-config path, so the chaos fleet
                    // exercises the same protocol every other tier does.
                    ["spawn"] = new JsonArray(ChaosBinaries.WorkerHarness(), "--acp"),
                    ["prompt"] = "Do the task you have been dispatched.",
                },
                new JsonObject
                {
                    // The wedge profile spawns a harness mode that deliberately says nothing
                    // — that is the point of it — so it speaks no ACP either. A prompt is
                    // still required to load, and is simply never answered.
                    //
                    // It DOES still name {mcp_config}, and alone in this file. The harness
                    // ignores every argument past `run`, so the path is never read; what the
                    // reference does is make landbridged write the 0600 file (#112 G11 gates that
                    // write on the argv asking for it), which is the only way the stale-token
                    // replay scenario can get hold of a real worker-instance credential —
                    // tokens are hashed at rest, so the plane's own tables cannot give one
                    // back. A test fixture buying a credential it needs, on the one profile
                    // that runs no real agent.
                    ["name"] = ChaosProfiles.Wedge,
                    ["spawn"] = new JsonArray(
                        ChaosBinaries.RunnerTestHarness(), "run", "--mcp-config", "{mcp_config}"),
                    ["prompt"] = "Do the task you have been dispatched.",
                }),
        };
        var path = Path.Combine(NewTempDir("config"), "landbridged.json");
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>http→ws on the plane's own base, the §10 runner path.</summary>
    private static string WsRunnerUrl(string httpBase) =>
        "ws" + httpBase["http".Length..].TrimEnd('/') + "/runner";

    /// <summary>TimeSpan config must be written out in full: a bare number parses as DAYS.</summary>
    private static string Fmt(TimeSpan value) => value.ToString(@"hh\:mm\:ss\.fff");

    private static string NewTempDir(string kind)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "landbridge-chaos-tests", Guid.NewGuid().ToString("N"), kind);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async ValueTask DisposeAsync()
    {
        // Order matters: landbridged first, so it tears down its own workers before the
        // plane goes away and starts logging requeues at a stopping host.
        _landbridged?.Dispose();
        foreach (var stray in _strays)
            stray.Dispose();
        _plane?.Dispose();
        _http?.Dispose();
        await Task.CompletedTask;

        foreach (var dir in new[] { _workRoot, _stateDir })
            if (dir is not null)
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>The tunable windows a scenario needs; see <see cref="ChaosFleet"/>.</summary>
internal sealed record ChaosFleetOptions
{
    /// <summary>
    /// landbridged's heartbeat cadence — the <c>alive</c> cadence and the dispatch signal.
    /// Must stay comfortably inside <see cref="PerTaskLivenessWindow"/>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The §10 aliveness window, and also the liveness sweep's PERIOD. Kept several
    /// heartbeats wide so a healthy task is never requeued, but short enough that the
    /// sweep runs often.
    ///
    /// <para><b>It is pulled in two directions, so it is left tight here and widened
    /// per scenario.</b> Too tight and it does not cover the COLD START: the clock
    /// starts on the plane in <c>RunnerConnectionRegistry.TrackDispatch</c>, just before
    /// the send, so it is already running while the command crosses the socket, landbridged
    /// writes <c>files[]</c>, and <c>Process.Start</c> runs — and no heartbeat can carry
    /// <c>alive</c> until that process exists. Too wide and it stops being the FAST
    /// RETRY that a scenario asserting real work inside <c>TransitionBudget</c> leans on:
    /// a dispatch that stalls on a loaded runner is only rescued by this sweep, and at
    /// 30s the rescue lands outside a 45s budget.</para>
    ///
    /// <para>Both failure modes have been seen in CI, one from each direction. So the
    /// default stays short, and a scenario that needs cold-start headroom asks for it —
    /// see the stale-token scenario, which is about token replay and not about this
    /// clock at all.</para>
    /// </summary>
    public TimeSpan PerTaskLivenessWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The §10 no-progress ceiling (30 minutes in production). Only a scenario about a
    /// wedged agent shrinks this; the others leave it long enough to stay out of the way.
    /// </summary>
    public TimeSpan NoProgressCeiling { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Hard deadline for any one process to come up.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>Runner profile names this suite declares in landbridged's config.</summary>
internal static class ChaosProfiles
{
    /// <summary>A worker that stays alive and makes no progress; see the config comment.</summary>
    public const string Wedge = "wedge";
}

/// <summary>A planted stray and the grandchild that inherited its tag.</summary>
internal readonly record struct StrayTree(int Pid, int GrandchildPid)
{
    public bool AnyAlive => ChaosProcess.PidAlive(Pid) || ChaosProcess.PidAlive(GrandchildPid);
}

/// <summary>The committed task facts the scenarios assert on.</summary>
internal readonly record struct TaskFacts(
    SessionState State,
    int Attempt,
    int InfrastructureRequeues,
    Guid? CurrentInstanceId,
    string? ResultReference,
    int LiveInstanceCount,
    LivenessLossReason? LastRequeueReason,
    int InfrastructureRequeueLimit);
