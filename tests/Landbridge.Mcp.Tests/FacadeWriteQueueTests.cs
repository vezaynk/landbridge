using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// A façade with <c>Landbridge:WriteQueue</c> on, which is the configuration the dev loop
/// runs and no test did.
///
/// <para>The flag gates <c>QueueWaitAsync</c> in the Lead and worker tools, so with it
/// unset every façade mutation goes straight to <see cref="SessionStore"/> and the queued
/// path is never entered. <c>/core/v1</c> with <c>Prefer: respond-async</c> is covered
/// because it enqueues regardless of the flag — but that is the HTTP edge, not the tool
/// surface an agent actually calls, and the two take different routes into the queue.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FacadeWriteQueueTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A Lead tool call becomes a durable command, and Core applying it later is what
    /// creates the session. Nothing drains while the tool runs, so this also exercises the
    /// wait giving up on its own clock and answering with what it has.
    /// </summary>
    [SkippableFact]
    public async Task A_lead_tool_enqueues_and_core_applies_it()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var team = TeamId.New();
        var bearer = await LeadBearerAsync(team, ct);

        using var host = StartLeadWithQueue();
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = host.BaseAddress,
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            host.Client);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

        var result = await client.CallToolAsync(
            "create_session",
            new Dictionary<string, object?>
            {
                ["teamId"] = team.Value.ToString("D"),
                ["description"] = "pnpm test",
                ["profile"] = "default",
            },
            cancellationToken: ct);
        Assert.True(result.IsError is not true, $"create_session failed: {result.IsError}");

        // Accepted and durable, but not yet applied: no Core is draining.
        Guid commandId;
        await using (var db = pg.NewContext())
        {
            var queued = await db.Commands.AsNoTracking().SingleAsync(ct);
            Assert.Equal(CommandRow.Queued, queued.Status);
            Assert.Equal(CommandRow.CreateSession, queued.Kind);
            Assert.False(await db.Sessions.AnyAsync(ct), "the façade must not have applied it itself");
            commandId = queued.Id;
        }

        var drain = new CommandDrain(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CommandDrain>.Instance);
        Assert.True(await drain.DrainOneAsync(ct));

        await using (var db = pg.NewContext())
        {
            var applied = await db.Commands.AsNoTracking().SingleAsync(c => c.Id == commandId, ct);
            Assert.Equal(CommandRow.Applied, applied.Status);
            Assert.True(await db.Sessions.AnyAsync(ct));
        }
    }

    private async Task<string> LeadBearerAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claimed = Assert.IsType<LeadClaimResult.Claimed>(
            await tokens.ClaimLeadAsync(human.Token, team, takeover: false, ct));
        return claimed.Token.Token;
    }

    private sealed record Started(
        HttpClient Client, Uri BaseAddress, IDisposable Factory, IServiceProvider Services) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private Started StartLeadWithQueue()
    {
        var factory = new WebApplicationFactory<LeadMcpHost>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Landbridge", pg.ConnectionString);
            b.UseSetting("Landbridge:PublicMcpUrl", "https://mcp.example.com");
            b.UseSetting("Landbridge:AuthUrl", "https://auth.example.com");
            b.UseSetting("Landbridge:WriteQueue", "true");
            // Short, because nothing drains during the call: the tool should answer with
            // the accepted command rather than hold the caller.
            b.UseSetting("Landbridge:WriteQueueWaitMs", "300");
        });
        return new Started(factory.CreateClient(), factory.Server.BaseAddress, factory, factory.Services);
    }
}
