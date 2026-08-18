using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Runner.Tests;

/// <summary>
/// The landbridged relay data planes (spec §8.3), each end driven in isolation
/// against a <em>fake</em> relay — a minimal loopback WebSocket server, the same
/// shape <see cref="WebSocketControlPlaneChannelTests"/> uses for the control
/// plane. The consumer plane binds loopback-only, reports its bound port via
/// <c>forward-opened</c>, and splices an accepted TCP connection to the relay; the
/// producer plane dials a local service and splices it. Both emit
/// <c>forward-closed</c> when their splice ends, and a consumer nobody connects to
/// closes within the bounded wait.
/// </summary>
public sealed class RelayForwardingTests
{
    private static CancellationToken Timeout(out CancellationTokenSource cts)
    {
        cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return cts.Token;
    }

    [Fact]
    public async Task Consumer_plane_binds_loopback_reports_its_port_and_splices_one_connection()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromSeconds(30));
        await daemon.StartAsync();
        await using var relay = await FakeRelay.StartAsync();

        var task = SessionId.New();
        const string forwardId = "fwd-consumer";
        var outcome = await daemon.HandleAsync(new OpenForwardCommand(
            task, forwardId, "db", RelayTunnel.ConsumerRole, "lbr_g_x", relay.HttpUrl, Port: 0));
        Assert.IsType<CommandOutcome.Acknowledged>(outcome);

        // The consumer bound its loopback listener and reported the port.
        var opened = await WaitForEventAsync<ForwardOpenedEvent>(channel, forwardId, ct);
        Assert.True(opened.Port > 0, "consumer did not report a bound port");

        // Connect to 127.0.0.1:port as the worker's client would — proving the
        // bind is on loopback, not 0.0.0.0. landbridged accepts and opens its tunnel.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, opened.Port, ct);
        await using var tcp = client.GetStream();

        // The relay end echoes: bytes the consumer sends come straight back, which
        // exercises the splice both directions through the one TCP connection.
        var relaySocket = await relay.Accepted.WaitAsync(ct);
        var echo = Task.Run(() => EchoWebSocketAsync(relaySocket, ct), ct);

        var payload = RandomBytes(96 * 1024); // > 64 KiB buffer: proves streaming
        await tcp.WriteAsync(payload, ct);
        var echoed = await ReadExactlyAsync(tcp, payload.Length, ct);
        Assert.True(payload.AsSpan().SequenceEqual(echoed), "consumer splice did not round-trip the bytes");

        // Worker closes → the splice ends → forward-closed.
        client.Client.Shutdown(SocketShutdown.Send);
        var closed = await WaitForEventAsync<ForwardClosedEvent>(channel, forwardId, ct);
        Assert.Equal(forwardId, closed.ForwardId);

        await echo.WaitAsync(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }, TaskScheduler.Default);
        await daemon.ShutdownAsync();
    }

    [Fact]
    public async Task Consumer_plane_that_nobody_connects_to_closes_within_the_bounded_wait()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        // Short accept timeout so the bounded wait elapses quickly in the test.
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromMilliseconds(500));
        await daemon.StartAsync();
        await using var relay = await FakeRelay.StartAsync();

        var task = SessionId.New();
        const string forwardId = "fwd-lonely";
        await daemon.HandleAsync(new OpenForwardCommand(
            task, forwardId, "db", RelayTunnel.ConsumerRole, "lbr_g_x", relay.HttpUrl, Port: 0));

        // It still binds and reports its port…
        await WaitForEventAsync<ForwardOpenedEvent>(channel, forwardId, ct);
        // …then closes cleanly once nobody connects within the wait — no hang.
        var closed = await WaitForEventAsync<ForwardClosedEvent>(channel, forwardId, ct);
        Assert.Equal(forwardId, closed.ForwardId);

        await daemon.ShutdownAsync();
    }

    [Fact]
    public async Task Producer_plane_dials_the_local_service_and_splices_it_to_the_relay()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        await using var service = await TcpEchoServer.StartAsync(); // the "registered service"
        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromSeconds(30));
        await daemon.StartAsync();
        await using var relay = await FakeRelay.StartAsync();

        var task = SessionId.New();
        const string forwardId = "fwd-producer";
        await daemon.HandleAsync(new OpenForwardCommand(
            task, forwardId, "db", RelayTunnel.ProducerRole, "lbr_g_x", relay.HttpUrl, service.Port));

        // Producer opened its tunnel and dialed the echo service. Drive bytes in
        // from the relay end: they reach the service, echo back, and return on the
        // tunnel — the producer splice both directions.
        var relaySocket = await relay.Accepted.WaitAsync(ct);
        var payload = RandomBytes(80 * 1024);
        await relaySocket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, ct);
        var echoed = await ReceiveExactlyAsync(relaySocket, payload.Length, ct);
        Assert.True(payload.AsSpan().SequenceEqual(echoed), "producer splice did not round-trip through the service");

        await relaySocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        var closed = await WaitForEventAsync<ForwardClosedEvent>(channel, forwardId, ct);
        Assert.Equal(forwardId, closed.ForwardId);

        await daemon.ShutdownAsync();
    }

    // ── §8.3 close-forward: the splice's authority ends with the task ───────────

    /// <summary>
    /// §8.3: "an established splice persists <b>until the owning task leaves
    /// <c>working</c></b>". Nothing enforced the second half — the plane revoked the grant,
    /// which gates only the <em>next</em> open, and the running splice had no handle anywhere
    /// in the system. This is that handle: an established consumer splice, proven live by a
    /// byte round-trip, then closed on command, and the worker's own TCP connection dies with
    /// it. Fails before the fix because <c>close-forward</c> did not exist and the only way to
    /// end one forward was to take the whole daemon down.
    /// </summary>
    [Fact]
    public async Task Close_forward_ends_an_established_consumer_splice()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromSeconds(30));
        await daemon.StartAsync();
        await using var relay = await FakeRelay.StartAsync();

        var task = SessionId.New();
        const string forwardId = "fwd-closable";
        await daemon.HandleAsync(new OpenForwardCommand(
            task, forwardId, "db", RelayTunnel.ConsumerRole, "lbr_g_x", relay.HttpUrl, Port: 0));

        var opened = await WaitForEventAsync<ForwardOpenedEvent>(channel, forwardId, ct);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, opened.Port, ct);
        await using var tcp = client.GetStream();

        // Live: the bytes go through. Everything after this is about a splice that WAS
        // working, which is the case the old code could not end.
        var relaySocket = await relay.Accepted.WaitAsync(ct);
        var echo = Task.Run(() => EchoWebSocketAsync(relaySocket, ct), ct);
        var payload = RandomBytes(4096);
        await tcp.WriteAsync(payload, ct);
        var echoed = await ReadExactlyAsync(tcp, payload.Length, ct);
        Assert.True(payload.AsSpan().SequenceEqual(echoed), "the splice was not live before the close");

        var outcome = Assert.IsType<CommandOutcome.Acknowledged>(
            await daemon.HandleAsync(new CloseForwardCommand(task, forwardId)));
        Assert.Contains(forwardId, outcome.Detail);
        Assert.DoesNotContain("not held", outcome.Detail);

        // The worker's connection dies: landbridged shuts its end of the loopback socket down,
        // so the next read is EOF (or a reset if the abort raced) — never a live connection
        // outliving the task that authorized it.
        Assert.True(await ConnectionIsDeadAsync(tcp, ct), "the worker's connection survived close-forward");
        var closed = await WaitForEventAsync<ForwardClosedEvent>(channel, forwardId, ct);
        Assert.Equal(forwardId, closed.ForwardId);

        await echo.WaitAsync(TimeSpan.FromSeconds(5), ct).ContinueWith(_ => { }, TaskScheduler.Default);
        await daemon.ShutdownAsync();
    }

    /// <summary>
    /// The other half a revoked grant left running: a consumer listener that never accepted
    /// anything. It was bounded only by the accept timeout — up to the grant's whole 2 minutes
    /// of an idle loopback port kept open for a task that has finished — because nothing ever
    /// told it to close. Driven with a long accept timeout, so the close is what ends it and
    /// not the clock.
    /// </summary>
    [Fact]
    public async Task Close_forward_closes_a_consumer_listener_that_never_accepted()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromMinutes(2));
        await daemon.StartAsync();
        await using var relay = await FakeRelay.StartAsync();

        var task = SessionId.New();
        const string forwardId = "fwd-idle-listener";
        await daemon.HandleAsync(new OpenForwardCommand(
            task, forwardId, "db", RelayTunnel.ConsumerRole, "lbr_g_x", relay.HttpUrl, Port: 0));
        var opened = await WaitForEventAsync<ForwardOpenedEvent>(channel, forwardId, ct);

        await daemon.HandleAsync(new CloseForwardCommand(task, forwardId));

        // Reported closed well inside the two-minute accept wait, and the port it bound
        // stops accepting: the listener is gone, not merely unreported.
        var closed = await WaitForEventAsync<ForwardClosedEvent>(channel, forwardId, ct);
        Assert.Equal(forwardId, closed.ForwardId);
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var late = new TcpClient();
            await late.ConnectAsync(IPAddress.Loopback, opened.Port, ct);
        });

        await daemon.ShutdownAsync();
    }

    [Fact]
    public async Task Close_forward_for_a_forward_this_machine_does_not_hold_is_acked_as_already_closed()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromSeconds(30));
        await daemon.StartAsync();

        // §10 commands are best-effort against a live machine: the plane closes what its
        // grant rows say a leaving task held, and a forward that already ended on its own is
        // exactly the outcome it wanted. Acked, never thrown, and no event invented for it.
        var outcome = Assert.IsType<CommandOutcome.Acknowledged>(
            await daemon.HandleAsync(new CloseForwardCommand(SessionId.New(), "fwd-never-existed")));

        Assert.Contains("not held", outcome.Detail);
        Assert.DoesNotContain(channel.Events, e => e.Event is ForwardClosedEvent);

        await daemon.ShutdownAsync();
    }

    [Fact]
    public async Task Producer_plane_dial_failure_surfaces_as_forward_closed()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromSeconds(30));
        await daemon.StartAsync();

        // A port with nothing listening: a service that died between registration
        // and dial must surface as forward-closed, not a hang (§8.3).
        var deadPort = ReserveClosedPort();
        var task = SessionId.New();
        const string forwardId = "fwd-deadservice";
        await daemon.HandleAsync(new OpenForwardCommand(
            task, forwardId, "db", RelayTunnel.ProducerRole, "lbr_g_x", "http://127.0.0.1:1", deadPort));

        var closed = await WaitForEventAsync<ForwardClosedEvent>(channel, forwardId, ct);
        Assert.Equal(forwardId, closed.ForwardId);
        // The producer never binds a listener, so it never reports forward-opened.
        Assert.DoesNotContain(channel.Events, e => e.Event is ForwardOpenedEvent);

        await daemon.ShutdownAsync();
    }

    /// <summary>
    /// Regression for the §8.3 splice-teardown final-frame drop, producer side. A
    /// local service sends a final frame then closes; the producer must deliver
    /// every byte to the relay and then a CLEAN close, not abort the tunnel and
    /// truncate the tail. The old teardown (CloseOutput + socket.Shutdown +
    /// immediate CancelAsync) aborts the WebSocket, RSTing the tunnel and discarding
    /// a final frame still in flight to the relay — surfacing at the far end as
    /// "closed without completing the close handshake". Driven across many rapid
    /// forwards, reading the frame only after the producer has torn down, to force
    /// the window; fails on the old teardown, clean on the fix. Mirrors the relay's
    /// own <c>RelayFinalFrameTests</c>, one deterministic guard per splice site.
    /// </summary>
    [Fact]
    public async Task Producer_delivers_the_services_final_frame_before_the_tunnel_closes()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromSeconds(30));
        await daemon.StartAsync();
        await using var relay = await MultiAcceptRelay.StartAsync();

        var payload = RandomBytes(4096);
        for (var i = 0; i < 120; i++)
        {
            await using var service = await TcpSender.StartAsync(payload); // sends payload, then closes
            var forwardId = $"fwd-final-{i}";
            await daemon.HandleAsync(new OpenForwardCommand(
                SessionId.New(), forwardId, "db", RelayTunnel.ProducerRole, "lbr_g_x", relay.HttpUrl, service.Port));

            var relaySocket = await relay.NextAsync(ct);
            var received = await ReceiveExactlyAsync(relaySocket, payload.Length, ct);
            Assert.True(payload.AsSpan().SequenceEqual(received), $"iteration {i}: producer final frame was truncated");

            var tail = await relaySocket.ReceiveAsync(new byte[16], ct);
            Assert.Equal(WebSocketMessageType.Close, tail.MessageType);
        }

        await daemon.ShutdownAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // ── §8.2 refuse-at-dial ─────────────────────────────────────────────────────

    /// <summary>
    /// §8.2's stated hazard is not the failed connection — it is the SUCCESSFUL one: a
    /// registration outliving its process, so the dial lands on whatever else took the
    /// port and the consumer gets plausible wrong answers instead of an error. Nobody
    /// could close that before, because nobody knew which listener was intended. For a
    /// landbridged-supervised service, landbridged does.
    /// </summary>
    [Fact]
    public async Task A_dial_for_a_declared_service_that_is_down_is_refused_with_a_reason()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        // A real listener is on the port — standing in for "something else grabbed it".
        // The dial must NOT reach it, because the declared service is not running.
        await using var impostor = await TcpEchoServer.StartAsync();
        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        // A real supervisor with the service declared on that port and never started:
        // it answers "declared, not running", which is the production seam verbatim.
        await using var services = new ServiceSupervisor(
            [DeclaredService("db", impostor.Port)], "machine-fwd", TimeProvider.System);
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromSeconds(30),
            services: services);
        await daemon.StartAsync();

        const string forwardId = "fwd-refused";
        await daemon.HandleAsync(new OpenForwardCommand(
            SessionId.New(), forwardId, "db", RelayTunnel.ProducerRole, "lbr_g_x",
            "http://127.0.0.1:1/relay", impostor.Port));

        var closed = await WaitForEventAsync<ForwardClosedEvent>(channel, forwardId, ct);
        Assert.NotNull(closed.Refusal);
        Assert.Contains("not running", closed.Refusal);
        Assert.Contains(impostor.Port.ToString(), closed.Refusal);

        await daemon.ShutdownAsync();
    }

    [Fact]
    public async Task A_dial_for_an_undeclared_port_is_not_refused()
    {
        var ct = Timeout(out var cts);
        using var _ = cts;

        // A null answer means "landbridged declares no service here", which must dial as
        // before — the port may be a worker-started listener that is none of its
        // business. Refusing on null would break §8.2 forwards that work today.
        await using var service = await TcpEchoServer.StartAsync();
        var ring = new OutboundEventRing(256);
        var channel = new InMemoryControlPlaneChannel();
        // A supervisor that declares a DIFFERENT port: this dial target is unknown to
        // it, so the answer is null and the dial proceeds.
        await using var services = new ServiceSupervisor(
            [DeclaredService("other", service.Port + 1)], "machine-fwd", TimeProvider.System);
        var daemon = BuildDaemon(ring, channel, acceptTimeout: TimeSpan.FromSeconds(30),
            services: services);
        await daemon.StartAsync();
        await using var relay = await FakeRelay.StartAsync();

        const string forwardId = "fwd-undeclared";
        await daemon.HandleAsync(new OpenForwardCommand(
            SessionId.New(), forwardId, "db", RelayTunnel.ProducerRole, "lbr_g_x",
            relay.HttpUrl, service.Port));

        // It dialed: the relay end sees a tunnel, and no refusal is reported.
        var relaySocket = await relay.Accepted.WaitAsync(ct);
        await relaySocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        var closed = await WaitForEventAsync<ForwardClosedEvent>(channel, forwardId, ct);
        Assert.Null(closed.Refusal);

        await daemon.ShutdownAsync();
    }

    private static ServiceConfig DeclaredService(string name, int port) =>
        new(name, [TestKit.HarnessPath()], null, new Dictionary<string, string>(StringComparer.Ordinal),
            port, null, ServiceDefaults.MaxBackoff, new LogsConfig());

    private static RunnerDaemon BuildDaemon(
        OutboundEventRing ring, InMemoryControlPlaneChannel channel, TimeSpan acceptTimeout,
        ServiceSupervisor? services = null)
    {
        var config = RunnerConfig.Load("""
            { "machine": { "work_root": "/tmp/landbridged-forward-test" },
              "profiles": [ { "name": "default", "prompt": "go", "spawn": ["noop"] } ] }
            """);
        var clock = new FakeTimeProvider();
        return new RunnerDaemon(
            "machine-fwd", config, new FakeProcessSupervisor(),
            new BackPressureMonitor(new FakeLoadReader(), config.Machine.BackPressure),
            channel, ring, new FakeStrayReaper(0), clock, forwardAcceptTimeout: acceptTimeout,
            services: services);
    }

    private static async Task<T> WaitForEventAsync<T>(
        InMemoryControlPlaneChannel channel, string forwardId, CancellationToken ct)
        where T : RunnerEvent
    {
        while (!ct.IsCancellationRequested)
        {
            var match = channel.Events
                .Select(e => e.Event)
                .OfType<T>()
                .FirstOrDefault(e => ForwardIdOf(e) == forwardId);
            if (match is not null)
                return match;
            await Task.Delay(20, ct);
        }
        throw new OperationCanceledException($"never observed {typeof(T).Name} for {forwardId}");
    }

    private static string? ForwardIdOf(RunnerEvent e) => e switch
    {
        ForwardOpenedEvent o => o.ForwardId,
        ForwardClosedEvent c => c.ForwardId,
        _ => null,
    };

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    /// <summary>
    /// Whether the far end of <paramref name="stream"/> has gone: a read that returns 0
    /// (landbridged shut its side down, the clean case) or throws (the socket was reset, which
    /// a teardown race can produce). Bounded so a connection that is still very much alive
    /// fails the assertion instead of hanging the test.
    /// </summary>
    private static async Task<bool> ConnectionIsDeadAsync(Stream stream, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await stream.ReadAsync(new byte[1], bounded.Token) == 0;
        }
        catch (OperationCanceledException)
        {
            return false; // still open, and nothing arrived: the splice outlived the close.
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

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

    private static async Task<byte[]> ReceiveExactlyAsync(WebSocket ws, int count, CancellationToken ct)
    {
        var received = new byte[count];
        var offset = 0;
        var buffer = new byte[64 * 1024];
        while (offset < count)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"peer closed after {offset}/{count} bytes");
            Buffer.BlockCopy(buffer, 0, received, offset, result.Count);
            offset += result.Count;
        }
        return received;
    }

    private static async Task EchoWebSocketAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                await ws.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
            }
        }
        catch (Exception) { /* teardown */ }
    }

    private static int ReserveClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// A minimal loopback relay: accepts one WebSocket on <c>/tunnel</c> and hands it
