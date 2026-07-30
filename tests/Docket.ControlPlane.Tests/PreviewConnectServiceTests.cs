using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The control-plane half of one HTTP-preview browser connection (spec §8.4):
/// resolve the label, enforce auth-policy + check 11, mint a fresh consumer grant
/// + forward id, and relay the producer its dial target. Exercises the real store,
/// grant service, token service, and forward orchestrator against Postgres — every
/// piece except the relay/producer sockets, which L3 (Docket.Mcp.Tests) covers.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PreviewConnectServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string RelayUrl = "http://127.0.0.1:5100";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Public_preview_connects_and_arms_the_producer()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (producerTask, port) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, sent) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30));

        var est = Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, operatorSession: null, RelayUrl));

        // The producer was armed with THIS connection's fresh grant + forward id,
        // its dial target, and the producer role (§8.4).
        Assert.Equal(RelayUrl, est.RelayUrl);
        Assert.False(string.IsNullOrEmpty(est.Grant));
        var cmd = sent.Single();
        Assert.Equal(RelayTunnel.ProducerRole, cmd.Role);
        Assert.Equal(est.Grant, cmd.Grant);
        Assert.Equal(est.ForwardId, cmd.ForwardId);
        Assert.Equal(RelayUrl, cmd.RelayUrl);
        Assert.Equal(port, cmd.Port);
        Assert.Equal(producerTask, cmd.Task);
    }

    [SkippableFact]
    public async Task Each_connection_mints_a_distinct_forward_id()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, sent) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30));

        var a = Assert.IsType<PreviewConnectResult.Established>(await connect.ConnectAsync(mint.Label, null, RelayUrl));
        var b = Assert.IsType<PreviewConnectResult.Established>(await connect.ConnectAsync(mint.Label, null, RelayUrl));

        // N browser connections are N forward ids through the unchanged relay (§8.4).
        Assert.NotEqual(a.ForwardId, b.ForwardId);
        Assert.NotEqual(a.Grant, b.Grant);
        Assert.Equal(2, sent.Count);
    }

    [SkippableFact]
    public async Task Gated_admits_a_human_session_and_a_same_team_lead()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var tokens = new TokenService(db, clock);
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, _) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(30));

        var human = await tokens.IssueHumanSessionAsync();
        Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, human.Token, RelayUrl));

        var lead = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, team);
        Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, lead.Token.Token, RelayUrl));
    }

    [SkippableFact]
    public async Task Gated_refuses_no_session_bad_session_and_a_wrong_team_lead()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var tokens = new TokenService(db, clock);
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, sent) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(30));

        Assert.IsType<PreviewConnectResult.Unauthorized>(await connect.ConnectAsync(mint.Label, null, RelayUrl));
        Assert.IsType<PreviewConnectResult.Unauthorized>(
            await connect.ConnectAsync(mint.Label, "dkt_not_a_real_token", RelayUrl));

        // A Lead scoped to a different Team is not an operator for THIS preview.
        var human = await tokens.IssueHumanSessionAsync();
        var otherLead = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, TeamId.New());
        Assert.IsType<PreviewConnectResult.Unauthorized>(
            await connect.ConnectAsync(mint.Label, otherLead.Token.Token, RelayUrl));

        // No gated refusal ever armed a producer.
        Assert.Empty(sent);
    }

    [SkippableFact]
    public async Task Unknown_and_expired_labels_are_distinct_refusals()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, _) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(5));

        Assert.IsType<PreviewConnectResult.NotFound>(
            await connect.ConnectAsync("deadbeefdeadbeefdeadbeefdeadbeef", null, RelayUrl));

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.IsType<PreviewConnectResult.Expired>(await connect.ConnectAsync(mint.Label, null, RelayUrl));
    }

    [SkippableFact]
    public async Task Unregistered_service_is_unavailable_check_11()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, sent) = BuildConnect(db, clock, producerTask);
        // A mapping whose service was never registered by a working task → check 11.
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "ghost", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30));

        Assert.IsType<PreviewConnectResult.Unavailable>(await connect.ConnectAsync(mint.Label, null, RelayUrl));
        Assert.Empty(sent);
    }

    [SkippableFact]
    public async Task Offline_producer_machine_is_unavailable()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        // Build the orchestrator WITHOUT tracking the producer's machine, so
        // MachineFor returns null — the producer is not connected (§8.4).
        var registry = new RunnerConnectionRegistry(clock);
        var orch = new ForwardOrchestrator(registry, new ForwardWaiters(), NullLogger<ForwardOrchestrator>.Instance);
        var connect = new PreviewConnectService(
            new PreviewMappingService(db, clock), new RelayGrantService(db, clock),
            new TokenService(db, clock), orch, NullLogger<PreviewConnectService>.Instance);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30));

        Assert.IsType<PreviewConnectResult.Unavailable>(await connect.ConnectAsync(mint.Label, null, RelayUrl));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A working producer task in <paramref name="team"/> with <paramref name="name"/> registered.</summary>
    private static async Task<(TaskId Task, int Port)> WorkingServiceAsync(
        DocketDbContext db, TimeProvider clock, TeamId team, string name, int port)
    {
        var store = new TaskStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Automated, null, true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" }), instance);
        await store.RegisterServiceAsync(new WorkerCaller(team, created.Task.Id, instance), name, port);
        return (created.Task.Id, port);
    }

    /// <summary>
    /// A connect service wired to a registry that has the producer's machine live,
    /// plus the list every armed producer command lands in (empty when nothing was armed).
    /// </summary>
    private static (PreviewConnectService Connect, List<OpenForwardCommand> Sent) BuildConnect(
        DocketDbContext db, TimeProvider clock, TaskId producerTask)
    {
        var sent = new List<OpenForwardCommand>();
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("mp", new HashSet<string> { "default" }, (cmd, _) =>
        {
            if (cmd is OpenForwardCommand ofc)
                sent.Add(ofc);
            return Task.CompletedTask;
        });
        registry.TrackDispatch("mp", producerTask);
        var orch = new ForwardOrchestrator(registry, new ForwardWaiters(), NullLogger<ForwardOrchestrator>.Instance);
        var connect = new PreviewConnectService(
            new PreviewMappingService(db, clock), new RelayGrantService(db, clock),
            new TokenService(db, clock), orch, NullLogger<PreviewConnectService>.Instance);
        return (connect, sent);
    }
}
