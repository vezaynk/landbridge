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

    [Fact]
    public async Task WatchLiveAsync_opens_then_skips_catch_up()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/sessions/events", async ctx =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            var after = ctx.Request.Query["after"].ToString();
            if (after != long.MaxValue.ToString())
            {
                await ctx.Response.WriteAsync(
                    "id: 1\nevent: change\ndata: {\"queueId\":1,\"topic\":\"sessions\"}\n\n",
                    ctx.RequestAborted);
            }
            await ctx.Response.WriteAsync(
                "id: 2\nevent: change\ndata: {\"queueId\":2,\"topic\":\"sessions\",\"entityId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}\n\n",
                ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
        });
        await app.StartAsync(cts.Token);

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))) };
        var hub = new HubClient(http, NullLogger<HubClient>.Instance);
        var seen = new List<HubChange>();
        await foreach (var change in hub.WatchLiveAsync("/sessions/events", "t", cts.Token))
        {
            seen.Add(change);
            if (seen.Count == 2)
                break;
        }

        Assert.True(seen[0].IsLiveOpen);
        Assert.Equal(2, seen[1].QueueId);
        await app.StopAsync(cts.Token);
    }

    [Fact]
    public async Task InboxWatch_snapshots_after_hub_subscribes_then_on_change()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/sessions/events", async ctx =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            await Task.Delay(80, ctx.RequestAborted);
            await ctx.Response.WriteAsync(
                "id: 4\nevent: change\ndata: {\"queueId\":4,\"topic\":\"sessions\",\"entityId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}\n\n",
                ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
        });
        await app.StartAsync(cts.Token);

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))) };
        var hub = new HubClient(http, NullLogger<HubClient>.Instance);
        var n = 0;
        var snaps = new List<int>();
        await foreach (var snap in InboxWatch.OnHub(
            hub, "t", "/sessions/events", filter: null,
            _ => Task.FromResult(++n), cts.Token))
        {
            snaps.Add(snap);
            if (snaps.Count == 2)
                break;
        }

        Assert.Equal([1, 2], snaps);
        await app.StopAsync(cts.Token);
    }

    [Fact]
    public async Task InboxWatch_ignores_sessions_outside_the_filter()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var mine = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/sessions/events", async ctx =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync(
                $"id: 1\nevent: change\ndata: {{\"queueId\":1,\"topic\":\"sessions\",\"entityId\":\"{other}\"}}\n\n" +
                $"id: 2\nevent: change\ndata: {{\"queueId\":2,\"topic\":\"sessions\",\"entityId\":\"{mine}\"}}\n\n",
                ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
        });
        await app.StartAsync(cts.Token);

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))) };
        var hub = new HubClient(http, NullLogger<HubClient>.Instance);
        var n = 0;
        var snaps = new List<int>();
        await foreach (var snap in InboxWatch.OnHub(
            hub, "t", "/sessions/events", filter: new HashSet<Guid> { mine },
            _ => Task.FromResult(++n), cts.Token))
        {
            snaps.Add(snap);
            if (snaps.Count == 2)
                break;
        }

        Assert.Equal([1, 2], snaps);
        await app.StopAsync(cts.Token);
    }
}
