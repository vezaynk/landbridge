using System.Text;
using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Landbridge.Runner.Tests;

/// <summary>
/// The landbridged side of the wire (spec §10): the channel POSTs encoded events and
/// heartbeats up to <c>/runner/ingest</c>, and takes commands off the <c>/runner/events</c>
/// stream and into the daemon. A minimal loopback Kestrel server speaks the contract so the
/// real <see cref="HttpControlPlaneChannel"/> runs end to end without the full MCP host.
/// </summary>
public class HttpControlPlaneChannelTests
{
    [Fact]
    public async Task Streams_commands_to_the_daemon_acks_them_and_posts_events_and_heartbeats()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        var eventTask = SessionId.New();
        var commandTask = SessionId.New();

        var gate = new object();
        var receivedEvents = new List<RunnerEvent>();
        var receivedHeartbeats = new List<MachineHeartbeat>();
        var acked = new List<long>();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();

        server.MapPost("/runner/ingest", async (HttpContext http) =>
        {
            var frame = await new StreamReader(http.Request.Body).ReadToEndAsync(http.RequestAborted);
            if (RunnerWire.DecodeHeartbeat(frame) is { } heartbeat)
                lock (gate) receivedHeartbeats.Add(heartbeat);
            else if (RunnerWire.DecodeEvent(frame) is { } evt)
                lock (gate) receivedEvents.Add(evt);
            return Results.NoContent();
        });

        server.MapPost("/runner/commands/{id:long}/ack", (long id) =>
        {
            lock (gate) acked.Add(id);
            return Results.NoContent();
        });

        // One command, then the response stays open exactly as the real endpoint's does.
        server.MapGet("/runner/events", async (HttpContext http) =>
        {
            http.Response.ContentType = "text/event-stream";
            await http.Response.StartAsync(http.RequestAborted);
            await http.Response.WriteAsync(": open\n\n", http.RequestAborted);
            var data = JsonSerializer.Serialize(new
            {
                id = 7L,
                kind = RunnerWire.Kill,
                frame = RunnerWire.EncodeCommand(new KillCommand(commandTask)),
            });
            await http.Response.WriteAsync($"id: 7\nevent: command\ndata: {data}\n\n", http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted); }
            catch (OperationCanceledException) { }
        });

        await server.StartAsync(ct);
        var planeUrl = new Uri(server.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal)));

        var supervisor = new FakeProcessSupervisor();
        var config = RunnerConfig.Load("""
            { "machine": { "work_root": "/tmp/landbridged-http-test" },
              "profiles": [ { "name": "default", "prompt": "go", "spawn": ["noop"] } ] }
            """);
        var clock = TimeProvider.System;
        await using var channel = new HttpControlPlaneChannel(planeUrl, "test-machine-token", clock);
        var daemon = new RunnerDaemon(
            "machine-http", config, supervisor,
            new BackPressureMonitor(new FakeLoadReader(), config.Machine.BackPressure),
            channel, new OutboundEventRing(64), new FakeStrayReaper(0), clock);

        channel.Start((command, token) => daemon.HandleAsync(command, token));

        Assert.True(await TestKit.WaitUntilAsync(() => channel.IsConnected, TimeSpan.FromSeconds(15)),
            "the channel never opened its command stream");

        Assert.True(await channel.PublishAsync(new StartedEvent(eventTask, clock.GetUtcNow()), gapBefore: 0, ct));
        Assert.True(await channel.HeartbeatAsync(
            new MachineHeartbeat(Ready: true, UnderBackPressure: false,
                new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], clock.GetUtcNow()), ct));

        Assert.True(await TestKit.WaitUntilAsync(
            () => { lock (gate) return receivedEvents.OfType<StartedEvent>().Any(e => e.Session == eventTask); },
            TimeSpan.FromSeconds(15)), "the plane never received the published event");
        Assert.True(await TestKit.WaitUntilAsync(
            () => { lock (gate) return receivedHeartbeats.Count > 0; },
            TimeSpan.FromSeconds(15)), "the plane never received the heartbeat");

        Assert.True(await TestKit.WaitUntilAsync(
            () => supervisor.Killed.Contains(commandTask), TimeSpan.FromSeconds(15)),
            "the streamed command never reached the daemon");

        // Acked only after the handler returned, which is what makes an interrupted
        // command replay instead of vanishing.
        Assert.True(await TestKit.WaitUntilAsync(
            () => { lock (gate) return acked.Contains(7L); }, TimeSpan.FromSeconds(15)),
            "the channel never acknowledged the command it ran");

        await daemon.ShutdownAsync();
        await server.StopAsync(ct);
    }
}
