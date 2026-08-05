using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Chaos.Tests;

/// <summary>
/// The §17.8 chaos rig: a real control plane and a real <c>docketd</c> as separate
/// OS processes over a real Postgres, with real worker binaries underneath. Nothing
/// here is a seam — where <see cref="Docket.MultiMachine.Tests"/>' FleetRig stands in
/// for the §10 socket with an in-process delegate, this dials the actual
/// <c>/runner</c> WebSocket, so the processes can be SIGKILLed and restarted the way
/// §17.8 asks ("kill a runner mid-task", "SIGKILL docketd and restart it").
///
/// <para>Three deliberate choices make the scenarios deterministic and bounded:</para>
/// <list type="bullet">
/// <item><b>Ephemeral ports.</b> Both the plane's URL and every reservation come from
/// an OS-assigned port, so parallel CI legs cannot collide.</item>
/// <item><b>Shrunk liveness windows.</b> The plane's aliveness / no-progress clocks
/// and docketd's heartbeat cadence are configuration (§10), so a scenario that would
/// otherwise wait 30 real minutes for the no-progress ceiling waits seconds instead.
/// No test here sleeps for a fixed duration as a synchronization device: every wait
/// is a bounded poll of committed control-plane state with a hard deadline.</item>
/// <item><b>Secrets off argv.</b> The machine token reaches docketd through the
/// environment and the worker token through the injected <c>mcp.json</c>, never as an
/// argument (§13).</item>
/// </list>
/// </summary>
internal sealed class ChaosFleet(PostgresFixture pg, ChaosFleetOptions options) : IAsyncDisposable
{
    private readonly List<ChaosProcess> _strays = new();
    private readonly List<string> _timeline = new();

    private ChaosProcess? _plane;
    private ChaosProcess? _docketd;
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
    public ChaosProcess Docketd => _docketd ?? throw new InvalidOperationException("docketd is not running");

    // ── Bring-up ────────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _workRoot = NewTempDir("work");
        // §13/§10: docketd's own state dir must be a temp path. On a box that has ever
        // run `docketd --enroll`, a real ~/.docket/credentials.json would override the
        // machine id we hand it — and point it at a real control plane.
        _stateDir = NewTempDir("state");

        // Mint the machine identity and the Lead credential directly against the
        // store, the idiom the existing suites use (RunnerSpineEndToEndTests for the
        // machine, MultiMachineKit.LeadTokenAsync for the Lead).
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var enrollment = await tokens.IssueEnrollmentTokenAsync(ct);
            var credentials = await tokens.ExchangeEnrollmentAsync(
                enrollment.Token,
                new MachineDeclaration("chaos-box", "spec §17.8 chaos suite", "linux", "standard"),
                ct)
                ?? throw new InvalidOperationException("chaos rig: enrollment exchange returned null");
            MachineId = credentials.MachineId.ToString();
            _machineToken = credentials.Access.Token;

