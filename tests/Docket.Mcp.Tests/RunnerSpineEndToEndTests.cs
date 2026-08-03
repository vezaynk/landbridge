using Docket.Contracts;
using Docket.ControlPlane;
using Docket.ControlPlane.Auth;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Mcp;
using Docket.Mcp.Auth;
using Docket.Mcp.Tools;
using Docket.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Docket.Mcp.Tests;

/// <summary>
/// The whole spine over the wire (spec §10): the real control-plane host — auth
/// scheme, connection registry, dispatch loop, and the <c>/runner</c> WebSocket
/// endpoint — hosted on loopback Kestrel, with the real docketd
/// <see cref="WebSocketControlPlaneChannel"/> dialing in. A submitted task flows
/// out as a <c>DispatchCommand</c> down the socket the runner dialed, and a
/// <c>started</c> event flows back. The harness stays fake — no real claude -p.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RunnerSpineEndToEndTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Submitted_task_dispatches_to_a_dialed_in_runner_and_a_started_event_flows_back()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));
        var wsUrl = new Uri(baseUrl.Replace("http://", "ws://") + "/runner");

        var team = TeamId.New();

        // ── Enroll a machine and seed one submitted task ────────────────────
        string machineToken;
        TaskId taskId;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var enrollment = await tokens.IssueEnrollmentTokenAsync(ct);
            var creds = await tokens.ExchangeEnrollmentAsync(
                enrollment.Token, new MachineDeclaration("box-1", "test", "macos", "standard"), ct);
            machineToken = creds!.Access.Token;

            var store = new TaskStore(db, TimeProvider.System);
            var created = (StoreResult.Applied)await store.CreateAsync(
                new CreateTask(new LeadClaim(team), team, "the suite is green", CompletionMode.Lead, null, TeamBudgetRemains: true), ct);
            taskId = created.Task.Id;
        }

        // ── docketd dials in with the real channel ──────────────────────────
        var dispatched = new TaskCompletionSource<DispatchCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new WebSocketControlPlaneChannel(wsUrl, machineToken, TimeProvider.System);
        channel.Start((command, _) =>
        {
            if (command is DispatchCommand d)
                dispatched.TrySetResult(d);
            return Task.CompletedTask;
        });

        Assert.True(await WaitUntilAsync(() => channel.IsConnected, TimeSpan.FromSeconds(15)), "runner never connected");

        // The machine announces readiness + its profiles; the endpoint nudges dispatch.
        Assert.True(await channel.HeartbeatAsync(
            new MachineHeartbeat("box-1", Ready: true, UnderBackPressure: false,
                new SystemLoad(0, 0, 0), RunningTasks: 0, ["default"], DateTimeOffset.UtcNow), ct));

        // ── A DispatchCommand for the task arrives down the dialed socket ───
        var command = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(taskId, command.Task);
        Assert.Equal("default", command.Profile);
        Assert.NotEqual("", command.WorkerToken);

        // The control plane committed submitted → working before sending (§10).
        await using (var db = pg.NewContext())
        {
            var state = await new TaskStore(db, TimeProvider.System).GetStateAsync(taskId, ct);
            Assert.Equal(TaskState.Working, state);
        }

        // The worker token minted for this dispatch validates as this task's worker.
        await using (var db = pg.NewContext())
        {
            var principal = await new TokenService(db, TimeProvider.System).ValidateAsync(command.WorkerToken, ct);
            var worker = Assert.IsType<Principal.Worker>(principal);
            Assert.Equal(taskId, worker.Caller.Task);
        }

        // ── started flows back up the same socket and is accepted ───────────
        Assert.True(await channel.PublishAsync(new StartedEvent(taskId, DateTimeOffset.UtcNow), gapBefore: 0, ct));
        // It does not move the task off working — started confirms the harness is up.
        await using (var db = pg.NewContext())
            Assert.Equal(TaskState.Working, await new TaskStore(db, TimeProvider.System).GetStateAsync(taskId, ct));

        await app.StopAsync(ct);
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddScoped<TaskStore>();
        builder.Services.AddScoped<TeamBudgetService>(); // §9.9: the store commits dispatch budget through it
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>(); // §8.4: WorkerTools.open_preview
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(DocketAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DocketAuthenticationHandler>(
                DocketAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>();

        builder.Services.AddSingleton<RunnerConnectionRegistry>();

        builder.Services.AddDocketForwarding(); // §8.3: WorkerTools needs the forward orchestrator
        builder.Services.AddSingleton<RunnerEventSink>();
        builder.Services.AddSingleton(new TaskEventListener(pg.ConnectionString));
        builder.Services.AddSingleton<DispatchService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DispatchService>());

        var app = builder.Build();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcp().RequireAuthorization();
        app.MapRunnerEndpoint();
        return app;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }
}
