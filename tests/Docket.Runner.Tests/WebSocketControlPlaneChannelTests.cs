using System.Net.WebSockets;
using System.Text;
using Docket.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Docket.Runner.Tests;

/// <summary>
/// The docketd side of the wire (spec §10): the channel dials a control plane
/// outbound, ships encoded events/heartbeats up it, and delivers decoded
/// commands to the daemon. A minimal loopback Kestrel server speaks the contract
/// so the real <see cref="WebSocketControlPlaneChannel"/> is exercised end to end
/// without the full MCP host.
/// </summary>
public class WebSocketControlPlaneChannelTests
{
    [Fact]
    public async Task Dials_out_publishes_events_and_heartbeats_and_delivers_commands_to_the_daemon()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        var commandTask = SessionId.New(); // the command the server pushes down
        var eventTask = SessionId.New();   // the event the channel publishes up

        var gate = new object();
        var receivedEvents = new List<RunnerEvent>();
        var receivedHeartbeats = new List<MachineHeartbeat>();

        // ── A loopback control plane that speaks the runner contract ─────────
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.UseWebSockets();
        server.Map("/runner", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();

            // Push a command down to the runner as soon as it connects.
            var killBytes = Encoding.UTF8.GetBytes(RunnerWire.EncodeCommand(new KillCommand(commandTask)));
            await socket.SendAsync(killBytes, WebSocketMessageType.Text, endOfMessage: true, http.RequestAborted);

            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !http.RequestAborted.IsCancellationRequested)
            {
                var message = await ReceiveFullMessageAsync(socket, buffer, http.RequestAborted);
                if (message is null)
                    break;
                if (RunnerWire.DecodeHeartbeat(message) is { } heartbeat)
                {
                    lock (gate) receivedHeartbeats.Add(heartbeat);
                    continue;
                }
                if (RunnerWire.DecodeEvent(message) is { } evt)
                {
                    lock (gate) receivedEvents.Add(evt);
                }
            }
        });
        await server.StartAsync(ct);
        var wsUrl = new Uri(server.Urls.First(u => u.StartsWith("http://")).Replace("http://", "ws://") + "/runner");

        // ── The real channel + a daemon backed by a fake supervisor ──────────
        var supervisor = new FakeProcessSupervisor();
        var config = RunnerConfig.Load("""
            { "machine": { "work_root": "/tmp/docketd-ws-test" },
              "profiles": [ { "name": "default", "prompt": "go", "spawn": ["noop"] } ] }
            """);
        var clock = TimeProvider.System;
        await using var channel = new WebSocketControlPlaneChannel(wsUrl, "test-machine-token", clock);
        var daemon = new RunnerDaemon(
            "machine-ws", config, supervisor,
            new BackPressureMonitor(new FakeLoadReader(), config.Machine.BackPressure),
            channel, new OutboundEventRing(64), new FakeStrayReaper(0), clock);

        channel.Start((command, token) => daemon.HandleAsync(command, token));

        Assert.True(await TestKit.WaitUntilAsync(() => channel.IsConnected, TimeSpan.FromSeconds(10)),
            "channel never connected");

        // Publish an event and a heartbeat up the dialed socket.
        Assert.True(await channel.PublishAsync(new StartedEvent(eventTask, clock.GetUtcNow()), gapBefore: 0, ct));
        Assert.True(await channel.HeartbeatAsync(
            new MachineHeartbeat("machine-ws", Ready: true, UnderBackPressure: false,
                new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], clock.GetUtcNow()), ct));

        // The server decoded both frames.
        Assert.True(await TestKit.WaitUntilAsync(
            () => { lock (gate) return receivedEvents.OfType<StartedEvent>().Any(e => e.Session == eventTask); },
            TimeSpan.FromSeconds(10)), "server never received the published event");
        Assert.True(await TestKit.WaitUntilAsync(
            () => { lock (gate) return receivedHeartbeats.Count > 0; },
            TimeSpan.FromSeconds(10)), "server never received the heartbeat");

        // The command the server pushed reached daemon.HandleAsync → the supervisor.
        Assert.True(await TestKit.WaitUntilAsync(
            () => supervisor.Killed.Contains(commandTask), TimeSpan.FromSeconds(10)),
            "the pushed command never reached the daemon");

        await daemon.ShutdownAsync();
        await server.StopAsync(ct);
    }

    private static async Task<string?> ReceiveFullMessageAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", ct);
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