            Team = TeamId.New();
            var human = await tokens.IssueHumanSessionAsync(ct);
            var claim = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, Team, ct: ct);
            _leadToken = claim.Token.Token;
        }

        _planeUrl = ReserveLoopbackUrl();
        await StartPlaneAsync(ct);

        _configPath = WriteDocketdConfig();
        await StartDocketdAsync(ct);
        Note($"fleet up: plane={_planeUrl} machine={MachineId}");
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
            ["ConnectionStrings__Docket"] = pg.ConnectionString,
            // The URL the plane writes into every worker's injected mcp.json. Without
            // this the workers would dial the 127.0.0.1:5000 default and never find us.
            ["Docket__PublicMcpUrl"] = _planeUrl,
            // The PostgresFixture already migrated; a second migrate would only race.
            ["Docket__MigrateOnStartup"] = "false",
            // §10 two-clock liveness, shrunk. Note PerTaskLivenessWindow doubles as the
            // sweep PERIOD, and that these parse as TimeSpan — a bare number would mean
            // DAYS, so they are always written out in full.
            ["Docket__PerTaskLivenessWindow"] = Fmt(options.PerTaskLivenessWindow),
            ["Docket__NoProgressCeiling"] = Fmt(options.NoProgressCeiling),
            // Keep Docket's own categories verbose — the liveness warning is the only
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
    /// Starts docketd and waits for its <c>docketd up:</c> announcement — which §10
    /// guarantees is printed only AFTER the restart sweep has run, so seeing the line
    /// is what makes "the sweep completed before this daemon accepted any dispatch"
    /// an observable fact. Returns the announcement line.
    /// </summary>
    public async Task<string> StartDocketdAsync(CancellationToken ct)
    {
        _docketd = ChaosProcess.Start("docketd", ChaosBinaries.Docketd(),
            ["--config", _configPath, "--state-dir", _stateDir],
            new Dictionary<string, string>
            {
                ["DOCKET_CONTROL_URL"] = WsRunnerUrl(_planeUrl),
                // §13: the machine token travels in the environment, never in argv.
                ["DOCKET_MACHINE_TOKEN"] = _machineToken,
                ["DOCKET_MACHINE_ID"] = MachineId,
            });

        var up = await _docketd.WaitForLineAsync(l => l.Contains("docketd up:", StringComparison.Ordinal),
            options.StartupTimeout);
        if (up is null)
            throw new TimeoutException(
                $"docketd never announced itself within {options.StartupTimeout}:\n" + _docketd.Tail());
        Note("docketd: " + up.Trim());
        return up;
    }

    /// <summary>
    /// Waits for a line on the plane's own log, up to <paramref name="timeout"/>.
    /// Which liveness clock reclaimed a task is deliberately NOT persisted (§10
    /// follow-up: <c>LivenessLossReason</c> is dropped on the way to the row), so the
    /// plane's warning is the only place that fact is recoverable — which makes reading
    /// it the only way a test can tell the two clocks apart.
    /// </summary>
    public Task<string?> WaitForPlaneLogAsync(Func<string, bool> match, TimeSpan timeout) =>
        (_plane ?? throw new InvalidOperationException("the plane is not running")).WaitForLineAsync(match, timeout);

    /// <summary>
    /// SIGKILL docketd — no handler, no flush, no child cleanup (§17.8). The plane
    /// notices the dropped socket and requeues everything the machine held; asserting
    /// that is the caller's job.
    /// </summary>
    public async Task SigkillDocketdAsync()
    {
        var victim = _docketd ?? throw new InvalidOperationException("docketd is not running");
        Note($"SIGKILL docketd pid={victim.Id}");
        victim.Sigkill();
        await victim.WaitForExitAsync(TimeSpan.FromSeconds(15));
        victim.Dispose();
        _docketd = null;
    }

    /// <summary>SIGKILL the plane, then bring an identically-configured one back up.</summary>
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
    /// Plants a tagged process tree that will OUTLIVE docketd, standing in for the
    /// escaped grandchild §10 describes (a dev server that <c>setsid</c>ed out of the
    /// task's process group and still holds its port). Two mechanisms have to be
    /// defeated for it to survive, exactly as <c>DocketdStrayReapEndToEndTests</c>
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
                // docketd would kill the test runner itself.
                ["DOCKET_MACHINE_ID"] = MachineId,
                ["DOCKET_TASK_ID"] = Guid.NewGuid().ToString(),
                ["DOCKET_TEST_DISABLE_PDEATHSIG"] = "1",
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
    /// drives a task to <c>verifying</c>, <see cref="ChaosProfiles.Wedge"/> is the
    /// worker that emits nothing and reports nothing.
    /// </summary>
    public async Task<TaskId> CreateTaskAsync(string description, string? profile, CancellationToken ct)
    {
        await using var lead = await ConnectLeadAsync(ct);
        var created = await lead.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["description"] = description,
            ["completionCriteria"] = "the chaos scenario holds",
            ["mode"] = "lead",
            ["profile"] = profile,
            ["workspace"] = $"chaos-{Guid.NewGuid():N}",
        }, cancellationToken: ct);
        Assert.NotEqual(true, created.IsError);
        var task = new TaskId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
        Note($"created task {task} profile={profile ?? "default"}");
        return task;
    }

    /// <summary>Accept a verifying task as the Lead, driving it to <c>completed</c> (§7).</summary>
    public async Task AcceptAsync(TaskId task, CancellationToken ct)
    {
        await using var lead = await ConnectLeadAsync(ct);
        var verdict = await lead.CallToolAsync("submit_review", new Dictionary<string, object?>
        {
            ["taskId"] = task.Value.ToString(),
            ["verdict"] = "accept",
        }, cancellationToken: ct);
        Assert.NotEqual(true, verdict.IsError);
    }

    private Task<McpClient> ConnectLeadAsync(CancellationToken ct) => ConnectMcpAsync(_leadToken, ct);

    /// <summary>
    /// Connects to the plane's real MCP endpoint with <paramref name="bearer"/>. Used
    /// for the Lead, and for replaying a dead worker's token (§17.8: "replay a stale
    /// worker-instance token"), where the connection itself is expected to be refused.
    /// </summary>
    public async Task<McpClient> ConnectMcpAsync(string bearer, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_planeUrl + "/"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    /// <summary>
    /// The worker token docketd injected for the CURRENT dispatch of
    /// <paramref name="task"/>, read out of the generated <c>mcp.json</c> in the task's
    /// work dir (§13) — the very token the live worker is authenticating with. Null
    /// until the file exists. Redispatch overwrites it in place, so a scenario that
    /// wants the PREDECESSOR's token has to read it before the requeue.
    /// </summary>
    public async Task<string?> InjectedWorkerTokenAsync(TaskId task, CancellationToken ct)
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

    public async Task<TaskState?> StateAsync(TaskId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await new TaskStore(db, TimeProvider.System).GetStateAsync(task, ct);
    }

    /// <summary>The committed row facts the scenarios assert on, in one read.</summary>
    public async Task<TaskFacts?> FactsAsync(TaskId task, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var row = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == task.Value, ct);
        if (row is null)
            return null;
        var liveInstances = await db.WorkerInstances.AsNoTracking()
            .CountAsync(w => w.TaskId == task.Value && !w.Revoked, ct);
        return new TaskFacts(
            row.State, row.Attempt, row.InfrastructureRequeues, row.CurrentInstanceId, row.ResultReference,
            liveInstances);
    }

    /// <summary>
    /// Bounded poll: true as soon as <paramref name="condition"/> holds, false at the
    /// deadline. The poll IS the synchronization — no scenario sleeps for a fixed
    /// duration and hopes.
    /// </summary>
    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition())
                return true;
            await Task.Delay(100, ct);
        }
        return await condition();
    }

    public Task<bool> WaitForStateAsync(TaskId task, TaskState state, TimeSpan timeout, CancellationToken ct) =>
        WaitUntilAsync(async () => await StateAsync(task, ct) == state, timeout, ct);

    // ── Diagnostics ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything needed to debug a failure from the CI log alone: what the rig did and
    /// when, each process's retained output tail, and — per task — the committed row,
    /// its live worker instances, and its ordered control-plane event log. Every
    /// deadline in this suite routes its failure message through here.
    /// </summary>
    public async Task<string> DiagnoseAsync(IEnumerable<TaskId> tasks, CancellationToken ct)
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
            foreach (var task in tasks)
            {
                var row = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == task.Value, ct);
                if (row is null)
                {
                    sb.AppendLine($"║ {task}: (no row)");
                    continue;
                }
                sb.AppendLine(
                    $"║ {task}: state={row.State} attempt={row.Attempt} " +
                    $"infraRequeues={row.InfrastructureRequeues} verificationFailures={row.VerificationFailures} " +
                    $"profile={row.Profile ?? "(default)"} result={row.ResultReference ?? "(none)"} " +
                    $"instance={row.CurrentInstanceId?.ToString() ?? "(none)"}");

                var instances = await db.WorkerInstances.AsNoTracking()
                    .Where(w => w.TaskId == task.Value).ToListAsync(ct);
                sb.AppendLine($"║   instances: " + (instances.Count == 0
                    ? "(none)"
                    : string.Join(", ", instances.Select(i => $"{i.Id.ToString()[..8]}{(i.Revoked ? " revoked" : " LIVE")}"))));

                var events = await db.TaskEvents.AsNoTracking()
                    .Where(e => e.TaskId == task.Value).OrderBy(e => e.OccurredAt).ToListAsync(ct);
                foreach (var e in events)
                    sb.AppendLine(
                        $"║   {e.OccurredAt:HH:mm:ss.fff} {e.Kind} " +
                        $"{e.FromState?.ToString() ?? "-"}->{e.ToState?.ToString() ?? "-"}" +
                        (e.Detail is { Length: > 0 } d ? $" [{d}]" : ""));
            }
        }

        sb.AppendLine("╠═ processes ═══════════════════════════════════════════════════");
        foreach (var process in new[] { _plane, _docketd }.Concat(_strays).OfType<ChaosProcess>())
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
    /// docketd's config (§10). Two profiles, both spawning REAL binaries by absolute
    /// path with argv only:
    /// <list type="bullet">
    /// <item><c>default</c> — <c>Docket.WorkerHarness</c>, which dials the plane with
    /// its injected token, calls <c>get_task</c>, reports a result and exits: a task
    /// that reaches <c>verifying</c> on its own.</item>
    /// <item><c>wedge</c> — <c>Docket.Runner.TestHarness run</c>, which writes a marker
    /// and then only watches stdin. It never speaks MCP, so it registers no service and
    /// makes no progress, while docketd keeps emitting <c>alive</c> for it every
    /// heartbeat. That is precisely the wedged agent the two clocks exist to separate:
    /// aliveness stays fresh, so only the no-progress ceiling can reclaim it.</item>
    /// </list>
    /// The heartbeat is deliberately short — it drives both the <c>alive</c> cadence and
    /// the dispatch signal, and it must stay well inside the plane's aliveness window or
    /// a perfectly healthy task would be requeued.
    /// </summary>
    private string WriteDocketdConfig()
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
                    ["spawn"] = new JsonArray(
                        ChaosBinaries.WorkerHarness(), "--mcp-config", "{mcp_config}"),
                },
                new JsonObject
                {
                    ["name"] = ChaosProfiles.Wedge,
                    ["spawn"] = new JsonArray(ChaosBinaries.RunnerTestHarness(), "run"),
                }),
        };
        var path = Path.Combine(NewTempDir("config"), "docketd.json");
        File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>http→ws on the plane's own base, the §10 runner path.</summary>
    private static string WsRunnerUrl(string httpBase) =>
        "ws" + httpBase["http".Length..].TrimEnd('/') + "/runner";

    /// <summary>
    /// An OS-assigned loopback port, reserved by binding and immediately releasing it
    /// (the MultiMachineKit idiom) — the plane must be told its own public URL before it
    /// starts, because that URL is what it writes into every worker's mcp.json.
    /// </summary>
    private static string ReserveLoopbackUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    /// <summary>TimeSpan config must be written out in full: a bare number parses as DAYS.</summary>
    private static string Fmt(TimeSpan value) => value.ToString(@"hh\:mm\:ss\.fff");

    private static string NewTempDir(string kind)
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "docket-chaos-tests", Guid.NewGuid().ToString("N"), kind);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async ValueTask DisposeAsync()
    {
        // Order matters: docketd first, so it tears down its own workers before the
        // plane goes away and starts logging requeues at a stopping host.
        _docketd?.Dispose();
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
    /// docketd's heartbeat cadence — the <c>alive</c> cadence and the dispatch signal.
    /// Must stay comfortably inside <see cref="PerTaskLivenessWindow"/>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The §10 aliveness window, and also the liveness sweep's PERIOD. Kept several
    /// heartbeats wide so a healthy task is never requeued, but short enough that the
    /// sweep runs often.
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

/// <summary>Runner profile names this suite declares in docketd's config.</summary>
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
    TaskState State,
    int Attempt,
    int InfrastructureRequeues,
    Guid? CurrentInstanceId,
    string? ResultReference,
    int LiveInstanceCount);
