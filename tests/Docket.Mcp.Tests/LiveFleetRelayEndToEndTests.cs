using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The live-fleet crown of increment 4 (spec §8.3): the relay proven through a
/// standing fleet with <b>no fakes on any surface that matters</b>. A real control
/// plane (grants + registry + orchestrator + event sink), a real relay validating
/// grants against that plane, and the real <see cref="DispatchService"/> +
/// <see cref="ProcessSupervisor"/> spawning the <b>real</b>
/// <see cref="Docket.WorkerHarness"/> process <em>twice</em> — once as a producer,
/// once as a consumer — off a single <c>default</c> profile. The two workers are
/// steered entirely by their task's opaque prose description (§7): a
/// <c>relay-serve:echo</c> task makes its worker bind a loopback echo service and
/// advertise it via <c>register_service</c>; a <c>relay-consume:echo</c> task makes
/// its worker <c>open_forward</c> that service, connect to the returned loopback
/// address, and prove a byte round-trip through the real relay before reporting
/// <c>relay-echo:ok:&lt;bytes&gt;</c>.
///
/// <para>The forward's two docketd data planes are real <see cref="RunnerDaemon"/>s
/// (via <see cref="DaemonHarness"/>, reused from the increment-3 crown): the producer
/// task's machine handles the producer <c>open-forward</c> (dialing the echo service),
/// the consumer task's machine handles the consumer one (binding the loopback the
/// worker connects to). Each machine's registry send delegate routes
/// <see cref="DispatchCommand"/> → the supervisor's real spawn and
/// <see cref="OpenForwardCommand"/> → that machine's real daemon, whose ring drains
/// forward-opened/-closed back into the plane's <see cref="RunnerEventSink"/> so the
/// orchestrator's waiter completes — exactly the §10 socket seam a production
/// docketd would occupy. Producer and consumer land on <em>different</em> machines
/// (the relay is cross-machine, and one <see cref="RelayForwarder"/> dedups a
/// forward id) by dispatching them one at a time with only the intended machine ready.</para>
///
/// <para>Finally, killing the producer's worker and driving the plane's liveness
/// loss shows the bookkeeping: leaving <c>working</c> fires
/// <c>ClearServicesAndForwards</c>, so the service row is gone.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LiveFleetRelayEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Bearer = "live-fleet-relay-shared-secret-under-test";
    private const string ServiceName = "echo";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Fleet_serves_and_forwards_echo_over_the_real_relay_then_clears_on_producer_exit()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // ── Plane + relay on a pre-reserved relay URL (no build-order race) ─────
        var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer, relayUrl);
        await plane.StartAsync(ct);
        var baseUrl = plane.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));
        await using var relay = RelayGrantTestKit.BuildRelay(
            RelayGrantTestKit.BaseUri(plane).ToString(), Bearer, listenUrl: relayUrl);
        await relay.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = plane.Services.GetRequiredService<RunnerEventSink>();

        // ── Two machines, each a real docketd data plane for its end of a forward.
        //    "mp" hosts the producer task, "mc" the consumer. Separate daemons →
        //    separate RelayForwarders, so the shared forward id is not deduped away.
        await using var producerDaemon = new DaemonHarness("mp", new SinkForwardingChannel(sink));
        await using var consumerDaemon = new DaemonHarness("mc", new SinkForwardingChannel(sink));
        await producerDaemon.StartAsync();
        await consumerDaemon.StartAsync();

        // ── One real supervisor spawns both workers (keyed by task id), running the
        //    real harness with the injected --mcp-config path (§13), like the skeleton.
        var workRoot = NewWorkRoot();
        var ring = new OutboundEventRing(capacity: 256);
        var supervisor = new ProcessSupervisor(
            new MachineConfig(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default),
            ring, TimeProvider.System);
        var profile = new ProfileConfig(
            "default",
            [WorkerHarnessPath(), "--mcp-config", "{mcp_config}"],
            new StopConfig(StopMode.Signal, Signal: null, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(30)),
            Resume: null,
            new EventsConfig(EventsSource.None, new Dictionary<string, string>()),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(Path: null, Format: null),
            MaxConcurrent: null);

        try
        {
            // The registry seam a socket would occupy (§10): each machine routes
            // DispatchCommand → the real supervisor spawn, OpenForwardCommand → the
            // real daemon standing up its relay data plane.
            registry.Register("mp", new HashSet<string> { "default" }, (command, sendCt) => command switch
            {
                DispatchCommand d => Spawn(supervisor, d, profile, "mp"),
                OpenForwardCommand => producerDaemon.Send(command, sendCt),
                _ => Task.CompletedTask,
            });
            registry.Register("mc", new HashSet<string> { "default" }, (command, sendCt) => command switch
            {
                DispatchCommand d => Spawn(supervisor, d, profile, "mc"),
                OpenForwardCommand => consumerDaemon.Send(command, sendCt),
                _ => Task.CompletedTask,
            });

            var dispatch = new DispatchService(
                plane.Services.GetRequiredService<IServiceScopeFactory>(),
                registry, TimeProvider.System, NullLogger<DispatchService>.Instance,
                publicMcpUrl: baseUrl);

            var team = TeamId.New();
            var leadToken = await RelayGrantTestKit.LeadTokenAsync(pg, team, ct);

            // ── Producer: create + dispatch task A onto "mp" (the only ready machine,
            //    and the only submitted task), then wait until its worker has bound
            //    the echo service and registered it.
            var taskA = await CreateTaskAsync(baseUrl, leadToken, $"relay-serve:{ServiceName}", ct);
            SetReady(registry, "mp", ready: true);
            SetReady(registry, "mc", ready: false);
            await dispatch.RunDispatchPassAsync(ct);

            var registered = await WaitUntilAsync(() => ServiceExistsAsync(team, ServiceName, ct), TimeSpan.FromSeconds(60));
            if (!registered)
                Assert.Fail("producer worker never registered the echo service. " + await DiagnoseAsync(workRoot, taskA, ct));

            // ── Consumer: create + dispatch task B onto "mc" (now the only ready
            //    machine, and A is working so B is the only submitted task).
            var taskB = await CreateTaskAsync(baseUrl, leadToken, $"relay-consume:{ServiceName}", ct);
            SetReady(registry, "mp", ready: false);
            SetReady(registry, "mc", ready: true);
            await dispatch.RunDispatchPassAsync(ct);

            // ── The consumer worker opened the forward, round-tripped bytes through
            //    the real relay, and reported — driving working → verifying.
            var reached = await WaitUntilAsync(
                async () => await StateAsync(taskB, ct) == TaskState.Verifying, TimeSpan.FromSeconds(60));
            if (!reached)
                Assert.Fail("consumer worker never drove its task to verifying. " + await DiagnoseAsync(workRoot, taskB, ct));

            // The result reference is the worker's own proof the bytes round-tripped.
            string? reference;
            await using (var v = pg.NewContext())
                reference = (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == taskB.Value, ct)).ResultReference;
            Assert.NotNull(reference);
            Assert.StartsWith("relay-echo:ok:", reference);

            // ── Bookkeeping (§6/§8.3): the producer leaving working clears its
            //    registered services. Kill its worker, drive the plane's liveness
            //    loss, and the service row is gone.
            Assert.True(supervisor.Kill(taskA), "producer worker was not running to kill");
            await using (var db = pg.NewContext())
            {
                var store = new TaskStore(db, TimeProvider.System);
                Assert.IsType<StoreResult.Applied>(
                    await store.ApplyAsync(taskA, new LivenessLost(LivenessLossReason.MachineReboot), ct));
            }
            Assert.False(await ServiceExistsAsync(team, ServiceName, ct),
                "registered_services row for the echo service was not cleared when the producer left working");
        }
        finally
        {
            supervisor.KillAll();
            await producerDaemon.StopAsync();
            await consumerDaemon.StopAsync();
            await relay.StopAsync(ct);
            await plane.StopAsync(ct);
            TryDeleteRoot(workRoot);
        }
    }

    // ── Seam helpers ────────────────────────────────────────────────────────────

    /// <summary>Spawn on a background-safe path: the supervisor's spawn is synchronous.</summary>
    private static Task Spawn(ProcessSupervisor supervisor, DispatchCommand dispatch, ProfileConfig profile, string machineId)
    {
        supervisor.Spawn(dispatch, profile, machineId);
        return Task.CompletedTask;
    }

    private static void SetReady(RunnerConnectionRegistry registry, string machineId, bool ready) =>
        registry.ApplyHeartbeat(machineId, new MachineHeartbeat(
            machineId, Ready: ready, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningTasks: 0, ["default"], DateTimeOffset.UtcNow));

    private async Task<TaskId> CreateTaskAsync(string baseUrl, string leadToken, string description, CancellationToken ct)
    {
        await using var lead = await RelayGrantTestKit.ConnectMcpAsync(new Uri(baseUrl + "/"), leadToken, ct);
        var created = await lead.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["description"] = description,
            ["completionCriteria"] = "the byte path holds",
            ["mode"] = "lead",
            ["profile"] = null,
            ["workspace"] = "relay-fleet-e2e",
        }, cancellationToken: ct);
        Assert.NotEqual(true, created.IsError);
        return new TaskId(Guid.Parse(Assert.Single(created.Content.OfType<TextContentBlock>()).Text));
    }

    private async Task<bool> ServiceExistsAsync(TeamId team, string name, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await db.RegisteredServices.AsNoTracking()
            .AnyAsync(s => s.TeamId == team.Value && s.Name == name, ct);
    }

    private async Task<TaskState?> StateAsync(TaskId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return await new TaskStore(db, TimeProvider.System).GetStateAsync(id, ct);
    }

    /// <summary>The harness diagnostic for a task's work dir, if it left one.</summary>
    private static async Task<string> DiagnoseAsync(string workRoot, TaskId task, CancellationToken ct)
    {
        var errPath = System.IO.Path.Combine(workRoot, task.ToString(), "harness_error.txt");
        return System.IO.File.Exists(errPath)
            ? "Harness diagnostic:\n" + await System.IO.File.ReadAllTextAsync(errPath, ct)
            : "(no harness_error.txt)";
    }

    /// <summary>
    /// The built <see cref="Docket.WorkerHarness"/> apphost, resolved from its own
    /// bin (not the copy beside this test) — its MCP-client closure is copied local
    /// only there, so the copy beside the test cannot start. Mirrors the skeleton E2E.
    /// </summary>
    private static string WorkerHarnessPath()
    {
        const string stem = "Docket.WorkerHarness";
        var testDir = System.IO.Path.GetDirectoryName(typeof(LiveFleetRelayEndToEndTests).Assembly.Location)!;
        var harnessDir = testDir.Replace(
            System.IO.Path.Combine("Docket.Mcp.Tests", "bin"),
            System.IO.Path.Combine(stem, "bin"),
            StringComparison.Ordinal);
        var apphost = System.IO.Path.Combine(harnessDir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        return System.IO.File.Exists(apphost)
            ? apphost
            : throw new System.IO.FileNotFoundException(
                $"worker harness apphost not found at {apphost}; is Docket.WorkerHarness built?");
    }

    private static string NewWorkRoot()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docket-live-fleet-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteRoot(string dir)
    {
        try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
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
}
