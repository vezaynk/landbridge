using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Hub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.Hub.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HubSseTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task A_session_commit_writes_queue_rows()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        await app.Services.GetRequiredService<HubProjector>().WhenListening.WaitAsync(ct);

        var sessionId = await CreateSessionAsync(ct);

        await using var db = pg.NewContext();
        var rows = await WaitForQueueAsync(n => n >= 2, ct);

        Assert.Contains(rows, r => r.Topic == HubQueueRow.SessionTopic && r.EntityId == sessionId);
        Assert.Contains(rows, r => r.Topic == HubQueueRow.SessionsTopic && r.EntityId == sessionId);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Membership_sse_wakes_on_create()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        await app.Services.GetRequiredService<HubProjector>().WhenListening.WaitAsync(ct);

        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/sessions/events");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sessionId = await CreateSessionAsync(ct);
        var ev = await ReadSseEventAsync(reader, ct);
        Assert.Equal("change", ev.Event);
        using var doc = JsonDocument.Parse(ev.Data);
        Assert.True(doc.RootElement.GetProperty("queueId").GetInt64() > 0);
        Assert.Equal("sessions", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal(sessionId, doc.RootElement.GetProperty("entityId").GetGuid());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Per_row_sse_replays_from_after()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        await app.Services.GetRequiredService<HubProjector>().WhenListening.WaitAsync(ct);

        var sessionId = await CreateSessionAsync(ct);
        await using (var db = pg.NewContext())
            await WaitForQueueAsync(n => n >= 2, ct);


        long queueId;
        await using (var db = pg.NewContext())
        {
            queueId = await db.HubQueue.Where(r => r.Topic == HubQueueRow.SessionTopic && r.EntityId == sessionId)
                .Select(r => r.Id).SingleAsync(ct);
        }

        using var client = Client(app);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{sessionId}/events?after={queueId - 1}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var ev = await ReadSseEventAsync(reader, ct);
        Assert.Equal("change", ev.Event);
        using var doc = JsonDocument.Parse(ev.Data);
        Assert.Equal(queueId, doc.RootElement.GetProperty("queueId").GetInt64());
        Assert.Equal("session", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal(sessionId, doc.RootElement.GetProperty("entityId").GetGuid());

        await app.StopAsync(ct);
    }

    private async Task<Guid> CreateSessionAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, new FakeTimeProvider());
        var team = TeamId.New();
        var applied = Assert.IsType<StoreResult.Applied>(await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "hub", "default"), ct));
        return applied.Session.Id.Value;
    }

    private async Task<List<HubQueueRow>> WaitForQueueAsync(Func<int, bool> enough, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            await using var db = pg.NewContext();
            var rows = await db.HubQueue.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
            if (enough(rows.Count))
                return rows;
            await Task.Delay(50, ct);
        }

        await using var lastDb = pg.NewContext();
        var last = await lastDb.HubQueue.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        throw new TimeoutException($"hub_queue stayed at {last.Count} rows");
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContextFactory<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddOptions<HubOptions>();
        builder.Services.AddSingleton<HubWaiters>();
        builder.Services.AddSingleton(sp => new HubProjector(
            pg.ConnectionString,
            sp.GetRequiredService<IDbContextFactory<LandbridgeDbContext>>(),
            sp.GetRequiredService<HubWaiters>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<HubProjector>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HubProjector>());
        var app = builder.Build();
        app.MapHub();
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static async Task<(string Event, string Data)> ReadSseEventAsync(StreamReader reader, CancellationToken ct)
    {
        var eventName = "message";
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                throw new EndOfStreamException("SSE stream ended before a complete event");
            if (line.Length == 0)
            {
                if (eventName == "ping" || data.Length == 0)
                {
                    eventName = "message";
                    data.Clear();
                    continue;
                }

                return (eventName, data.ToString());
            }

            if (line.StartsWith(':'))
                continue;
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line["data:".Length..].TrimStart());
            }
        }
    }
}
