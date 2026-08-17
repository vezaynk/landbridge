using System.Net.WebSockets;
using System.Text;
using Docket.ControlPlane;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Docket.Mcp.Tests;

/// <summary>
/// L3 stress guard for the §8.4 preview websocket close handshake through the real
/// relay + a real producer docketd. It hammers connect → echo → close many times
/// (sequential and concurrent) and requires every close to complete cleanly. This
/// is the end-to-end guard that the §8.3 splice-teardown fix (Docket.Relay
/// ForwardEntry + Docket.Runner RelayForwarder — PR #49) holds through the whole
/// preview data path; it reproduced the drop on run 1 before that fix. Fast
/// (~1s: the fixed teardown completes each close in milliseconds), so it stays a
/// normal Postgres-gated test rather than a dispatch-gated one.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PreviewWsStressTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string RelayBearer = "relay-shared-secret-under-test";
    private const string PreviewBearer = "preview-shared-secret-under-test";

    public async Task InitializeAsync() { if (pg.Available) await pg.ResetAsync(); }
    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Websocket_close_handshake_survives_stress()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        await using var upstream = await PreviewUpstream.StartAsync();
        var team = TeamId.New();
        var producerTask = await RelayGrantTestKit.RegisterWorkingServiceAsync(pg, team, "web", ct, port: upstream.Port);

        var relayUrl = RelayGrantTestKit.ReserveLoopbackUrl();
        await using var plane = RelayGrantTestKit.BuildPlane(pg.ConnectionString, RelayBearer, relayUrl, PreviewBearer);
        await plane.StartAsync(ct);
        await using var relay = RelayGrantTestKit.BuildRelay(RelayGrantTestKit.BaseUri(plane).ToString(), RelayBearer, listenUrl: relayUrl);
        await relay.StartAsync(ct);

        var registry = plane.Services.GetRequiredService<RunnerConnectionRegistry>();
        var sink = plane.Services.GetRequiredService<RunnerEventSink>();
        await using var producerDaemon = new DaemonHarness("mp", new SinkForwardingChannel(sink));
        await producerDaemon.StartAsync();
        registry.Register("mp", new HashSet<string> { "default" }, producerDaemon.Send);
        registry.TrackDispatch("mp", producerTask);

        string label;
        await using (var db = pg.NewContext())
            label = (await new PreviewMappingService(db, TimeProvider.System)
                .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30), ct)).Label;

        await using var frontend = PreviewFrontendHarness.Start(RelayGrantTestKit.BaseUri(plane).ToString());

        async Task OneAsync(int i)
        {
            using var ws = await frontend.BrowserWebSocketAsync(label, "/ws", ct);
            await ws.SendAsync(Encoding.UTF8.GetBytes($"ping{i}"), WebSocketMessageType.Text, true, ct);
            var buffer = new byte[64];
            var r = await ws.ReceiveAsync(buffer, ct);
            Assert.Equal($"ping{i}", Encoding.UTF8.GetString(buffer, 0, r.Count));
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
            Assert.Equal(WebSocketState.Closed, ws.State);
        }

        for (var i = 0; i < 60; i++)
            await OneAsync(i);
        for (var round = 0; round < 4; round++)
            await Task.WhenAll(Enumerable.Range(0, 10).Select(OneAsync));

        await producerDaemon.StopAsync();
        await relay.StopAsync(ct);
        await plane.StopAsync(ct);
    }
}
