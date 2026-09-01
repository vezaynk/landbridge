using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// Lead inbox HTTP: JSON snapshot and SSE of the same occupancy-aware view.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LeadInboxEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task A_fresh_team_snapshot_is_empty()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var team = TeamId.New();
        var token = await ClaimLeadAsync(team, ct);

        using var client = Client(app, token);
        using var resp = await client.GetAsync($"/lead/inbox?teamId={team.Value}", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_question_appears_on_the_json_snapshot()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var team = TeamId.New();
        var token = await ClaimLeadAsync(team, ct);
        var (sessionId, messageId) = await SeedQuestionAsync(team, ct);

        using var client = Client(app, token);
        using var resp = await client.GetAsync($"/lead/inbox?teamId={team.Value}", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(sessionId.ToString(), item.GetProperty("sessionId").GetString());
        Assert.Equal("question", item.GetProperty("kind").GetString());
        Assert.Equal(messageId.ToString(), item.GetProperty("messageId").GetString());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Another_teams_question_does_not_leak()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var a = TeamId.New();
        var other = TeamId.New();
        var b = await ClaimLeadAsync(other, ct);
        await SeedQuestionAsync(a, ct);

        using var client = Client(app, b);
        using var resp = await client.GetAsync($"/lead/inbox?teamId={other.Value}", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_worker_bearer_is_forbidden_and_unauthenticated_is_401()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var team = TeamId.New();
        var worker = await MintWorkerAsync(team, ct);

        using (var client = Client(app, worker))
        using (var resp = await client.GetAsync($"/lead/inbox?teamId={team.Value}", ct))
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        using (var anon = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) })
        using (var resp = await anon.GetAsync("/lead/inbox", ct))
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Sse_sends_a_snapshot_and_wakes_when_a_question_lands()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        await app.Services.GetRequiredService<SessionEventFanout>().WhenListening.WaitAsync(ct);

        var team = TeamId.New();
        var token = await ClaimLeadAsync(team, ct);

        using var client = Client(app, token);
        client.Timeout = Timeout.InfiniteTimeSpan;
        Guid sessionId, messageId;
        using (var req = new HttpRequestMessage(HttpMethod.Get, $"/lead/inbox/events?teamId={team.Value}"))
        {
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var first = await ReadSseEventAsync(reader, ct);
            Assert.Equal("snapshot", first.Event);
            using (var empty = JsonDocument.Parse(first.Data))
                Assert.Equal(0, empty.RootElement.GetProperty("items").GetArrayLength());

            (sessionId, messageId) = await SeedQuestionAsync(team, ct);
            var woken = await ReadSseEventAsync(reader, ct);
            Assert.Equal("snapshot", woken.Event);
        }

        using var jsonResp = await client.GetAsync($"/lead/inbox?teamId={team.Value}", ct);
        jsonResp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await jsonResp.Content.ReadAsStringAsync(ct));
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(sessionId.ToString(), item.GetProperty("sessionId").GetString());
        Assert.Equal("question", item.GetProperty("kind").GetString());
        Assert.Equal(messageId.ToString(), item.GetProperty("messageId").GetString());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Session_filter_returns_only_that_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var team = TeamId.New();
        var token = await ClaimLeadAsync(team, ct);
        var (a, _) = await SeedQuestionAsync(team, ct);
        var (b, _) = await SeedQuestionAsync(team, ct);

        using var client = Client(app, token);
        using var all = await client.GetAsync($"/lead/inbox?teamId={team.Value}", ct);
        using var allDoc = JsonDocument.Parse(await all.Content.ReadAsStringAsync(ct));
        Assert.Equal(2, allDoc.RootElement.GetProperty("items").GetArrayLength());

        using var one = await client.GetAsync($"/lead/inbox?teamId={team.Value}&sessionId={a}", ct);
        using var oneDoc = JsonDocument.Parse(await one.Content.ReadAsStringAsync(ct));
        var item = Assert.Single(oneDoc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(a.ToString(), item.GetProperty("sessionId").GetString());

        using var bad = await client.GetAsync($"/lead/inbox?teamId={team.Value}&sessionId=not-a-guid", ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        await app.StopAsync(ct);
    }

    private async Task<string> ClaimLeadAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = Assert.IsType<LeadClaimResult.Claimed>(await tokens.ClaimLeadAsync(human.Token, team, ct: ct));
        return claim.Token.Token;
    }

    private async Task<(Guid SessionId, Guid MessageId)> SeedQuestionAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "ask", "default"), ct);
        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
            new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance, ct));
        var asked = Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            created.Session.Id,
            new RequestInput(new WorkerCaller(team, created.Session.Id, instance),
                InputRequestKind.Question, "which DB?"), ct));
        return (created.Session.Id.Value, asked.Session.MessageId!.Value);
    }

    private async Task<string> MintWorkerAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "worker", "default"), ct);
        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
            new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance, ct));
        return (await new TokenService(db, TimeProvider.System)
            .MintWorkerTokenAsync(team, created.Session.Id, instance, ct)).Token;
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(sp => new SessionEventFanout(
            pg.ConnectionString, sp.GetRequiredService<ILogger<SessionEventFanout>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionEventFanout>());

        builder.Services.AddAuthentication(LandbridgeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LandbridgeAuthenticationHandler>(
                LandbridgeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapLeadInbox();
        return app;
    }

    private static HttpClient Client(WebApplication app, string bearer)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
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
