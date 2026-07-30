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
            new LogsConfig(Path: null, Format: null),
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
        var supervisor = new ProcessSupervisor(
            new MachineConfig(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default),
            ring, TimeProvider.System);
        var daemon = new DaemonHarness(machineId, new SinkForwardingChannel(_sink));
        await daemon.StartAsync();

        var machine = new MachineRig(machineId, supervisor, workRoot, daemon);
        _machines[machineId] = machine;

        // The §10 socket seam: dispatch → the real spawn, open-forward → the real
        // daemon standing up the relay data plane. Nothing else is exercised here.
        _registry.Register(machineId, new HashSet<string> { "default" }, (command, sendCt) => command switch
        {
            DispatchCommand d => Spawn(machine, d),
            OpenForwardCommand => machine.Daemon.Send(command, sendCt),
            _ => Task.CompletedTask,
        });
    }

    private Task Spawn(MachineRig machine, DispatchCommand dispatch)
    {
        machine.Supervisor.Spawn(dispatch, _profile, machine.Id);
        return Task.CompletedTask;
    }

    /// <summary>Create a task for this fleet's Team via the real Lead MCP surface.</summary>
    public async Task<TaskId> CreateTaskAsync(string description, CancellationToken ct)
    {
        await using var lead = await MultiMachineKit.ConnectMcpAsync(new Uri(_baseUrl + "/"), _leadToken, ct);
        var created = await lead.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["description"] = description,
            ["completionCriteria"] = "the byte path holds",
            ["mode"] = "automated",
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

    private sealed record MachineRig(string Id, ProcessSupervisor Supervisor, string WorkRoot, DaemonHarness Daemon);
}
