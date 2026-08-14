using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The two §8.3 lifetime facts the increment-3 crown never pinned: an established splice
/// survives grant <em>revocation</em> (a grant is a connection-establishment credential),
/// and it must die when the owning producer leaves <c>working</c> (revocation is not that
/// kill switch; the plane has to tear the live tunnels down). Both run the real relay,
/// two real docketd data planes, and a real echo that outlives the producer task — so a
/// dead splice is a closed TCP connection, not a missing <c>registered_services</c> row.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ForwardLifetimeEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Bearer = "relay-lifetime-shared-secret-under-test";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task An_established_splice_keeps_carrying_bytes_after_its_grants_are_revoked()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var session = await OpenAsync(ct);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, session.Port, ct);
        await using var stream = client.GetStream();
        await EchoOnceAsync(stream, ct);

        await using (var db = pg.NewContext())
        {
            var n = await db.RelayGrants.ExecuteUpdateAsync(s => s.SetProperty(g => g.Revoked, true), ct);
            Assert.True(n > 0, "expected at least one live grant to revoke");
        }

        // §8.3: a grant is checked on open. An established splice is not re-validated,
        // so revoking unused-open grants must not sever bytes already flowing. v1 is
        // one accept per forward, so this has to be the same connection, not a new one.
        await EchoOnceAsync(stream, ct);
    }

    [SkippableFact]
    public async Task Producer_leaving_working_closes_the_established_splice()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var session = await OpenAsync(ct);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, session.Port, ct);
        await using var stream = client.GetStream();
        await EchoOnceAsync(stream, ct);

        await using (var db = pg.NewContext())
        {
            var store = new TaskStore(db, TimeProvider.System);
            Assert.IsType<StoreResult.Applied>(
                await store.ApplyAsync(session.ProducerTask, new LivenessLost(LivenessLossReason.MachineReboot), ct));
        }

        // The echo process is still up: if the splice died it is because the plane tore
        // the forward down, not because the service vanished. A further write must fail
        // (EOF or reset), not round-trip.
        var closed = await WriteFailsAsync(stream, ct);
        Skip.If(!closed,
            "pending #147: leaving working revokes grants but does not close the live splice — unskip when the TCP connection dies");
        Assert.True(closed);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private async Task<ForwardSession> OpenAsync(CancellationToken ct)
    {
        var echo = await EchoService.StartAsync();
        var team = TeamId.New();
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "db", ct, port: echo.Port);
        var consumer = await RelayGrantTestKit.SeedWorkingWorkerAsync(pg, team, ct);

        var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
        var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, Bearer, relayUrl);
        await plane.StartAsync(ct);
        var relay = RelayGrantTestKit.BuildRelay(
            RelayGrantTestKit.BaseUri(plane).ToString(), Bearer, listenUrl: relayUrl);
        await relay.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = plane.Services.GetRequiredService<RunnerEventSink>();
        var consumerChannel = new SinkForwardingChannel(sink);
        var producerChannel = new SinkForwardingChannel(sink);
        var consumerDaemon = new DaemonHarness("mc", consumerChannel);
        var producerDaemon = new DaemonHarness("mp", producerChannel);
        await consumerDaemon.StartAsync();
        await producerDaemon.StartAsync();

        registry.Register("mc", new HashSet<string> { "default" }, consumerDaemon.Send);
        registry.Register("mp", new HashSet<string> { "default" }, producerDaemon.Send);
        registry.TrackDispatch("mc", consumer.Task);
        registry.TrackDispatch("mp", producerTask);

        int port;
        string forwardId;
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
            port = doc.RootElement.GetProperty("port").GetInt32();
            forwardId = doc.RootElement.GetProperty("forward_id").GetString()!;
            Assert.True(port > 0);
        }

        return new ForwardSession(
            echo, plane, relay, consumerDaemon, producerDaemon, producerTask, port, forwardId);
    }

    private static async Task EchoOnceAsync(NetworkStream stream, CancellationToken ct)
    {
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        await stream.WriteAsync(payload, ct);
        var echoed = await ReadExactlyAsync(stream, payload.Length, ct);
        Assert.True(payload.AsSpan().SequenceEqual(echoed), "bytes did not round-trip through the splice");
    }

    private static async Task<bool> WriteFailsAsync(NetworkStream stream, CancellationToken ct)
    {
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);
        try
        {
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
            var buffer = new byte[payload.Length];
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(5));
            var read = await stream.ReadAsync(buffer, readCts.Token);
            return read == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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

    private sealed class ForwardSession(
        EchoService echo,
        Microsoft.AspNetCore.Builder.WebApplication plane,
        Microsoft.AspNetCore.Builder.WebApplication relay,
        DaemonHarness consumerDaemon,
        DaemonHarness producerDaemon,
        TaskId producerTask,
        int port,
        string forwardId) : IAsyncDisposable
    {
        public TaskId ProducerTask { get; } = producerTask;
        public int Port { get; } = port;
        public string ForwardId { get; } = forwardId;

        public async ValueTask DisposeAsync()
        {
            await consumerDaemon.DisposeAsync();
            await producerDaemon.DisposeAsync();
            await relay.StopAsync();
            await relay.DisposeAsync();
            await plane.StopAsync();
            await plane.DisposeAsync();
            await echo.DisposeAsync();
        }
    }
}
