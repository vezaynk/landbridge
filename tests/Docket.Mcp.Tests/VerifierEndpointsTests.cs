using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp;
using Docket.Mcp.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Docket.Mcp.Tests;

/// <summary>
/// The verifier webhook over real HTTP (spec §5, §10): the real
/// <see cref="VerifierEndpoints"/> hosted on loopback Kestrel with the real
/// opaque-token auth handler in the loop. Tasks are driven to <c>verifying</c>
/// control-plane-internally (workers are dispatched, never claimants — §5), then
/// a human-provisioned verifier credential polls <c>/verify/pending</c> and posts
/// verdicts to <c>/verify/{id}</c>, exactly as an automated verifier would.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VerifierEndpointsTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();

    private static MachineSnapshot Machine() =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    [SkippableFact]
    public async Task Post_verify_accept_completes_the_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        using var client = Client(app);

        var id = await SeedVerifyingTaskAsync(CompletionMode.Automated, "r-accept", ct);
        var verifier = await ProvisionVerifierAsync(ct);

        var resp = await PostVerdictAsync(client, verifier, id, "accept", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("Completed", await ReadStateAsync(resp, ct));

        await using (var v = pg.NewContext())
            Assert.Equal(TaskState.Completed,
                (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value, ct)).State);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Post_verify_fail_requeues_until_retries_exhaust_then_rejects()
    {
        // §6: the verification counter — not the infrastructure one — drives
        // rejection. Default limit is 3, so two fails requeue to submitted and the
        // third exhausts to rejected. The count is driven honestly: each fail is a
        // real HTTP verdict, with a control-plane redispatch + report between them
        // to bring the task back to verifying.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        using var client = Client(app);

        var id = await SeedVerifyingTaskAsync(CompletionMode.Automated, "r-1", ct);
        var verifier = await ProvisionVerifierAsync(ct);

        // Fail #1 and #2: retries remain, so the task returns to submitted.
        var r1 = await PostVerdictAsync(client, verifier, id, "fail", ct);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal("Submitted", await ReadStateAsync(r1, ct));
        await DriveBackToVerifyingAsync(id, "r-2", ct);

        var r2 = await PostVerdictAsync(client, verifier, id, "fail", ct);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal("Submitted", await ReadStateAsync(r2, ct));
        await DriveBackToVerifyingAsync(id, "r-3", ct);

        // Fail #3: retries exhausted → rejected (§6, §9 check 8).
        var r3 = await PostVerdictAsync(client, verifier, id, "fail", ct);
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
        Assert.Equal("Rejected", await ReadStateAsync(r3, ct));

        await using (var v = pg.NewContext())
        {
            var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value, ct);
            Assert.Equal(TaskState.Rejected, row.State);
            Assert.Equal(3, row.VerificationFailures);
            Assert.Equal(0, row.InfrastructureRequeues); // requeues here were verification, not infra
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Non_verifier_tokens_are_forbidden_on_both_endpoints()
    {
        // §5 invariant, applied at the door: only the verifier credential reaches
        // the verdict path or its read scope. A worker or lead token authenticates
        // fine (it is a valid principal) but is refused 403 here.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        using var client = Client(app);

        var worker = await MintWorkerTokenAsync(ct);
        var lead = await MintLeadTokenAsync(ct);
        var someTask = Guid.NewGuid().ToString();

        foreach (var token in new[] { worker, lead })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await GetPendingAsync(client, token, ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await PostVerdictAsync(client, token, someTask, "accept", ct)).StatusCode);
        }

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task Pending_lists_automated_verifying_tasks_with_their_reference_never_review_mode()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        using var client = Client(app);

        var automated = await SeedVerifyingTaskAsync(CompletionMode.Automated, "auto-ref", ct);
        var review = await SeedVerifyingTaskAsync(CompletionMode.Review, "review-ref", ct);
        var verifier = await ProvisionVerifierAsync(ct);

        var resp = await GetPendingAsync(client, verifier, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var pending = await ReadPendingAsync(resp, ct);
        var view = Assert.Single(pending);
        Assert.Equal(automated.Value, view.TaskId);
        Assert.Equal("auto-ref", view.ResultReference);
        Assert.DoesNotContain(pending, v => v.TaskId == review.Value);

        await app.StopAsync(ct);
    }

    // ── HTTP helpers ─────────────────────────────────────────────────────────

    private static HttpClient Client(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };

    private static async Task<HttpResponseMessage> GetPendingAsync(HttpClient client, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/verify/pending");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req, ct);
    }

    private static async Task<HttpResponseMessage> PostVerdictAsync(
        HttpClient client, string token, TaskId id, string verdict, CancellationToken ct) =>
        await PostVerdictAsync(client, token, id.ToString(), verdict, ct);

    private static async Task<HttpResponseMessage> PostVerdictAsync(
        HttpClient client, string token, string taskId, string verdict, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/verify/{taskId}")
        {
            Content = JsonContent.Create(new { verdict }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req, ct);
    }

    private static async Task<string?> ReadStateAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("state").GetString();
    }

    private static async Task<List<VerifyingTaskView>> ReadPendingAsync(HttpResponseMessage resp, CancellationToken ct) =>
        JsonSerializer.Deserialize<List<VerifyingTaskView>>(
            await resp.Content.ReadAsStringAsync(ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    // ── Control-plane-internal setup (workers are dispatched, never claimants) ─

    private async Task<TaskId> SeedVerifyingTaskAsync(CompletionMode mode, string resultRef, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "the suite is green", mode, null, TeamBudgetRemains: true), ct);
        var id = created.Task.Id;
        var instance = WorkerInstanceId.New();
        var dispatched = (StoreResult.Applied)await store.DispatchNextAsync(Machine(), instance, ct);
        Assert.Equal(id, dispatched.Task.Id); // only one submitted row, so this is deterministic
        await store.ApplyAsync(id, new ReportResult(new WorkerCaller(Team, id, instance), resultRef), ct);
        return id;
    }

    private async Task DriveBackToVerifyingAsync(TaskId id, string resultRef, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var instance = WorkerInstanceId.New();
        var dispatched = (StoreResult.Applied)await store.DispatchNextAsync(Machine(), instance, ct);
        Assert.Equal(id, dispatched.Task.Id);
        await store.ApplyAsync(id, new ReportResult(new WorkerCaller(Team, id, instance), resultRef), ct);
    }

    private async Task<string> ProvisionVerifierAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await new TokenService(db, TimeProvider.System).ProvisionVerifierAsync(ct)).Token;
    }

    private async Task<string> MintWorkerTokenAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, TimeProvider.System);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(Team), Team, "x", CompletionMode.Automated, null, TeamBudgetRemains: true), ct);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance, ct);
        return (await new TokenService(db, TimeProvider.System)
            .MintWorkerTokenAsync(Team, created.Task.Id, instance, ct)).Token;
    }

    private async Task<string> MintLeadTokenAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var tokens = new TokenService(db, TimeProvider.System);
        var human = await tokens.IssueHumanSessionAsync(ct);
        var claim = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, Team, ct: ct);
        return claim.Token.Token;
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TaskStore>();
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
        app.MapVerifierEndpoints();
        return app;
    }
}
