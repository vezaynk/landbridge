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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Landbridge.Mcp.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CoreWritePreferTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Prefer_respond_async_returns_202_and_leaves_the_command_queued()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var team = TeamId.New();
        var token = await ClaimLeadAsync(team, ct);

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/core/v1/create-session")
        {
            Content = JsonContent.Create(new CoreCreateSessionBody(
                team.Value.ToString("D"), "pnpm test", "default"), options: CoreWriteClient.Json),
        };
        req.Headers.TryAddWithoutValidation("Prefer", "respond-async");
        using var resp = await client.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal("respond-async", resp.Headers.GetValues("Preference-Applied").Single());
        var reply = await resp.Content.ReadFromJsonAsync<CoreStoreReply>(CoreWriteClient.Json, ct);
        Assert.Equal("accepted", reply!.Status);
        Assert.False(string.IsNullOrEmpty(reply.CommandId));
        Assert.False(string.IsNullOrEmpty(reply.SessionId));

        await using (var db = pg.NewContext())
        {
            var cmd = await db.Commands.AsNoTracking()
                .SingleAsync(c => c.Id == Guid.Parse(reply.CommandId!), ct);
            Assert.Equal(CommandRow.Queued, cmd.Status);
            Assert.False(await db.Sessions.AnyAsync(s => s.Id == Guid.Parse(reply.SessionId!), ct));
        }

        var drain = new CommandDrain(
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommandDrain>.Instance);
        Assert.True(await drain.DrainOneAsync(ct));

        await using (var db = pg.NewContext())
        {
            Assert.True(await db.Sessions.AnyAsync(s => s.Id == Guid.Parse(reply.SessionId!), ct));
            var cmd = await db.Commands.AsNoTracking()
                .SingleAsync(c => c.Id == Guid.Parse(reply.CommandId!), ct);
            Assert.Equal(CommandRow.Applied, cmd.Status);
        }

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

    private WebApplication BuildServer()
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
