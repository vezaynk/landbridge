using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
using Docket.Mcp.Skills;
using Docket.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Docket.Mcp.Tests;

/// <summary>
/// The skill bundle (spec §14) ships as MCP resources (§10): a connected agent
/// lists and reads its guidance over the same authenticated MCP pipeline the tools
/// use. These drive the real server (auth scheme, tools, and
/// <c>WithResources&lt;SkillResources&gt;()</c>) on a loopback Kestrel port with the
/// MCP C# SDK client — the same shape as <see cref="LeadWorkerEndToEndTests"/>.
///
/// Scoping is advisory (see <see cref="SkillResources"/>): all four skills list and
/// read for any authenticated principal, mirroring how <c>list_tools</c> returns
/// every tool regardless of caller. The last test pins that contract.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SkillResourcesEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_worker_lists_every_skill_and_reads_its_own_verbatim()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var workerToken = await MintWorkerTokenAsync(TeamId.New(), ct);

        await using var worker = await ConnectAsync(baseUri, workerToken, ct);

        // The whole bundle is discoverable on connect (§10) — all four resources,
        // with stable docket:// URIs and a Markdown media type.
        var listed = await worker.ListResourcesAsync(cancellationToken: ct);
        var byUri = listed.ToDictionary(r => r.Uri);
        Assert.Contains(SkillBundle.WorkerUri, byUri.Keys);
        Assert.Contains(SkillBundle.LeadUri, byUri.Keys);
        Assert.Contains(SkillBundle.EnrollUri, byUri.Keys);
        Assert.Contains(SkillBundle.RunnerConfigUri, byUri.Keys);
        Assert.Equal(SkillBundle.Markdown, byUri[SkillBundle.WorkerUri].MimeType);

        // The worker skill is served byte-for-byte from the embedded bundle, and the
        // URI→content mapping is the intended one (its own frontmatter, not another
        // skill's).
        var workerText = await ReadTextAsync(worker, SkillBundle.WorkerUri, ct);
        Assert.Equal(SkillBundle.Worker, workerText);
        Assert.Contains("name: docket-worker", workerText);

        // The runner-config reference is served to the worker too (its MCP config is
        // documented there).
        var runnerText = await ReadTextAsync(worker, SkillBundle.RunnerConfigUri, ct);
        Assert.Equal(SkillBundle.RunnerConfig, runnerText);
        Assert.Contains("runner config", runnerText);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_lead_reads_the_lead_skill_verbatim()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var leadToken = await MintLeadTokenAsync(TeamId.New(), ct);

        await using var lead = await ConnectAsync(baseUri, leadToken, ct);

        var listed = await lead.ListResourcesAsync(cancellationToken: ct);
        Assert.Contains(listed, r => r.Uri == SkillBundle.LeadUri);

        var leadText = await ReadTextAsync(lead, SkillBundle.LeadUri, ct);
        Assert.Equal(SkillBundle.Lead, leadText);
        Assert.Contains("name: docket-lead", leadText);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Scoping_is_advisory_a_worker_may_also_read_the_lead_skill()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUri = new Uri(app.Urls.First(u => u.StartsWith("http://")) + "/");

        var workerToken = await MintWorkerTokenAsync(TeamId.New(), ct);

        await using var worker = await ConnectAsync(baseUri, workerToken, ct);

        // Deliberate contract: resource scoping is advisory, not a hard ACL. A worker
        // can read the Lead skill, exactly as list_tools shows a worker every tool.
        // The dispatch prompt — not the resource server — points each agent at its
        // own skill (spec §3: "Skills and prompts are advisory and can be bypassed").
        var leadText = await ReadTextAsync(worker, SkillBundle.LeadUri, ct);
        Assert.Equal(SkillBundle.Lead, leadText);

        await app.StopAsync(ct);
    }

    private static async Task<string> ReadTextAsync(McpClient client, string uri, CancellationToken ct)
    {
        var result = await client.ReadResourceAsync(uri, cancellationToken: ct);
        return Assert.Single(result.Contents.OfType<TextResourceContents>()).Text;
    }

    // A dispatched worker instance's minted token — mirrors LeadWorkerEndToEndTests.
    private async Task<string> MintWorkerTokenAsync(TeamId team, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "seed", CompletionMode.Lead, null, TeamBudgetRemains: true), ct);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot("m1", true, false, new HashSet<string> { "default" }), instance, ct);
        var tokens = new TokenService(db, TimeProvider.System);
        return (await tokens.MintWorkerTokenAsync(team, created.Task.Id, instance, ct)).Token;
    }

    // A human session claiming the Lead of a Team — mirrors LeadWorkerEndToEndTests.
    private async Task<string> MintLeadTokenAsync(TeamId team, CancellationToken ct)
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

        // Mirrors Program.cs wiring, pointed at the fixture's ephemeral database.
        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddDocketStore();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>(); // §8.4: WorkerTools.open_preview
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunnerConnectionRegistry>();
        builder.Services.AddDocketForwarding(); // §8.3: WorkerTools needs the forward orchestrator
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(DocketAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DocketAuthenticationHandler>(
                DocketAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>()
            .WithResources<SkillResources>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp().RequireAuthorization();
        return app;
    }

    private static async Task<McpClient> ConnectAsync(Uri endpoint, string bearer, CancellationToken ct)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }
}