/// to the test, which drives / echoes bytes on it. It never validates the grant —
/// grant policy is the real relay's job (covered elsewhere); this exercises the
/// landbridged side of the tunnel in isolation.
/// </summary>
internal sealed class FakeRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _hold = new();
    private readonly TaskCompletionSource<WebSocket> _accepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FakeRelay(WebApplication app) => _app = app;

    public Task<WebSocket> Accepted => _accepted.Task;

    public string HttpUrl => _app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

    public static async Task<FakeRelay> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var relay = new FakeRelay(app);
        app.UseWebSockets();
        app.Map("/tunnel", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var socket = await http.WebSockets.AcceptWebSocketAsync();
            relay._accepted.TrySetResult(socket);
            // Park while the test uses the socket; released on dispose or abort.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(relay._hold.Token, http.RequestAborted);
            try { await Task.Delay(System.Threading.Timeout.Infinite, linked.Token); }
            catch (OperationCanceledException) { }
        });
        await app.StartAsync();
        return relay;
    }

    public async ValueTask DisposeAsync()
    {
        await _hold.CancelAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _hold.Dispose();
    }
}

/// <summary>A loopback TCP echo server standing in for a registered local service.</summary>
internal sealed class TcpEchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private TcpEchoServer(TcpListener listener)
    {
        _listener = listener;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<TcpEchoServer> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new TcpEchoServer(listener));
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
                    if (read == 0)
                        return;
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

