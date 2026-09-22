using Landbridge.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Landbridge.Mcp.Tests;

public sealed class HubClientWatchTests
{
    [Fact]
    public async Task WatchAsync_yields_change_frames_and_skips_pings()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/sessions/events", async ctx =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync(
                "event: ping\ndata:\n\n" +
                "id: 9\nevent: change\ndata: {\"queueId\":9,\"topic\":\"sessions\",\"entityId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}\n\n",
                ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
        });
        await app.StartAsync(cts.Token);

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))) };
        var hub = new HubClient(http, NullLogger<HubClient>.Instance);
        HubChange? seen = null;
        await foreach (var change in hub.WatchAsync("/sessions/events", "t", cts.Token))
        {
            seen = change;
            break;
        }

        Assert.NotNull(seen);
        Assert.Equal(9, seen.QueueId);
        Assert.Equal("sessions", seen.Topic);
        await app.StopAsync(cts.Token);
    }
}
