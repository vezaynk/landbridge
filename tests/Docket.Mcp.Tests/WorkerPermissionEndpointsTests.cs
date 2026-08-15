using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Docket.Mcp.Tests;

/// <summary>
/// The runner-facing permission bridge: docketd posts the worker bearer at
/// <c>POST /worker/permission</c> and gets the same verdict the MCP tool would.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerPermissionEndpointsTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [SkippableFact]
    public async Task A_worker_bearer_can_open_a_permission_request_and_receive_the_verdict()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        var ct = cts.Token;

        var (caller, token) = await SeedWorkingWorkerAsync(ct);

        await using var app = BuildServer();
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var pending = client.PostAsJsonAsync("/worker/permission", new { tool = "Bash", input = """{"cmd":"ls"}""" }, ct);

        await WaitUntilBlockedAsync(caller.Task, pending, ct);

        await using (var db = pg.NewContext())
        {
            var applied = await new TaskStore(db, TimeProvider.System).AnswerPermissionAsync(
                new LeadClaim(Team), caller.Task, PermissionVerdict.Allow, ct: ct);
            Assert.IsType<StoreResult.Applied>(applied);
        }

        using var resp = await pending.WaitAsync(ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        Assert.Equal("allow", doc.RootElement.GetProperty("verdict").GetString());

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task The_endpoint_refuses_an_unauthenticated_call()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(Patience);
        await using var app = BuildServer();
        await app.StartAsync(cts.Token);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };
        using var resp = await client.PostAsJsonAsync("/worker/permission", new { tool = "Bash" }, cts.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        await app.StopAsync(cts.Token);
    }

    private async Task<(WorkerCaller Caller, string Token)> SeedWorkingWorkerAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "needs permission", CompletionMode.Lead, null), ct);
        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
            new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" }),
            instance, ct));
        var token = (await new TokenService(db, TimeProvider.System)
            .MintWorkerTokenAsync(Team, created.Task.Id, instance, ct)).Token;
        return (new WorkerCaller(Team, created.Task.Id, instance), token);
    }

    private async Task WaitUntilBlockedAsync(TaskId task, Task pending, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (pending.IsCompleted)
                Assert.Fail("the permission POST returned before the task blocked");
            await using var db = pg.NewContext();
            var state = await db.Tasks.AsNoTracking()
                .Where(t => t.Id == task.Value)
                .Select(t => t.State)
                .SingleAsync(ct);
            if (state == TaskState.BlockedOnInput)
                return;
            await Task.Delay(10, ct);
        }
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Docket:PermissionPollIntervalMs"] = "10";

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddDocketStore();
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(DocketAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DocketAuthenticationHandler>(
                DocketAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapWorkerPermissionEndpoint();
        return app;
    }
}
