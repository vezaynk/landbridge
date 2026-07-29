using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The crown of increment 3 (spec §8.3): the whole forward loop with <b>no
/// fakes</b>. A real control plane (grants + registry + orchestrator + event
/// sink), a real relay validating grants against that plane, a real TCP echo
/// service registered by a working producer task, and two real docketd data
/// planes driven through <see cref="RunnerDaemon.HandleAsync"/> at the registry
/// seam. A consumer worker calls <c>open_forward</c> over real MCP, gets a
/// <c>127.0.0.1:port</c> address, and bytes written there round-trip
/// consumer-docketd → relay → producer-docketd → echo service and back. Closing
/// the connection surfaces <c>forward-closed</c> on both ends.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ForwardDataPlaneEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Bearer = "relay-shared-secret-under-test";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Worker_opens_a_forward_and_bytes_round_trip_through_the_real_relay()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // ── The producer's registered service: a real loopback TCP echo ─────────
        await using var echo = await EchoService.StartAsync();

        var team = TeamId.New();
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct, port: echo.Port);
        var consumer = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);

        // ── Plane + relay on a pre-reserved relay URL (no build-order race) ─────
        var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer, relayUrl);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(
            RelayGrantTestKit.BaseUri(plane).ToString(), Bearer, listenUrl: relayUrl);
        await relay.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = plane.Services.GetRequiredService<RunnerEventSink>();

        // ── Two real docketd data planes, one per machine ───────────────────────
        var consumerChannel = new SinkForwardingChannel(sink);
        var producerChannel = new SinkForwardingChannel(sink);
        await using var consumerDaemon = new DaemonHarness("mc", consumerChannel);
        await using var producerDaemon = new DaemonHarness("mp", producerChannel);
        await consumerDaemon.StartAsync();
        await producerDaemon.StartAsync();

        // The registry seam a socket would occupy: each machine's send delegate
        // hands the open-forward command straight to its docketd (§10).
        registry.Register("mc", new HashSet<string> { "default" }, consumerDaemon.Send);
        registry.Register("mp", new HashSet<string> { "default" }, producerDaemon.Send);
        registry.TrackDispatch("mc", consumer.Task);
        registry.TrackDispatch("mp", producerTask);

        // ── Consumer worker: open_forward over real MCP → a loopback address ────
        string forwardId;
        int port;
        await using (var worker = await RelayGrantTestKit.ConnectMcpAsync(
                         RelayGrantTestKit.BaseUri(plane), consumer.Token, ct))
        {
            var result = await worker.CallToolAsync("open_forward", new Dictionary<string, object?>
            {
                ["serviceName"] = "db",
            }, cancellationToken: ct);

            Assert.NotEqual(true, result.IsError);
            var json = result.StructuredContent is { } structured
                ? structured.GetRawText()
                : string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            using var doc = JsonDocument.Parse(json);
            var payload = doc.RootElement;
            Assert.Equal("127.0.0.1", payload.GetProperty("host").GetString());
            port = payload.GetProperty("port").GetInt32();
            forwardId = payload.GetProperty("forward_id").GetString()!;
            Assert.True(port > 0);
        }

        // ── Connect to the loopback address and prove the byte path ─────────────
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            await using var stream = client.GetStream();

            var payload = new byte[128 * 1024];
            Random.Shared.NextBytes(payload);
            await stream.WriteAsync(payload, ct);
            var echoed = await ReadExactlyAsync(stream, payload.Length, ct);
            Assert.True(payload.AsSpan().SequenceEqual(echoed),
                "bytes did not round-trip through consumer-docketd → relay → producer-docketd → echo");

            // Closing the connection tears the splice down on both ends.
            client.Client.Shutdown(SocketShutdown.Both);
        }

        // ── forward-closed bookkeeping fires on both ends ───────────────────────
        Assert.True(
            await WaitForAsync(() => consumerChannel.HasForwardClosed(forwardId), TimeSpan.FromSeconds(20)),
            "consumer docketd never reported forward-closed");
        Assert.True(
            await WaitForAsync(() => producerChannel.HasForwardClosed(forwardId), TimeSpan.FromSeconds(20)),
            "producer docketd never reported forward-closed");

        await consumerDaemon.StopAsync();
        await producerDaemon.StopAsync();
        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                throw new InvalidOperationException($"stream closed after {offset}/{count} bytes");
            offset += read;
        }
        return buffer;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }
}