/// <summary>Like <see cref="FakeRelay"/> but accepts many tunnels, handing each accepted
/// socket to the test through a channel — for the final-frame loop, one forward per accept.</summary>
internal sealed class MultiAcceptRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _hold = new();
    private readonly System.Threading.Channels.Channel<WebSocket> _accepted =
        System.Threading.Channels.Channel.CreateUnbounded<WebSocket>();

    private MultiAcceptRelay(WebApplication app) => _app = app;

    public string HttpUrl => _app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal));

    public ValueTask<WebSocket> NextAsync(CancellationToken ct) => _accepted.Reader.ReadAsync(ct);

    public static async Task<MultiAcceptRelay> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var relay = new MultiAcceptRelay(app);
        app.UseWebSockets();
        app.Map("/tunnel", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var socket = await http.WebSockets.AcceptWebSocketAsync();
            await relay._accepted.Writer.WriteAsync(socket);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(relay._hold.Token, http.RequestAborted);
            try { await Task.Delay(System.Threading.Timeout.Infinite, linked.Token); }
            catch (OperationCanceledException) { }
        });
        await app.StartAsync();
        return relay;
    }

    public async ValueTask DisposeAsync()
    {
        await _hold.CancelAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _hold.Dispose();
    }
}

/// <summary>A loopback TCP service that, on connect, writes a fixed payload and then closes —
/// the "final frame then close" shape that lost its tail on the old producer teardown (§8.3).</summary>
internal sealed class TcpSender : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private TcpSender(TcpListener listener, byte[] payload)
    {
        _listener = listener;
        _loop = Task.Run(() => ServeAsync(payload));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<TcpSender> StartAsync(byte[] payload)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new TcpSender(listener, payload));
    }

    private async Task ServeAsync(byte[] payload)
    {
        try
        {
            var socket = await _listener.AcceptSocketAsync(_cts.Token);
            using (socket)
            await using (var stream = new NetworkStream(socket, ownsSocket: false))
            {
                await stream.WriteAsync(payload, _cts.Token);
                await stream.FlushAsync(_cts.Token);
                // Leaving the scope disposes the socket → FIN: "final frame then close".
            }
        }
        catch (Exception) { /* test tore us down */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (Exception) { /* stopped */ }
        _cts.Dispose();
    }
}
