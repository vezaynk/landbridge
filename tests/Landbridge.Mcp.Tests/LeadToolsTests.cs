using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Mcp.Auth;
using Landbridge.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;

namespace Landbridge.Mcp.Tests;

/// <summary>
/// LeadTools driven directly against a Postgres-backed <see cref="SessionStore"/>
/// with a lead principal on HttpContext.User — the precise-assertion layer for
/// the §7 human-confirmation gate, the §10 no-prose read, and the §4 eviction
/// reason. The over-the-wire path is covered by
/// <see cref="LeadWorkerEndToEndTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LeadToolsTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!pg.Available) return;
        await pg.ResetAsync();
        Factory = await LeadFactory.SeedAsync(pg, Team, _clock);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private readonly FakeTimeProvider _clock = new();
    private Principal.Lead Factory = null!;
    private string Tid => LeadFactory.Id(Team);

    private static IHttpContextAccessor AccessorFor(Principal principal) =>
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = LandbridgeClaims.ToClaimsPrincipal(principal) } };

    private LeadTools LeadFor(Principal principal, RunnerConnectionRegistry? registry = null) =>
        RelayGrantTestKit.LeadToolsFor(
            pg.NewContext(), _clock, registry ?? new RunnerConnectionRegistry(_clock), AccessorFor(principal));

    private static readonly string M1 = Guid.NewGuid().ToString();

    private static MachineSnapshot Machine() =>
        new(M1, Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });


    [SkippableFact]
    public async Task Create_task_via_the_tool_persists_a_submitted_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);

        var idText = await tools.CreateSession("build the thing", "default", Tid, CancellationToken.None);

        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Slug == idText);
        var id = row.Id;
        Assert.Equal(SessionState.Submitted, row.State);
        Assert.Equal(Team.Value, row.TeamId);
        Assert.Equal("build the thing", row.Description);
    }

    [SkippableFact]
    public async Task Create_team_mints_an_id_this_factory_owns_and_a_sibling_does_not()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);
        var minted = await tools.CreateTeam(CancellationToken.None);
        Assert.True(HaikuSlug.IsWellFormed(minted));

        var idText = await tools.CreateSession("on the new team", "default", minted, CancellationToken.None);
        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Slug == idText);
        var teamRow = await v.LeadTeams.AsNoTracking().SingleAsync(t => t.Slug == minted);
        Assert.Equal(teamRow.TeamId, row.TeamId);

        var otherTeam = TeamId.New();
        var other = await LeadFactory.SeedAsync(pg, otherTeam, _clock);
        var ex = await Assert.ThrowsAsync<McpException>(
            () => LeadFor(other).CreateSession("nope", "default", minted, CancellationToken.None));
        Assert.Contains("does not own that team", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Create_task_rejects_an_empty_description()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);

        // The description is the worker's instructions; the tool refuses an empty
        // one before the command ever reaches the store.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateSession("   ", "default", Tid, CancellationToken.None));
        Assert.Contains("description", ex.Message);
    }

    [SkippableFact]
    public async Task Create_task_rejects_an_empty_profile()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateSession("build the thing", "   ", Tid, CancellationToken.None));
        Assert.Contains("profile", ex.Message);
    }

    [SkippableFact]
    public async Task Stop_session_completes_and_records_lead_provenance()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedTaskWithReport();
        var tools = LeadFor(Factory);

        var ok = await tools.StopSession(sessionId.ToString(), Tid, CancellationToken.None);
        Assert.Contains("Completed", ok);

        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value);
        Assert.Equal(SessionState.Completed, row.State);
        Assert.Equal(VerdictProvenance.LeadSession, row.CompletionProvenance);
    }

    [SkippableFact]
    public async Task Get_team_state_returns_counts_and_states_scoped_to_the_lead_team()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);
        await tools.CreateSession("first", "default", Tid, CancellationToken.None);
        await tools.CreateSession("second", "default", Tid, CancellationToken.None);

        var view = await tools.GetTeamState(Tid, CancellationToken.None);

        Assert.True(HaikuSlug.IsWellFormed(view.TeamId));
        Assert.Equal(2, view.TotalSessions);
        Assert.Equal(2, view.CountsByState[SessionState.Submitted]);
        Assert.All(view.Sessions, t => Assert.StartsWith($"team-{Team}/session-", t.Namespace));
    }

    [SkippableFact]
    public async Task Get_lead_inbox_lists_outstanding_items_and_can_filter_a_session()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);
        var id = await SeedBlockedOnInputTask();

        var inbox = await tools.GetLeadInbox(Tid, ct: CancellationToken.None);
        var item = Assert.Single(inbox.Items);
        Assert.True(HaikuSlug.IsWellFormed(item.SessionId));
        Assert.Equal(LeadInboxKind.Question, item.Kind);

        Assert.Empty((await tools.GetLeadInbox(Tid, Guid.NewGuid().ToString(), CancellationToken.None)).Items);
        Assert.Single((await tools.GetLeadInbox(Tid, id.Value.ToString(), CancellationToken.None)).Items);
    }

    [SkippableFact]
    public async Task Watch_lead_inbox_returns_when_a_question_lands()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var fanout = new SessionEventFanout(pg.ConnectionString);
        await fanout.StartAsync(cts.Token);
        await fanout.WhenListening.WaitAsync(cts.Token);

        var tools = RelayGrantTestKit.LeadToolsFor(
            pg.NewContext(), _clock, new RunnerConnectionRegistry(_clock),
            AccessorFor(Factory), fanout);

        var pending = tools.WatchLeadInbox(Tid, ct: cts.Token);
        await Task.Delay(50, cts.Token);
        var id = await SeedBlockedOnInputTask();

        var inbox = await pending;
        Assert.True(HaikuSlug.IsWellFormed(Assert.Single(inbox.Items).SessionId));

        await fanout.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Stop_session_via_the_tool_hides_the_row()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);
        var idText = await tools.CreateSession("build the thing", "default", Tid, CancellationToken.None);

        var msg = await tools.StopSession(idText, Tid, CancellationToken.None);

        Assert.Contains("Completed", msg);
    }

    [SkippableFact]
    public async Task Send_input_request_unhides_a_stopped_session_that_had_run()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedTaskWithReport();
        var tools = LeadFor(Factory);
        await tools.StopSession(sessionId.ToString(), Tid, CancellationToken.None);

        var ok = await tools.SendInputRequest(sessionId.ToString(), Tid, "more of this", CancellationToken.None);
        Assert.DoesNotContain("Completed", ok, StringComparison.Ordinal);

        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value);
        Assert.False(row.Hidden);
        Assert.Equal(Occupancy.Running, row.OccupancyDesired);
        Assert.Equal(PendingSpawn.Load, row.PendingSpawn);
        Assert.Equal("more of this", row.InputAnswer);
    }

    [SkippableFact]
    public async Task Send_input_request_on_a_stopped_never_dispatched_session_is_a_cold_start()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);
        var idText = await tools.CreateSession("build the thing", "default", Tid, CancellationToken.None);
        await tools.StopSession(idText, Tid, CancellationToken.None);

        await tools.SendInputRequest(idText, Tid, "try it", CancellationToken.None);

        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Slug == idText);
        Assert.False(row.Hidden);
        Assert.Equal(PendingSpawn.New, row.PendingSpawn);
    }

    [SkippableFact]
    public async Task Send_input_request_refuses_a_session_waiting_on_a_question()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedBlockedOnInputTask();
        var tools = LeadFor(Factory);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.SendInputRequest(sessionId.ToString(), Tid, "use staging", CancellationToken.None));
        Assert.Contains("send_input_response", ex.Message);
    }

    [SkippableFact]
    public async Task Send_input_response_refuses_a_session_that_is_not_waiting()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var tools = LeadFor(Factory);
        var idText = await tools.CreateSession("build the thing", "default", Tid, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.SendInputResponse(idText, Tid, "keep going", CancellationToken.None));
        Assert.Contains("send_input_request", ex.Message);
    }

    [SkippableFact]
    public async Task Send_input_request_follows_up_a_live_worker_that_is_not_waiting()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = new SessionStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(Team), Team, "build the thing", "default"));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);

        var sent = new List<RunnerCommand>();
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register(M1, new HashSet<string> { "default" }, (cmd, _) =>
        {
            sent.Add(cmd);
            return Task.CompletedTask;
        });
        registry.TrackDispatch(M1, created.Session.Id);

        var tools = LeadFor(Factory, registry);

        var msg = await tools.SendInputRequest(
            created.Session.Id.ToString(), Tid, "keep going on the tests", CancellationToken.None);
        Assert.Contains("Working", msg);
        Assert.IsType<PromptCommand>(Assert.Single(sent));
    }

    [SkippableFact]
    public async Task Answer_input_request_requeues_for_a_cold_start_when_the_dispatched_machine_is_gone()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedBlockedOnInputTask();
        // An empty registry: the dispatched machine's connection is gone, so there
        // is no held-lease machine (§10). The worker process is gone the moment the
        // task blocked (§11), so the answer cannot resume in place regardless — it
        // requeues the task (→ submitted) and redispatch cold-starts it elsewhere
        // from the workspace (§6, §11), rather than refusing.
        var tools = LeadFor(Factory);

        var msg = await tools.SendInputResponse(sessionId.ToString(), Tid, "use the staging DB", CancellationToken.None);
        Assert.Contains("Working", msg);

        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value);
        Assert.Equal(SessionState.Working, row.State);
        Assert.Null(row.ParkMachine); // no machine to prefer; cold start
        // §11: the cold-start path is exactly where the answer matters most — the
        // transcript that held the question is on a machine that is gone, so the row
        // is the only thing carrying it into the next attempt.
        Assert.Equal("use the staging DB", row.InputAnswer);
        Assert.Equal(SeededQuestion, row.InputQuestion);
    }

    [SkippableFact]
    public async Task Answer_input_request_requeues_a_blocked_task_for_redispatch_with_resume()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedBlockedOnInputTask();
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register(M1, new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.TrackDispatch(M1, sessionId);
        registry.MarkProcessGone(sessionId);
        var tools = LeadFor(Factory, registry);

        // M1 still holds the lease, so the park record prefers it — but the process
        // is gone, so the answer redispatches (→ submitted) rather than prompting
        // a dead session. Resume goes back through dispatch (§11).
        var msg = await tools.SendInputResponse(sessionId.ToString(), Tid, "staging-pg, not docker", CancellationToken.None);
        Assert.Contains("Working", msg);

        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value);
        Assert.Equal(SessionState.Working, row.State);
        Assert.Equal(M1, row.ParkMachine); // held-lease machine preferred (§11)

        Assert.Equal("staging-pg, not docker", row.InputAnswer);
    }

    [SkippableFact]
    public async Task Answer_input_request_continues_a_live_session_and_sends_prompt()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedBlockedOnInputTask();
        var sent = new List<RunnerCommand>();
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register(M1, new HashSet<string> { "default" }, (cmd, _) =>
        {
            sent.Add(cmd);
            return Task.CompletedTask;
        });
        registry.TrackDispatch(M1, sessionId);

        var tools = LeadFor(Factory, registry);

        var msg = await tools.SendInputResponse(sessionId.ToString(), Tid, "use staging-pg", CancellationToken.None);
        Assert.Contains("Working", msg);

        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value);
        Assert.Equal(SessionState.Working, row.State);
        Assert.NotNull(row.CurrentInstanceId);
        Assert.Equal("use staging-pg", row.InputAnswer);
        var prompt = Assert.IsType<PromptCommand>(Assert.Single(sent));
        Assert.Equal(sessionId, prompt.Session);
    }

    [SkippableFact]
    public async Task Answering_a_task_the_sweeper_already_parked_wakes_it_and_it_redispatches()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        // ask: a dispatched worker on m1 requests input → blocked_on_input.
        var sessionId = await SeedBlockedOnInputTask();

        // The registry dispatch set up: enrolled machine live, heartbeating on the fake clock,
        // tracking the task. The sweep is about to park it and untrack the machine.
        var registry = await LiveMachineAsync(sessionId);

        // sweeper parks it once the wait TTL elapses — its own seam, FakeTimeProvider.
        var sweeper = NewSweeper(registry, waitTtl: TimeSpan.FromMinutes(30), machineWindow: TimeSpan.FromHours(2));
        _clock.Advance(TimeSpan.FromMinutes(31));
        await sweeper.SweepAsync(CancellationToken.None);
        await using (var v = pg.NewContext())
            Assert.Equal(SessionState.Parked, (await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value)).State);

        // The Lead answers through the SAME tool, unaware the sweeper got there
        // first — one call, correct outcome either way (§11). It wakes and requeues,
        // and the answer text lands on this branch exactly as on the blocked one:
        // the Lead does not know which branch it took, so neither may lose words.
        var tools = LeadFor(Factory, registry);
        var msg = await tools.SendInputResponse(
            sessionId.ToString(), Tid, "use staging-pg; docker has no seed data", CancellationToken.None);
        Assert.Contains("Working", msg); // load in flight, not resumed in place

        // Redispatch the woken task and confirm a fresh worker instance reads its
        // assignment via the same read get_session delegates to — carrying the answer it
        // was woken for, plus the question that makes the answer legible after a cold
        // start, and the incremented attempt that warns it inherited a workspace.
        await using var db = pg.NewContext();
        var store = new SessionStore(db, _clock);
        var successor = WorkerInstanceId.New();
        var dispatched = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(Machine(), successor));
        Assert.Equal(sessionId, dispatched.Session.Id);

        var assignment = await store.GetAssignmentAsync(new WorkerCaller(Team, sessionId, successor));
        Assert.NotNull(assignment);
        Assert.Equal(2, assignment!.Attempt);
        Assert.Equal(SeededQuestion, assignment.Question);
        Assert.Equal("use staging-pg; docker has no seed data", assignment.Answer);
    }

    [SkippableFact]
    public async Task An_evicted_lead_is_refused_every_tool_with_an_explicit_reason()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var evictedBy = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var tools = LeadFor(new Principal.EvictedLead(Team, evictedBy, at));

        // §4: not a bare authorization error — the reason names who and when.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateSession("build the thing", "default", Tid, CancellationToken.None));
        Assert.Contains("taken over", ex.Message);
        Assert.Contains(evictedBy.ToString("N"), ex.Message);

        // Reads are refused for the same reason.
        await Assert.ThrowsAsync<McpException>(() => tools.GetTeamState(Tid, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_non_lead_principal_cannot_reach_lead_tools()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var worker = new Principal.Worker(new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New()));
        var tools = LeadFor(worker);

        await Assert.ThrowsAsync<McpException>(
            () => tools.CreateSession("build the thing", "default", Tid, CancellationToken.None));
    }

    [SkippableFact]
    public async Task List_profiles_shows_a_lead_the_declared_profiles_the_machines_offering_them_and_liveness()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // §7/§10: the routing read. A Lead about to set create_session(profile:) needs the
        // names that exist, where each can run, and whether it can run there now — exact
        // match means a guessed name is a task nothing ever claims.
        var registry = new RunnerConnectionRegistry(_clock);
        Guid m1, m2;
        await using (var db = pg.NewContext())
        {
            m1 = await TestMachines.ConnectAsync(db, _clock, registry, "m1", profiles: ["default", "gpu"]);
            m2 = await TestMachines.ConnectAsync(db, _clock, registry, "m2", profiles: ["default"]);
        }
        var tools = LeadFor(Factory, registry);

        var view = await tools.ListProfiles(CancellationToken.None);

        Assert.Equal(new[] { "default", "gpu" }, view.Profiles.Select(p => p.Profile));
        Assert.Equal(2, view.ConnectedMachines);
        var shared = view.Profiles.Single(p => p.Profile == "default");
        Assert.Equal(new[] { m1.ToString(), m2.ToString() }.OrderBy(s => s),
            shared.Machines.Select(m => m.MachineId).OrderBy(s => s));
        Assert.True(shared.Dispatchable);
        // Liveness per machine, so "this profile is reachable NOW" is answerable rather
        // than inferred from the mere fact that a machine once declared it.
        Assert.All(shared.Machines, m =>
        {
            Assert.True(m.Ready);
            Assert.False(m.UnderBackPressure);
            Assert.NotNull(m.LastHeartbeat);
        });

        // The narrower profile carries only the machine that declares it (§7 exact match).
        Assert.Equal(new[] { m1.ToString() }, view.Profiles.Single(p => p.Profile == "gpu").Machines
            .Select(m => m.MachineId));
    }

    [SkippableFact]
    public async Task List_profiles_refuses_a_worker_and_tells_it_nothing_about_the_fleet()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // Lead-only through the real authority path: the principal is rebuilt from the
        // claims on HttpContext.User, so a worker's credential is refused by the same
        // LeadPrincipal check every other tool here makes — no test-only seam.
        var registry = new RunnerConnectionRegistry(_clock);
        registry.Register("secret-machine", new HashSet<string> { "restricted" }, (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat("secret-machine", Heartbeat("secret-machine", "restricted"));
        var worker = new Principal.Worker(new WorkerCaller(Team, SessionId.New(), WorkerInstanceId.New()));

        var refused = await Assert.ThrowsAsync<McpException>(
            () => LeadFor(worker, registry).ListProfiles(CancellationToken.None));

        // The refusal must not leak the answer it withheld — this read is fleet-wide, so a
        // message naming a machine or a profile would hand a worker exactly the enumeration
        // it was denied.
        Assert.DoesNotContain("secret-machine", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("restricted", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Lead token", refused.Message, StringComparison.Ordinal);

        // An evicted Lead is refused too, with its own §4 reason rather than this one.
        var evicted = new Principal.EvictedLead(Team, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Contains(
            "taken over",
            (await Assert.ThrowsAsync<McpException>(
                () => LeadFor(evicted, registry).ListProfiles(CancellationToken.None))).Message,
            StringComparison.Ordinal);
    }

    /// <summary>The question the seeded worker asks (§11), so the answer round-trip
    /// assertions have both halves of the exchange to check.</summary>
    private const string SeededQuestion = "which database should I target?";

    /// <summary>Drives a task to blocked_on_input via dispatch + a worker's request.</summary>
    private async Task<SessionId> SeedBlockedOnInputTask(
        string? question = SeededQuestion, InputRequestKind kind = InputRequestKind.Question)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(Team), Team, "needs input", "default"));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Session.Id,
            new RequestInput(new WorkerCaller(Team, created.Session.Id, instance), kind, question));
        return created.Session.Id;
    }

    private async Task<RunnerConnectionRegistry> LiveMachineAsync(SessionId task)
    {
        await using var db = pg.NewContext();
        var machine = await TestMachines.EnrollAsync(db, _clock, "box");
        var registry = new RunnerConnectionRegistry(_clock);
        TestMachines.Register(registry, machine);
        await TestMachines.HeartbeatAsync(db, _clock, machine);
        registry.TrackDispatch(machine.ToString(), task);
        return registry;
    }

    private WaitTtlSweeper NewSweeper(
        RunnerConnectionRegistry registry, TimeSpan? waitTtl = null, TimeSpan? machineWindow = null) =>
        new(ScopeFactory(), registry, _clock, NullLogger<WaitTtlSweeper>.Instance,
            waitTtl, machineWindow, sweepInterval: null);

    /// <summary>A scope factory over the same Postgres + fake clock, so the sweeper's
    /// per-pass scoped SessionStore writes to the DB these tools read.</summary>
    private IServiceScopeFactory ScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<LandbridgeDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddLandbridgeStore();
        services.AddScoped<TokenService>();
        services.AddSingleton<TimeProvider>(_clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static MachineHeartbeat Heartbeat(string machineId, params string[] profiles) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningSessions: 0, profiles, DateTimeOffset.UtcNow);

    [SkippableFact]
    public async Task Per_session_inbox_carries_report_body_and_marks_it_read()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        const string report = "ran the suite (green); proposes task Z on profile gpu";
        var sessionId = await SeedReportedTask(Team, report);
        var tools = LeadFor(Factory);

        var teamWide = await tools.GetLeadInbox(Tid, ct: CancellationToken.None);
        var flag = Assert.Single(teamWide.Items);
        Assert.Equal(LeadInboxKind.Report, flag.Kind);
        Assert.Null(flag.Report);
        Assert.Null(flag.ResultReference);

        var delivered = await tools.GetLeadInbox(Tid, sessionId.ToString(), CancellationToken.None);
        var item = Assert.Single(delivered.Items);
        Assert.Equal(LeadInboxKind.Report, item.Kind);
        Assert.Equal("git:ref", item.ResultReference);
        Assert.Equal(report, item.Report);

        Assert.Empty((await tools.GetLeadInbox(Tid, sessionId.ToString(), CancellationToken.None)).Items);
        Assert.Empty((await tools.GetLeadInbox(Tid, ct: CancellationToken.None)).Items);
    }

    [SkippableFact]
    public async Task Per_session_inbox_carries_a_reference_when_the_worker_left_no_prose()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedReportedTask(Team, report: null);
        var tools = LeadFor(Factory);

        var item = Assert.Single((await tools.GetLeadInbox(Tid, sessionId.ToString(), CancellationToken.None)).Items);
        Assert.Equal("git:ref", item.ResultReference);
        Assert.Null(item.Report);
    }

    [SkippableFact]
    public async Task Per_session_inbox_of_another_team_is_empty()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var foreign = await SeedReportedTask(TeamId.New(), "secret");
        var tools = LeadFor(Factory);

        var inbox = await tools.GetLeadInbox(Tid, foreign.ToString(), CancellationToken.None);
        Assert.Empty(inbox.Items);
    }

    [SkippableFact]
    public async Task Per_session_inbox_carries_the_infrastructure_account_on_a_requeued_report()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        const string report = "ran the suite (green) on the third machine";
        var sessionId = await SeedRequeuedTask(
            requeueLimit: 5, requeues: 2, LivenessLossReason.MachineReboot, report);
        var tools = LeadFor(Factory);

        var item = Assert.Single((await tools.GetLeadInbox(Tid, sessionId.ToString(), CancellationToken.None)).Items);
        Assert.Equal(report, item.Report);
        Assert.Equal(2, item.InfrastructureRequeues);
        Assert.Equal(LivenessLossReason.MachineReboot, item.LastRequeueReason);
    }

    [SkippableFact]
    public async Task Per_session_inbox_carries_a_question_body_without_closing_the_wait()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedBlockedOnInputTask();
        var tools = LeadFor(Factory);

        var first = Assert.Single((await tools.GetLeadInbox(Tid, sessionId.ToString(), CancellationToken.None)).Items);
        Assert.Equal(LeadInboxKind.Question, first.Kind);
        Assert.Equal(SeededQuestion, first.Question);
        Assert.Null(first.Answer);

        var second = Assert.Single((await tools.GetLeadInbox(Tid, sessionId.ToString(), CancellationToken.None)).Items);
        Assert.Equal(SeededQuestion, second.Question);

        await tools.SendInputResponse(sessionId.ToString(), Tid, "target staging-pg", CancellationToken.None);
        var after = await tools.GetLeadInbox(Tid, sessionId.ToString(), CancellationToken.None);
        Assert.DoesNotContain(after.Items, i => i.Kind == LeadInboxKind.Question);
    }

    [SkippableFact]
    public async Task Per_session_inbox_of_a_question_in_another_team_is_empty()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var foreign = await SeedBlockedOnInputTaskIn(TeamId.New(), "the other Team's secret question");
        var tools = LeadFor(Factory);
        Assert.Empty((await tools.GetLeadInbox(Tid, foreign.ToString(), CancellationToken.None)).Items);
    }

    [SkippableFact]
    public async Task Get_team_state_carries_the_question_flag_and_kind_but_never_the_question_text()
    {
        // §10's never-prose rule, on the question exactly as on the report: the bulk
        // status read gains triage structure (which task, what kind) and nothing a Lead
        // could read as instructions.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedBlockedOnInputTask();
        var tools = LeadFor(Factory);

        var view = await tools.GetTeamState(Tid, CancellationToken.None);

        var summary = Assert.Single(view.Sessions);
        Assert.True(summary.HasQuestion);
        Assert.Equal(InputRequestKind.Question, summary.InputKind);
        // The prose itself appears nowhere in the serialized view.
        var json = System.Text.Json.JsonSerializer.Serialize(view);
        Assert.DoesNotContain(SeededQuestion, json, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task An_over_cap_answer_is_refused_and_the_task_stays_blocked()
    {
        // §10 cap discipline, the answer half: refusing leaves the task waiting, which
        // is recoverable — an unblocked task whose answer was dropped is not.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var sessionId = await SeedBlockedOnInputTask();
        var tools = LeadFor(Factory);

        var oversized = new string('x', AnswerInput.MaxAnswerBytes + 1);
        var ex = await Assert.ThrowsAsync<McpException>(
            () => tools.SendInputResponse(sessionId.ToString(), Tid, oversized, CancellationToken.None));
        Assert.Contains(nameof(Rule.AnswerWithinSizeCap), ex.Message);
        // The refusal says where the detail belongs, so the Lead's next move is obvious.
        Assert.Contains("reference", ex.Message, StringComparison.Ordinal);

        await using var v = pg.NewContext();
        var row = await v.Sessions.AsNoTracking().SingleAsync(t => t.Id == sessionId.Value);
        Assert.Equal(SessionState.Working, row.State);
        Assert.Null(row.InputAnswer);
    }

    /// <summary>Drives a task in the given Team to blocked_on_input with a question —
    /// for the cross-Team scoping case.</summary>
    private async Task<SessionId> SeedBlockedOnInputTaskIn(TeamId team, string question)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "needs input", "default"));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Session.Id,
            new RequestInput(new WorkerCaller(team, created.Session.Id, instance), InputRequestKind.Question, question));
        return created.Session.Id;
    }

    /// <summary>Drives a task a report with an optional in-band report, in the
    /// given Team (used for both same-Team and cross-Team cases).</summary>
    private async Task<SessionId> SeedReportedTask(TeamId team, string? report)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", "default"));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Session.Id,
            new ReportResult(new WorkerCaller(team, created.Session.Id, instance), "git:ref", report));
        return created.Session.Id;
    }

    /// <summary>A freshly created task, never dispatched and never reported on — so it
    /// carries neither a result reference nor a report.</summary>
    private async Task<SessionId> SeedTask(TeamId team)
    {
        await using var db = pg.NewContext();
        var created = (StoreResult.Applied)await new SessionStore(db, _clock).CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", "default"));
        return created.Session.Id;
    }

    /// <summary>
    /// Drives a task through <paramref name="requeues"/> infrastructure requeues against a
    /// cap of <paramref name="requeueLimit"/> (§9 check 7), each one a dispatch the plane
    /// then declares dead for <paramref name="reason"/>, and optionally lets one more
    /// worker report <paramref name="report"/> afterwards. The caller picks the count/cap
    /// pair it wants — requeues past the cap are unreachable, since the requeue that
    /// reaches it is the one that abandons the task.
    /// </summary>
    private async Task<SessionId> SeedRequeuedTask(
        int requeueLimit, int requeues, LivenessLossReason reason, string? report = null)
    {
        await using var db = pg.NewContext();
        // The cap is stamped onto the row at creation from this store's policy, so only the
        // creating store needs to know it — nothing on the liveness path reads the config.
        var store = new SessionStore(db, _clock, policy: new SessionStorePolicy(requeueLimit));
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(Team), Team, "criteria", "default"));
        var id = created.Session.Id;

        for (var i = 0; i < requeues; i++)
        {
            Assert.IsType<StoreResult.Applied>(
                await store.DispatchNextAsync(Machine(), WorkerInstanceId.New()));
            Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(id, new LivenessLost(reason)));
            // Failed is not claimable. Wake between losses when another attempt is needed.
            if (report is not null || i < requeues - 1)
                Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(id, new WakeParked()));
        }

        if (report is null)
            return id;

        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(Machine(), instance));
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            id, new ReportResult(new WorkerCaller(Team, id, instance), "git:ref", report)));
        return id;
    }

    private async Task<SessionId> SeedTaskWithReport()
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, _clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(Team), Team, "close this", "default"));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Session.Id,
            new ReportResult(new WorkerCaller(Team, created.Session.Id, instance), "git:ref"));
        return created.Session.Id;
    }
}