/// <summary>
/// A real <see cref="RunnerDaemon"/> wired to a channel, standing up its relay
/// data planes. No harness ever spawns here — only open-forward commands are
/// driven — so the supervisor and back-pressure are the real ones doing nothing.
/// </summary>
internal sealed class DaemonHarness : IAsyncDisposable
{
    private readonly RunnerDaemon _daemon;
    private readonly string _workRoot;

    public DaemonHarness(string machineId, IControlPlaneChannel channel)
    {
        _workRoot = Path.Combine(Path.GetTempPath(), "docket-forward-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workRoot);
        var config = RunnerConfig.Load($$"""
            { "machine": { "work_root": {{JsonSerializer.Serialize(_workRoot)}} },
              "profiles": [ { "name": "default", "spawn": ["noop"] } ] }
            """);
        var ring = new OutboundEventRing(256);
        var supervisor = new ProcessSupervisor(config.Machine, ring, TimeProvider.System);
        var backPressure = new BackPressureMonitor(
            new PortableSystemLoadReader(config.Machine.WorkRoot), config.Machine.BackPressure);
        _daemon = new RunnerDaemon(
            machineId, config, supervisor, backPressure, channel, ring, new NoOpReaper(), TimeProvider.System);
    }

    public Task Send(RunnerCommand command, CancellationToken ct) => _daemon.HandleAsync(command, ct);

    public Task StartAsync() => _daemon.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(_workRoot, recursive: true); } catch { /* best effort */ }
    }

    private bool _stopped;

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        await _daemon.ShutdownAsync();
    }
}

/// <summary>
/// An <see cref="IControlPlaneChannel"/> that stands in for a runner socket's
/// receive loop: it records every event and forwards the forward-lifecycle ones
/// into the plane's <see cref="RunnerEventSink"/>, so the orchestrator's
/// <c>forward-opened</c> waiter completes and <c>forward-closed</c> bookkeeping
/// runs. Reboot/started are recorded but not replayed — pre-tracking the tasks to
/// resolve their machines would otherwise make a start-time reboot requeue them.
/// </summary>
internal sealed class SinkForwardingChannel(RunnerEventSink sink) : IControlPlaneChannel
{
    private readonly object _gate = new();
    private readonly List<RunnerEvent> _events = [];

    public async Task<bool> PublishAsync(RunnerEvent evt, long gapBefore, CancellationToken ct)
    {
        lock (_gate) _events.Add(evt);
        if (evt is ForwardOpenedEvent or ForwardClosedEvent)
            await sink.HandleAsync(evt, ct);
        return true;
    }

    public Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct) => Task.FromResult(true);

    public bool HasForwardClosed(string forwardId)
    {
        lock (_gate)
            return _events.OfType<ForwardClosedEvent>().Any(e => e.ForwardId == forwardId);
    }
}

/// <summary>A stray reaper that reaps nothing — the test spawns no harnesses.</summary>
internal sealed class NoOpReaper : IStrayReaper
{
    public int Reap(string machineId) => 0;
}

/// <summary>A loopback TCP echo service standing in for a registered service.</summary>
internal sealed class EchoService : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private EchoService(TcpListener listener)
    {
        _listener = listener;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<EchoService> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new EchoService(listener));
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                _ = Task.Run(() => EchoAsync(socket));
            }
        }
        catch (Exception) { /* stopped */ }
    }

    private async Task EchoAsync(Socket socket)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read == 0) return;
                    await stream.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                }
            }
            catch (Exception) { /* peer closed / stopped */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (Exception) { /* stopped */ }
        _cts.Dispose();
    }
}
