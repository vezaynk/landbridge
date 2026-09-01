using System.Net;
using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Landbridge.Runner;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// The whole spine over the wire (spec §10): the real control-plane host — auth
/// scheme, connection registry, dispatch loop, and the <c>/runner</c> WebSocket
/// endpoint — hosted on loopback Kestrel, with the real landbridged
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
        SessionId sessionId;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var enrollment = await tokens.IssueEnrollmentTokenAsync(ct);
            var creds = await tokens.ExchangeEnrollmentAsync(
                enrollment.Token, new MachineDeclaration("box-1", "macos"), ct);
            machineToken = creds!.Access.Token;

            var store = new SessionStore(db, TimeProvider.System);
            var created = (StoreResult.Applied)await store.CreateAsync(
                new CreateSession(new LeadClaim(team), team, "the suite is green", "default"), ct);
            sessionId = created.Session.Id;
        }

        // ── landbridged dials in with the real channel ──────────────────────────
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
                new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], DateTimeOffset.UtcNow), ct));

        // ── A DispatchCommand for the task arrives down the dialed socket ───
        var command = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(sessionId, command.Session);
        Assert.Equal("default", command.Profile);
        Assert.NotEqual("", command.WorkerToken);

        // The control plane committed submitted → working before sending (§10).
        await using (var db = pg.NewContext())
        {
            var state = await new SessionStore(db, TimeProvider.System).GetStateAsync(sessionId, ct);
            Assert.Equal(SessionState.Working, state);
        }

        // The worker token minted for this dispatch validates as this task's worker.
        await using (var db = pg.NewContext())
        {
            var principal = await new TokenService(db, TimeProvider.System).ValidateAsync(command.WorkerToken, ct);
            var worker = Assert.IsType<Principal.Worker>(principal);
            Assert.Equal(sessionId, worker.Caller.Session);
        }

        // ── started flows back up the same socket and is accepted ───────────
        Assert.True(await channel.PublishAsync(new StartedEvent(sessionId, DateTimeOffset.UtcNow), gapBefore: 0, ct));
        // It does not move the task off working — started confirms the harness is up.
        await using (var db = pg.NewContext())
            Assert.Equal(SessionState.Working, await new SessionStore(db, TimeProvider.System).GetStateAsync(sessionId, ct));

        await app.StopAsync(ct);
    }

    /// <summary>
    /// #94 at the endpoint, over real sockets: one machine holding two <c>/runner</c>
    /// connections, and the older one closing afterwards — §17.8's "close a laptop and
    /// reattach", where the plane never noticed the first socket had stopped carrying bytes.
    ///
    /// <para>The endpoint is where this bug lived, and the ControlPlane unit tests cannot reach
    /// it: they drive the registry directly, so they cannot show the endpoint presenting the
    /// right connection at teardown. Here the real <c>MapRunnerEndpoint</c> serves two real
    /// dialed channels for one machine token, and the first one's teardown must leave the
    /// second — which by then holds the machine's tracked work — completely alone. Unregistering
    /// by machine id, as it did, requeued the running task and left the live socket registered
    /// nowhere, so the machine went silently undispatchable.</para>
    ///
    /// <para>Synchronization: nothing observable changes when a superseded teardown does
    /// nothing, so the wait is on the fact that DOES change — a second task reaching the
    /// surviving channel, which needs a full dispatch round trip and therefore cannot outrun
    /// the teardown that the client's close triggers immediately. Under the old behaviour that
    /// dispatch never arrives at all.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_second_connection_for_one_machine_survives_the_first_ones_teardown()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));
        var wsUrl = new Uri(baseUrl.Replace("http://", "ws://") + "/runner");
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();

        var team = TeamId.New();
        string machineToken;
        string machineId;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var enrollment = await tokens.IssueEnrollmentTokenAsync(ct);
            var creds = await tokens.ExchangeEnrollmentAsync(
                enrollment.Token, new MachineDeclaration("box-1", "macos"), ct);
            machineToken = creds!.Access.Token;
            // The registry keys on the AUTHENTICATED identity, never the name a heartbeat
            // reports for itself (§13), so this — not "box-1" — is what it is filed under.
            machineId = creds.MachineId.ToString();
        }
        var held = await SeedSubmittedAsync(team, "the first task", ct);

        // ── The connection the laptop will leave behind, with real work on it ────────
        var stale = new WebSocketControlPlaneChannel(wsUrl, machineToken, TimeProvider.System);
        var staleDispatches = new TaskCompletionSource<DispatchCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        stale.Start((command, _) =>
        {
            if (command is DispatchCommand d)
                staleDispatches.TrySetResult(d);
            return Task.CompletedTask;
        });
        Assert.True(await WaitUntilAsync(() => stale.IsConnected, TimeSpan.FromSeconds(15)),
            "the first connection never dialed in");
        Assert.True(await stale.HeartbeatAsync(Ready("box-1"), ct));
        Assert.Equal(held, (await staleDispatches.Task.WaitAsync(TimeSpan.FromSeconds(30), ct)).Session);

        // ── The reattach: a second connection for the same machine supersedes it ─────
        await using var live = new WebSocketControlPlaneChannel(wsUrl, machineToken, TimeProvider.System);
        var liveDispatches = new TaskCompletionSource<DispatchCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        live.Start((command, _) =>
        {
            if (command is DispatchCommand d)
                liveDispatches.TrySetResult(d);
            return Task.CompletedTask;
        });
        Assert.True(await WaitUntilAsync(() => live.IsConnected, TimeSpan.FromSeconds(15)),
            "the reattaching connection never dialed in");
        Assert.True(await live.HeartbeatAsync(Ready("box-1"), ct));

        // The reattached machine reports the work it is still running, as a real one would —
        // which lands on the tracking the replacing connection re-derived, and holds the
        // aliveness clock off the task so the requeue count asserted below can only have been
        // moved by the teardown under test rather than by this host's default 60s window.
        Assert.True(await live.PublishAsync(new AliveEvent(held, DateTimeOffset.UtcNow), gapBefore: 0, ct));

        // ── The stale socket finally closes, running the teardown that used to take the
        // live connection with it.
        await stale.DisposeAsync();

        // The machine is still there: a task submitted now reaches the surviving channel.
        var next = await SeedSubmittedAsync(team, "the task that proves the machine is still there", ct);
        var arrived = await liveDispatches.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(next, arrived.Session);

        // And the work already in flight never noticed — same attempt, no requeue. It is also
        // still TRACKED, which takes both halves of the fix: the replacing connection re-derived
        // it from committed state, and the superseded teardown then left it alone.
        await using (var db = pg.NewContext())
        {
            var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == held.Value, ct);
            Assert.Equal(SessionState.Working, row.State);
            Assert.Equal(0, row.InfrastructureRequeues);
        }
        Assert.Contains(held, registry.SessionsOn(machineId));
        Assert.NotNull(registry.SnapshotFor(machineId));

        await app.StopAsync(ct);
    }

    /// <summary>
    /// §5/§13 at the endpoint, over real sockets: revoking a machine has to take away the
    /// <em>channel</em>, not just the token rows.
    ///
    /// <para>The runner socket authenticates exactly once, at the upgrade
    /// (<see cref="RunnerEndpoint"/>), and nothing re-checks it afterwards. So a revoke that
    /// only flipped credential rows left the box holding an open, fully-authorized command
    /// channel for as long as it cared to keep it — still receiving dispatches, still
    /// reporting events, its worker tokens still authenticating because a worker credential
    /// carries no machine id for a by-machine sweep to match. "Un-trusting a machine takes
    /// seconds" was true only of the database.</para>
    ///
    /// <para>Only the endpoint can show this: the ControlPlane tests drive the registry
    /// directly, so a recorded close delegate is as far as they reach. Here the socket is
    /// real, and what is asserted is the client observing it go away.</para>
    /// </summary>
    [SkippableFact]
    public async Task Revoking_a_machine_closes_its_dialed_socket_and_401s_its_worker_token()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var app = BuildServer();
        await app.StartAsync(ct);
        var baseUrl = app.Urls.First(u => u.StartsWith("http://"));
        var wsUrl = new Uri(baseUrl.Replace("http://", "ws://") + "/runner");
        var registry = app.Services.GetRequiredService<RunnerConnectionRegistry>();

        var team = TeamId.New();
        string machineToken;
        Guid machineGuid;
        await using (var db = pg.NewContext())
        {
            var tokens = new TokenService(db, TimeProvider.System);
            var creds = await tokens.ExchangeEnrollmentAsync(
                (await tokens.IssueEnrollmentTokenAsync(ct)).Token,
                new MachineDeclaration("box-1", "macos"), ct);
            machineToken = creds!.Access.Token;
            machineGuid = creds.MachineId;
        }
        var machineId = machineGuid.ToString();
        var task = await SeedSubmittedAsync(team, "the work the compromised box is running", ct);

        await using var channel = new WebSocketControlPlaneChannel(wsUrl, machineToken, TimeProvider.System);
        var dispatched = new TaskCompletionSource<DispatchCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Start((command, _) =>
        {
            if (command is DispatchCommand d)
                dispatched.TrySetResult(d);
            return Task.CompletedTask;
        });
        Assert.True(await WaitUntilAsync(() => channel.IsConnected, TimeSpan.FromSeconds(15)),
            "the runner never dialed in");
        Assert.True(await channel.HeartbeatAsync(Ready("box-1"), ct));

        var command = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(task, command.Session);
        // The worker token is live right now — this is what the harness on that box
        // authenticates its MCP calls with, so the assertion after the revoke means something.
        Assert.NotEqual(HttpStatusCode.Unauthorized, await McpProbeAsync(baseUrl, command.WorkerToken, ct));

        // ── The revoke, through the same service the dashboard action calls ──────────
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<MachineRevocationService>()
                .RevokeAsync(machineGuid, ct);

        // The channel is gone at both ends: out of the registry (nothing dispatches or sends
        // here again) and closed on the wire, which the client sees for itself.
        Assert.Null(registry.SnapshotFor(machineId));
        Assert.True(await WaitUntilAsync(() => !channel.IsConnected, TimeSpan.FromSeconds(15)),
            "the revoked machine's socket stayed open");

        // Its worker token now 401s, and its own machine token can no longer buy a new socket
        // — so the reconnect the daemon is already attempting is refused rather than served.
        Assert.Equal(HttpStatusCode.Unauthorized, await McpProbeAsync(baseUrl, command.WorkerToken, ct));
        Assert.Equal(HttpStatusCode.Unauthorized, await McpProbeAsync(baseUrl, machineToken, ct));

        // And the work it held failed rather than being abandoned on a box nobody
        // trusts. Failed is a park the Lead did not ask for; the only machine just left.
        await using (var db = pg.NewContext())
            Assert.Equal(SessionState.Failed,
                (await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == task.Value, ct)).State);

        await app.StopAsync(ct);
    }

    /// <summary>
    /// A well-formed JSON-RPC <c>initialize</c> against the MCP endpoint carrying
    /// <paramref name="token"/> — the cheapest thing that makes the auth handler run and
    /// therefore the honest test of whether a token still authenticates (§5).
    /// </summary>
    private static async Task<HttpStatusCode> McpProbeAsync(string baseUrl, string token, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var res = await http.SendAsync(req, ct);
        return res.StatusCode;
    }

    private static MachineHeartbeat Ready(string machineId) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], DateTimeOffset.UtcNow);

    private async Task<SessionId> SeedSubmittedAsync(TeamId team, string criteria, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var created = (StoreResult.Applied)await new SessionStore(db, TimeProvider.System).CreateAsync(
            new CreateSession(new LeadClaim(team), team, criteria, "default"), ct);
        return created.Session.Id;
    }

    private WebApplication BuildServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        builder.Services.AddLandbridgeStore();
        builder.Services.AddScoped<RelayGrantService>();
        builder.Services.AddScoped<PreviewMappingService>(); // §8.4: WorkerTools.open_preview
        builder.Services.AddScoped<TokenService>();
        builder.Services.AddScoped<MachineRevocationService>(); // §13: un-trust a machine, whole
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddAuthentication(LandbridgeAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, LandbridgeAuthenticationHandler>(
                LandbridgeAuthenticationHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorization();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<WorkerTools>()
            .WithTools<LeadTools>()
            .WithTools<FrictionTools>();

        builder.Services.AddSingleton<RunnerConnectionRegistry>();

        builder.Services.AddLandbridgeForwarding(); // §8.3: WorkerTools needs the forward orchestrator
        builder.Services.AddSingleton<RunnerEventSink>();
        builder.Services.AddSingleton(new SessionEventListener(pg.ConnectionString));
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
