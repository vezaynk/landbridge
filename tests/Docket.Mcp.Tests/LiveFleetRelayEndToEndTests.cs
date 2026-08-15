using System.Net;
using System.Net.Sockets;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
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
            [WorkerHarnessPath(), "--acp"],
            new StopConfig(StopMode.Signal, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(30)),
            Resume: null,
            new EventsConfig(EventsSource.None, new Dictionary<string, string>()),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(),
            MaxConcurrent: null,
            Protocol: ProtocolMode.Acp,
            Prompt: "Do the task.",
            FollowUp: "There is new input on your assignment. Read it, then continue.");

        try
        {
            // The registry seam a socket would occupy (§10): each machine routes
            // DispatchCommand → the real supervisor spawn, OpenForwardCommand → the
            // real daemon standing up its relay data plane.
            registry.Register("mp", new HashSet<string> { "default" }, (command, sendCt) => command switch
            {
                DispatchCommand d => Spawn(supervisor, d, profile, "mp"),
                // Both halves of a forward's life go to the daemon: open-forward stands the
                // data plane up, close-forward ends it when the owning task leaves working.
                OpenForwardCommand or CloseForwardCommand => producerDaemon.Send(command, sendCt),
                _ => Task.CompletedTask,
            });
            registry.Register("mc", new HashSet<string> { "default" }, (command, sendCt) => command switch
            {
                DispatchCommand d => Spawn(supervisor, d, profile, "mc"),
                OpenForwardCommand or CloseForwardCommand => consumerDaemon.Send(command, sendCt),
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

            // ── §8.3's second bound, through the whole real stack: "an established
            //    splice persists UNTIL the owning task leaves working."
            //
            //    The consumer end above belonged to a worker that has since exited, so the
            //    test takes one for itself: a real grant, both real docketd ends armed by the
            //    real orchestrator, and a TCP client the test holds OPEN across the
            //    producer's transition. Deliberately driven by report_result and NOT by a
            //    liveness loss, because a liveness loss tree-kills the worker (§10, #84) — the
            //    echo service dies with it and the splice would end by accident, which is
            //    exactly how this gap stayed invisible. Here the producer worker and its echo
            //    service stay up, so nothing but close-forward can end this connection.
            var instanceB = await IncumbentInstanceAsync(taskB, ct);
            RelayGrantResult.Issued issued;
            await using (var scope = plane.Services.CreateAsyncScope())
            {
                issued = Assert.IsType<RelayGrantResult.Issued>(
                    await scope.ServiceProvider.GetRequiredService<RelayGrantService>()
                        .IssueAsync(new WorkerCaller(team, taskB, instanceB), ServiceName, ct));
            }
            Assert.Equal(taskA, issued.Producer);

            var established = Assert.IsType<ForwardEstablishResult.Established>(
                await plane.Services.GetRequiredService<ForwardOrchestrator>().EstablishAsync(
                    new WorkerCaller(team, taskB, instanceB), issued, ServiceName, relayUrl, ct));

            using var held = new TcpClient();
            await held.ConnectAsync(IPAddress.Loopback, established.Port, ct);
            await using var heldStream = held.GetStream();

            // Live first: the bytes go consumer machine → relay → producer machine → the
            // worker's echo service and back. Everything after this is about a splice that
            // was demonstrably working.
            var probe = System.Text.Encoding.UTF8.GetBytes("splice-teardown-probe\n");
            await heldStream.WriteAsync(probe, ct);
            var echoed = await ReadExactlyAsync(heldStream, probe.Length, ct);
            Assert.True(probe.AsSpan().SequenceEqual(echoed), "the held forward never round-tripped bytes");

            // The producer reports and leaves working, through the plane's OWN store — so the
            // §8.3 teardown is wired exactly as a production host wires it.
            var instanceA = await IncumbentInstanceAsync(taskA, ct);
            await using (var scope = plane.Services.CreateAsyncScope())
            {
                Assert.IsType<StoreResult.Applied>(
                    await scope.ServiceProvider.GetRequiredService<TaskStore>().ApplyAsync(
                        taskA, new ReportResult(new WorkerCaller(team, taskA, instanceA), "served"), ct));
            }

            // Bookkeeping (§6/§8.3): the registration is gone…
            Assert.False(await ServiceExistsAsync(team, ServiceName, ct),
                "registered_services row for the echo service was not cleared when the producer left working");
            // …and so is the live connection through it, with the producer's worker — and the
            // echo service it started — still running, which is the whole point.
            Assert.True(await ConnectionIsDeadAsync(heldStream, ct),
                "the established splice outlived the task that authorized it (§8.3)");
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

    /// <summary>
    /// A task's incumbent worker instance, read off the row — the tokens themselves stay
    /// inside the harness processes, so this is how the test speaks as one of them.
    /// </summary>
    private async Task<WorkerInstanceId> IncumbentInstanceAsync(TaskId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var current = (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value, ct)).CurrentInstanceId;
        Assert.NotNull(current);
        return new WorkerInstanceId(current.Value);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new InvalidOperationException($"the forward closed after {offset}/{count} bytes");
            offset += read;
        }
        return buffer;
    }

    /// <summary>
    /// Whether the far end has gone: a read of 0 (docketd shut its side of the loopback
    /// socket down, the clean case) or a reset. Bounded, so a connection that is still very
    /// much alive fails the assertion rather than hanging the test.
    /// </summary>
    private static async Task<bool> ConnectionIsDeadAsync(Stream stream, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await stream.ReadAsync(new byte[1], bounded.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            return true;
        }
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
