using System.Net;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The three hosts the gateway split into, booted from their own <c>Program.cs</c>
/// rather than a reconstruction of it (spec §13, <c>hub-and-write-queue.md</c>).
///
/// <para>They went in as scaffolding: the fused plane still serves everything, so
/// nothing exercised them and a host could have been broken — a service left
/// unregistered, a pipeline stage in the wrong order — without any suite noticing.
/// These assert the two things that make each one a host rather than a project: it
/// starts with its real dependency graph, and it exposes its own surface and not a
/// neighbour's.</para>
///
/// <para>The tool split is the contract worth pinning. LeadMCP and WorkerMCP differ
/// only in which tools they register, so a copy-paste between the two entry points
/// is both the easiest mistake to make here and the one with the widest blast
/// radius — a worker reaching Lead tools is a §5 authority violation, not a bug in
/// a listing.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SplitHostSurfaceTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableTheory]
    [InlineData("lead")]
    [InlineData("worker")]
    [InlineData("dashboard")]
    public async Task Each_host_starts_with_its_real_dependency_graph(string which)
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        using var host = Start(which);

        // /health is ServiceDefaults', mapped after the container is built: reaching it
        // means every singleton and hosted service this host declares actually resolved.
        using var health = await host.Client.GetAsync("/health", cts.Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [SkippableTheory]
    [InlineData("lead")]
    [InlineData("worker")]
    public async Task An_mcp_host_refuses_an_unauthenticated_caller(string which)
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        using var host = Start(which);

        using var anonymous = await host.Client.PostAsync(
            "/", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), cts.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var challenge = Assert.Single(anonymous.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("resource_metadata", challenge.Parameter ?? "");
    }

    /// <summary>
    /// Each MCP host lists its own tools and none of the other's. Asserted by name
    /// against the real registration, so adding a tool to the wrong entry point fails
    /// here rather than at whatever a worker manages to call in production.
    /// </summary>
    [SkippableFact]
    public async Task Lead_and_worker_hosts_expose_disjoint_tool_surfaces()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var lead = await ToolNamesAsync("lead", ct);
        var worker = await ToolNamesAsync("worker", ct);

        Assert.Contains("create_session", lead);
        Assert.Contains("bind_machine", lead);
        Assert.DoesNotContain("report_result", lead);
        Assert.DoesNotContain("register_service", lead);

        Assert.Contains("report_result", worker);
        Assert.Contains("register_service", worker);
        Assert.DoesNotContain("create_session", worker);
        Assert.DoesNotContain("bind_machine", worker);

        // report_friction is deliberately on both — either role may report it (§14).
        Assert.Contains("report_friction", lead);
        Assert.Contains("report_friction", worker);
    }

    private async Task<IReadOnlyList<string>> ToolNamesAsync(string which, CancellationToken ct)
    {
        using var host = Start(which);
        // A real MCP session over the host's own pipeline, authenticated as the role
        // that host serves — a tool listing is only meaningful past RequireAuthorization.
        // The transport rides the test server's own handler rather than a socket, so
        // this is the host's real middleware chain and not a second copy of it.
        var bearer = which == "lead" ? await LeadBearerAsync(ct) : await WorkerBearerAsync(ct);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = host.BaseAddress,
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            host.Client);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return [.. tools.Select(t => t.Name)];
    }

    private async Task<string> LeadBearerAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claimed = Assert.IsType<LeadClaimResult.Claimed>(
            await tokens.ClaimLeadAsync(human.Token, TeamId.New(), takeover: false, ct));
        return claimed.Token.Token;
    }

    private async Task<string> WorkerBearerAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var clock = TimeProvider.System;
        var tokens = new TokenService(db, clock);
        var team = TeamId.New();
        var store = new SessionStore(db, clock);
        var session = Assert.IsType<StoreResult.Applied>(await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "surface probe", "default"), ct)).Session.Id;
        var instance = WorkerInstanceId.New();
        db.WorkerInstances.Add(new WorkerInstanceRow
        {
            Id = instance.Value,
            SessionId = session.Value,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return (await tokens.MintWorkerTokenAsync(team, session, instance, ct)).Token;
    }

    /// <summary>One started host: its client, its base address, and what to dispose.</summary>
    private sealed record Started(HttpClient Client, Uri BaseAddress, IDisposable Factory) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private Started Start(string which) => which switch
    {
        "lead" => Start<LeadMcpHost>(),
        "worker" => Start<WorkerMcpHost>(),
        "dashboard" => Start<DashboardHost>(),
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "unknown host"),
    };

    private Started Start<TEntry>() where TEntry : class
    {
        var factory = new WebApplicationFactory<TEntry>().WithWebHostBuilder(b =>
        {
            // Development so ServiceDefaults maps /health; the rest is the real config
            // path these hosts read in the dev loop.
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:Landbridge", pg.ConnectionString);
            b.UseSetting("Landbridge:PublicMcpUrl", "https://mcp.example.com");
            b.UseSetting("Landbridge:AuthUrl", "https://auth.example.com");
        });
        return new Started(factory.CreateClient(), factory.Server.BaseAddress, factory);
    }
}
