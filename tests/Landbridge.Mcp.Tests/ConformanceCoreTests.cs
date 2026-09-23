using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp;
using Landbridge.Mcp.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// Operator dummy sessions Apply on Core. A Lead token is refused.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConformanceCoreTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Human_mint_creates_the_dummy_set_on_core()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        await using var app = BuildCore();
        await app.StartAsync(ct);

        var run = Guid.NewGuid();
        using var client = Client(app, await HumanTokenAsync(ct));
        using var resp = await client.PostAsJsonAsync("/core/v1/conformance", new CoreConformanceBody(
            "goose",
            [
                new CoreConformanceSpec("identity", "check (kind: identity)"),
                new CoreConformanceSpec("write", "check (kind: write)"),
                new CoreConformanceSpec("shell", "check (kind: shell)"),
            ],
            run), CoreWriteClient.Json, ct);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var reply = await resp.Content.ReadFromJsonAsync<CoreConformanceReply>(CoreWriteClient.Json, ct);
        Assert.True(reply!.Ok);
        Assert.Equal(run, reply.RunId);
        Assert.Equal(3, reply.Sessions!.Count);

        await using var db = pg.NewContext();
        var profiles = await db.Sessions.AsNoTracking().Where(s => s.TeamId == run).Select(s => s.Profile).ToListAsync(ct);
        Assert.Equal(3, profiles.Count);
        Assert.All(profiles, p => Assert.Equal("goose", p));
        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_lead_token_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        await using var app = BuildCore();
        await app.StartAsync(ct);

        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = Assert.IsType<LeadClaimResult.Claimed>(
            await tokens.ClaimLeadAsync(human.Token, TeamId.New(), ct: ct));

        using var client = Client(app, claim.Token.Token);
        using var resp = await client.PostAsJsonAsync("/core/v1/conformance",
            new CoreConformanceBody("default", [new CoreConformanceSpec("identity", "x")]),
            CoreWriteClient.Json, ct);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        await app.StopAsync(ct);
    }

    private static HttpClient Client(WebApplication app, string bearer)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal))),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private async Task<string> HumanTokenAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await new TokenService(db, TimeProvider.System).IssueHumanSessionAsync(ct)).Token;
    }

    private WebApplication BuildCore()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["ConnectionStrings:Landbridge"] = pg.ConnectionString;
        builder.Configuration["Landbridge:PublicMcpUrl"] = "https://mcp.example.com";
        builder.Configuration["Landbridge:AuthUrl"] = "https://auth.example.com";
        builder.AddPlane();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCoreWrites();
        return app;
    }
}
