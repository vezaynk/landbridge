using Landbridge.Contracts;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The control-plane half of one HTTP-preview browser connection (spec §8.4):
/// resolve the label, enforce auth-policy (operator session or per-label preview
/// session) + check 11, mint a fresh consumer grant + forward id, and relay the
/// producer its dial target. Exercises the real store, grant service, token
/// service, preview-auth store, and forward orchestrator against Postgres — every
/// piece except the relay/producer sockets, which L3 (Landbridge.Mcp.Tests) covers.
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
        var (connect, sent, _) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30));

        var est = Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, operatorSession: null, previewSession: null, RelayUrl));

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
        Assert.Equal(producerTask, cmd.Session);
    }

    [SkippableFact]
    public async Task Each_connection_mints_a_distinct_forward_id()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, sent, _) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30));

        var a = Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, null, null, RelayUrl));
        var b = Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, null, null, RelayUrl));

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
        var (connect, _, _) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(30));

        var human = await tokens.IssueHumanSessionAsync();
        Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, human.Token, null, RelayUrl));

        var lead = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, team);
        Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, lead.Token.Token, null, RelayUrl));
    }

    [SkippableFact]
    public async Task Gated_admits_a_valid_per_label_preview_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, _, previewAuth) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(30));

        // The redirect flow's product: a code minted for this label, redeemed into a
        // per-label preview session, admits the gated connection with no operator token.
        var session = previewAuth.Redeem(previewAuth.MintCode(mint.Label), mint.Label);
        Assert.NotNull(session);
        Assert.IsType<PreviewConnectResult.Established>(
            await connect.ConnectAsync(mint.Label, operatorSession: null, previewSession: session, RelayUrl));

        // A preview session for a DIFFERENT label does not admit this one.
        var otherSession = previewAuth.Redeem(previewAuth.MintCode("someotherlabel"), "someotherlabel");
        Assert.IsType<PreviewConnectResult.Unauthorized>(
            await connect.ConnectAsync(mint.Label, null, otherSession, RelayUrl));
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
        var (connect, sent, _) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Gated, TimeSpan.FromMinutes(30));

        Assert.IsType<PreviewConnectResult.Unauthorized>(
            await connect.ConnectAsync(mint.Label, null, null, RelayUrl));
        Assert.IsType<PreviewConnectResult.Unauthorized>(
            await connect.ConnectAsync(mint.Label, "lbr_not_a_real_token", null, RelayUrl));

        // A Lead scoped to a different Team is not an operator for THIS preview.
        var human = await tokens.IssueHumanSessionAsync();
        var otherLead = (LeadClaimResult.Claimed)await tokens.ClaimLeadAsync(human.Token, TeamId.New());
        Assert.IsType<PreviewConnectResult.Unauthorized>(
            await connect.ConnectAsync(mint.Label, otherLead.Token.Token, null, RelayUrl));

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
        var (connect, _, _) = BuildConnect(db, clock, producerTask);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(5));

        Assert.IsType<PreviewConnectResult.NotFound>(
            await connect.ConnectAsync("deadbeefdeadbeefdeadbeefdeadbeef", null, null, RelayUrl));

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.IsType<PreviewConnectResult.Expired>(
            await connect.ConnectAsync(mint.Label, null, null, RelayUrl));
    }

    [SkippableFact]
    public async Task Unregistered_service_is_unavailable_check_11()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var (producerTask, _) = await WorkingServiceAsync(db, clock, team, "web", 3000);
        var (connect, sent, _) = BuildConnect(db, clock, producerTask);
        // A mapping whose service was never registered by a working task → check 11.
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "ghost", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30));

        Assert.IsType<PreviewConnectResult.Unavailable>(
            await connect.ConnectAsync(mint.Label, null, null, RelayUrl));
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
            new TokenService(db, clock), new PreviewAuthStore(clock), orch,
            NullLogger<PreviewConnectService>.Instance);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, producerTask, "web", PreviewAuthPolicy.Public, TimeSpan.FromMinutes(30));

        Assert.IsType<PreviewConnectResult.Unavailable>(
            await connect.ConnectAsync(mint.Label, null, null, RelayUrl));
    }

    /// <summary>
    /// The §8.4 authority the mapping is <em>for</em>: a label minted against one task's
    /// service resolves to that task's registration and nothing else. The mapping has
    /// recorded its task all along (<c>PreviewMappingRow.SessionId</c>) and connect dropped it,
    /// minting by <c>(Team, name)</c> — so once the label's own task finished and a second
    /// task in the Team registered the same name, an unexpired URL for A's <c>web</c> spliced
    /// a browser into B's <c>web</c>: a preview reaching a service its holder never exposed,
    /// on a URL its holder shared. Fails before the fix, where the connect is
    /// <see cref="PreviewConnectResult.Established"/> against B's port.
    /// </summary>
    [SkippableFact]
    public async Task A_label_minted_for_one_task_never_resolves_another_tasks_service()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();

        var (taskA, instanceA) = await WorkingServiceWithInstanceAsync(db, clock, team, "web", 3000);
        var mint = await new PreviewMappingService(db, clock)
            .CreateAsync(team, taskA, "web", PreviewAuthPolicy.Public, TimeSpan.FromHours(2));

        // A finishes: its registration goes with it (ClearServicesAndForwards), which frees
        // the name — and the label, TTL-bound rather than task-bound, outlives it.
        Assert.IsType<StoreResult.Applied>(await new SessionStore(db, clock).ApplyAsync(
            taskA, new ReportResult(new WorkerCaller(team, taskA, instanceA), "ref")));

        // B, same Team, registers the same name on a different port — legitimate, since the
        // name is free now.
        var (taskB, _) = await WorkingServiceAsync(db, clock, team, "web", 4000);
        var (connect, sent, _) = BuildConnect(db, clock, taskA, taskB);

        var result = await connect.ConnectAsync(mint.Label, null, null, RelayUrl);

        Assert.IsType<PreviewConnectResult.Unavailable>(result);
        Assert.Empty(sent);
        // Nothing was minted either: no grant, so nothing to replay against B later.
        Assert.Empty(db.RelayGrants);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="WorkingServiceAsync"/>, also handing back the worker instance — needed
    /// wherever a test drives the producer task's own transition afterwards.
    /// </summary>
    private static async Task<(SessionId Session, WorkerInstanceId Instance)> WorkingServiceWithInstanceAsync(
        LandbridgeDbContext db, TimeProvider clock, TeamId team, string name, int port)
    {
        var store = new SessionStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" }), instance);
        Assert.IsType<StoreResult.Applied>(
            await store.RegisterServiceAsync(new WorkerCaller(team, created.Session.Id, instance), name, port));
        return (created.Session.Id, instance);
    }

    /// <summary>A working producer task in <paramref name="team"/> with <paramref name="name"/> registered.</summary>
    private static async Task<(SessionId Session, int Port)> WorkingServiceAsync(
        LandbridgeDbContext db, TimeProvider clock, TeamId team, string name, int port)
    {
        var store = new SessionStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(
            new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" }), instance);
        await store.RegisterServiceAsync(new WorkerCaller(team, created.Session.Id, instance), name, port);
        return (created.Session.Id, port);
    }

    /// <summary>
    /// A connect service wired to a registry that has the producer's machine live,
    /// the list every armed producer command lands in, and the shared preview-auth
    /// store (so tests can mint a per-label session).
    /// </summary>
    private static (PreviewConnectService Connect, List<OpenForwardCommand> Sent, PreviewAuthStore PreviewAuth) BuildConnect(
        LandbridgeDbContext db, TimeProvider clock, params SessionId[] trackedTasks)
    {
        var sent = new List<OpenForwardCommand>();
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("mp", new HashSet<string> { "default" }, (cmd, _) =>
        {
            if (cmd is OpenForwardCommand ofc)
                sent.Add(ofc);
            return Task.CompletedTask;
        });
        // Every task named is live on the one machine, so a producer the connect resolves
        // — right or wrong — is always reachable and therefore always visible in `sent`.
        foreach (var task in trackedTasks)
            registry.TrackDispatch("mp", task);
        var orch = new ForwardOrchestrator(registry, new ForwardWaiters(), NullLogger<ForwardOrchestrator>.Instance);
        var previewAuth = new PreviewAuthStore(clock);
        var connect = new PreviewConnectService(
            new PreviewMappingService(db, clock), new RelayGrantService(db, clock),
            new TokenService(db, clock), previewAuth, orch, NullLogger<PreviewConnectService>.Instance);
        return (connect, sent, previewAuth);
    }
}
