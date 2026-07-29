using System.Net.Http.Headers;
using System.Text.RegularExpressions;
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
using Docket.Verifier;

namespace Docket.Verifier.Tests;

/// <summary>
/// The reference verifier against the real webhook (spec §5, §6, §10): the real
/// <see cref="VerifierEndpoints"/> hosted on loopback Kestrel with the real
/// opaque-token auth handler and a Postgres store, driven by the real
/// <see cref="VerifierClient"/> / <see cref="VerifierLoop"/> — the same tick the
/// console <see cref="Program"/> runs on a timer. Tasks are driven to
/// <c>verifying</c> control-plane-internally (workers are dispatched, never
/// claimants — §5), then a human-provisioned verifier credential rules on them.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VerifierLoopEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();

    private static MachineSnapshot Machine() =>
        new("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    // ── The reference tick: accept ────────────────────────────────────────────

    [SkippableFact]
    public async Task Tick_accepts_a_matching_reference_and_completes_the_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);

        var id = await SeedVerifyingTaskAsync(CompletionMode.Automated, "r-accept", ct);
        var verifier = await ProvisionVerifierAsync(ct);

        using var http = HttpFor(app, verifier);
        var tick = await VerifierLoop.RunTickAsync(new VerifierClient(http), AcceptAll(), Silent(), ct);

        Assert.Equal(TickStatus.Polled, tick.Status);
        Assert.Equal(1, tick.VerdictsApplied);
        Assert.Equal(TaskState.Completed, await ReadStateAsync(id, ct));

        await app.StopAsync(ct);
    }

    // ── The reference tick: fail (pattern miss → the engine's fail outcome) ─────

    [SkippableFact]
    public async Task Tick_fails_a_non_matching_reference_and_requeues_while_retries_remain()
    {
        // §6: a fail with retries remaining returns the task to submitted (the
        // engine's fail outcome), not rejected. The verdict itself is still applied
        // (200), so the tick counts it.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);

        var id = await SeedVerifyingTaskAsync(CompletionMode.Automated, "unremarkable-ref", ct);
        var verifier = await ProvisionVerifierAsync(ct);

        using var http = HttpFor(app, verifier);
        // Policy that this reference cannot satisfy → the verifier decides "fail".
        var policy = new VerdictPolicy(new Regex("^ACCEPT-ONLY-THIS$", RegexOptions.CultureInvariant));
        var tick = await VerifierLoop.RunTickAsync(new VerifierClient(http), policy, Silent(), ct);

        Assert.Equal(TickStatus.Polled, tick.Status);
        Assert.Equal(1, tick.VerdictsApplied);
        Assert.Equal(TaskState.Submitted, await ReadStateAsync(id, ct));

        await app.StopAsync(ct);
    }

    // ── A concurrent verdict (409) is benign ────────────────────────────────────

    [SkippableFact]
    public async Task A_verdict_against_an_already_ruled_task_is_a_benign_409()
    {
        // The verifier polled a task, but someone (or an earlier tick) ruled it
        // first: the second verdict lands on a terminal task → 409
        // TerminalStatesAreFinal (§10's explicit "gone", never a silent success).
        // The client surfaces it as a benign Conflict — the loop logs and moves on.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);

        var id = await SeedVerifyingTaskAsync(CompletionMode.Automated, "r-accept", ct);
        var verifier = await ProvisionVerifierAsync(ct);

        using var http = HttpFor(app, verifier);
        var client = new VerifierClient(http);

        var first = await client.PostVerdictAsync(id.Value, "accept", ct);
        Assert.Equal(VerdictStatus.Applied, first.Status);
        Assert.Equal("Completed", first.Detail);

        var second = await client.PostVerdictAsync(id.Value, "accept", ct);
        Assert.Equal(VerdictStatus.Conflict, second.Status);
        Assert.Contains("TerminalStatesAreFinal", second.Detail);

        await app.StopAsync(ct);
    }

    // ── An unknown task (404) is benign ─────────────────────────────────────────

    [SkippableFact]
    public async Task A_verdict_for_an_unknown_task_is_a_benign_404()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);

        var verifier = await ProvisionVerifierAsync(ct);
        using var http = HttpFor(app, verifier);

        var result = await new VerifierClient(http).PostVerdictAsync(Guid.NewGuid(), "accept", ct);
        Assert.Equal(VerdictStatus.NotFound, result.Status);

        await app.StopAsync(ct);
    }

    // ── Auth: a bad token, and a valid non-verifier token, both fail the poll ───

    [SkippableFact]
    public async Task A_garbage_token_makes_the_tick_report_an_auth_failure()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);

        using var http = HttpFor(app, "dkt_v_not-a-real-token");
        var tick = await VerifierLoop.RunTickAsync(new VerifierClient(http), AcceptAll(), Silent(), ct);

        Assert.Equal(TickStatus.Unauthorized, tick.Status);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task A_valid_non_verifier_token_is_also_an_auth_failure_to_the_verifier()
    {
        // §5 door: a lead credential authenticates fine but is 403'd on the verifier
        // webhook. The client folds 403 into the same auth-failure signal as 401.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);

        var lead = await MintLeadTokenAsync(ct);
        using var http = HttpFor(app, lead);
        var tick = await VerifierLoop.RunTickAsync(new VerifierClient(http), AcceptAll(), Silent(), ct);

        Assert.Equal(TickStatus.Unauthorized, tick.Status);

        await app.StopAsync(ct);
    }

    [SkippableFact]
    public async Task The_loop_exits_nonzero_after_three_consecutive_auth_failures()
    {
        // A wrong token will not heal itself: after MaxConsecutiveAuthFailures polls
        // the loop gives up with a nonzero exit for the operator to rotate + restart.
        // A tiny interval keeps the test fast; the exit code does not depend on
        // timing, only on three refused polls.
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);

        using var http = HttpFor(app, "dkt_v_still-not-real");
        var exit = await Program.RunAsync(
            new VerifierClient(http), AcceptAll(), Silent(),
            TimeSpan.FromMilliseconds(1), TimeProvider.System, ct);

        Assert.Equal(Program.AuthFailureExitCode, exit);

        await app.StopAsync(ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static VerdictPolicy AcceptAll() =>
        new(new Regex(VerifierConfig.DefaultAcceptPattern, RegexOptions.CultureInvariant));

    private static VerifierLog Silent() => new(TextWriter.Null, TextWriter.Null);

    private static HttpClient HttpFor(WebApplication app, string token)
    {
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First(u => u.StartsWith("http://"))) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private async Task<TaskState> ReadStateAsync(TaskId id, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value, ct)).State;
    }

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

    private async Task<string> ProvisionVerifierAsync(CancellationToken ct)
    {
        await using var db = pg.NewContext();
        return (await new TokenService(db, TimeProvider.System).ProvisionVerifierAsync(ct)).Token;
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
