using System.Collections.Concurrent;
using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The §8.3 <b>human path</b> at the control plane: the lead↔machine binding that
/// tells the plane where a person sits, and a Lead-consumer forward orchestrated
/// onto that machine. Observed at the runner-channel seam like the worker-side
/// forward tests — the two <c>open-forward</c> commands the plane sends are the
/// contract — against the real schema through the ephemeral Postgres fixture (the
/// AddLeadMachineBindings migration).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LeadMachineForwardTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string RelayUrl = "http://127.0.0.1:5100";

    private static MachineSnapshot DispatchTarget() =>
        new("producer-machine", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    /// <summary>An enrolled machine, as <c>/docket-enroll</c> would leave it (§11).</summary>
    private static async Task<Guid> EnrollAsync(DocketDbContext db, TimeProvider clock, string name)
    {
        var tokens = new TokenService(db, clock);
        var enrollment = await tokens.IssueEnrollmentTokenAsync();
        var credentials = await tokens.ExchangeEnrollmentAsync(
            enrollment.Token, new MachineDeclaration(name, "lead's laptop", "macos", "standard"));
        return credentials!.MachineId;
    }

    /// <summary>A working producer task in <paramref name="team"/> with a registered service.</summary>
    private static async Task<TaskId> WorkingServiceAsync(
        DocketDbContext db, TimeProvider clock, TeamId team, string serviceName, int port = 5432)
    {
        var store = new TaskStore(db, clock);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null, true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(DispatchTarget(), instance);
        Assert.IsType<StoreResult.Applied>(
            await store.RegisterServiceAsync(new WorkerCaller(team, created.Task.Id, instance), serviceName, port));
        return created.Task.Id;
    }

    // ── The binding ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Bind_claims_an_enrolled_machine_and_reads_back()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var machine = await EnrollAsync(db, clock, "slavas-laptop");
        var bindings = new LeadMachineBindingService(db, clock);
        var human = Guid.NewGuid();

        var bound = Assert.IsType<LeadMachineBindResult.Bound>(await bindings.BindAsync(human, machine));

        Assert.Equal(machine, bound.Binding.MachineId);
        Assert.Equal("slavas-laptop", bound.Binding.MachineName);
        Assert.Equal(clock.GetUtcNow(), bound.Binding.BoundAt);

        var read = await bindings.GetAsync(human);
        Assert.Equal(machine, read!.MachineId);
        Assert.Equal("slavas-laptop", read.MachineName);
    }

    [SkippableFact]
    public async Task A_human_with_no_binding_reads_back_nothing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var bindings = new LeadMachineBindingService(db, new FakeTimeProvider());

        Assert.Null(await bindings.GetAsync(Guid.NewGuid()));
    }

    [SkippableFact]
    public async Task Bind_refuses_a_machine_that_was_never_enrolled()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var bindings = new LeadMachineBindingService(db, new FakeTimeProvider());

        var refused = Assert.IsType<LeadMachineBindResult.Refused>(
            await bindings.BindAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Contains("no enrolled machine", refused.Reason);
        Assert.Contains("/docket-enroll", refused.Reason);
    }

    [SkippableFact]
    public async Task Bind_refuses_a_revoked_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var machine = await EnrollAsync(db, clock, "untrusted-box");
        // §13: un-trusting a machine takes it out of the fleet, binding included.
        await new TokenService(db, clock).RevokeMachineAsync(machine);

        var refused = Assert.IsType<LeadMachineBindResult.Refused>(
            await new LeadMachineBindingService(db, clock).BindAsync(Guid.NewGuid(), machine));

        Assert.Contains("no enrolled machine", refused.Reason);
    }

    [SkippableFact]
    public async Task Bind_refuses_a_second_machine_for_the_same_human()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var laptop = await EnrollAsync(db, clock, "laptop");
        var desktop = await EnrollAsync(db, clock, "desktop");
        var bindings = new LeadMachineBindingService(db, clock);
        var human = Guid.NewGuid();

        Assert.IsType<LeadMachineBindResult.Bound>(await bindings.BindAsync(human, laptop));

        // Moving desks is explicit: unbind, then bind (1:1 per human).
        var refused = Assert.IsType<LeadMachineBindResult.Refused>(await bindings.BindAsync(human, desktop));
        Assert.Contains("already bound to machine laptop", refused.Reason);
        Assert.Contains("unbind_machine", refused.Reason);

        var released = await bindings.UnbindAsync(human);
        Assert.Equal(laptop, released!.MachineId);
        var rebound = Assert.IsType<LeadMachineBindResult.Bound>(await bindings.BindAsync(human, desktop));
        Assert.Equal(desktop, rebound.Binding.MachineId);
    }

    [SkippableFact]
    public async Task Bind_refuses_a_machine_another_human_already_claims()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var machine = await EnrollAsync(db, clock, "shared-box");
        var bindings = new LeadMachineBindingService(db, clock);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        Assert.IsType<LeadMachineBindResult.Bound>(await bindings.BindAsync(alice, machine));

        var refused = Assert.IsType<LeadMachineBindResult.Refused>(await bindings.BindAsync(bob, machine));
        Assert.Contains("already bound by human", refused.Reason);
        Assert.Contains(alice.ToString("D"), refused.Reason);

        // Alice releasing frees the slot — the unique index filters on revoked.
        await bindings.UnbindAsync(alice);
        Assert.IsType<LeadMachineBindResult.Bound>(await bindings.BindAsync(bob, machine));
    }

    [SkippableFact]
    public async Task Unbind_with_nothing_bound_is_not_an_error()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var bindings = new LeadMachineBindingService(db, new FakeTimeProvider());

        Assert.Null(await bindings.UnbindAsync(Guid.NewGuid()));
    }

    [SkippableFact]
    public async Task Unbind_leaves_the_history_and_the_machine_bindable_again()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var machine = await EnrollAsync(db, clock, "laptop");
        var bindings = new LeadMachineBindingService(db, clock);
        var human = Guid.NewGuid();

        await bindings.BindAsync(human, machine);
        clock.Advance(TimeSpan.FromMinutes(5));
        await bindings.UnbindAsync(human);

        Assert.Null(await bindings.GetAsync(human));
        await using var verify = pg.NewContext();
        var row = await verify.LeadMachineBindings.AsNoTracking().SingleAsync(b => b.HumanId == human);
        Assert.True(row.Revoked);
        Assert.Equal(clock.GetUtcNow(), row.RevokedAt);
    }

    [SkippableFact]
    public async Task The_machine_group_view_shows_who_bound_each_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var mine = await EnrollAsync(db, clock, "laptop");
        var unbound = await EnrollAsync(db, clock, "build-server");
        var human = Guid.NewGuid();
        await new LeadMachineBindingService(db, clock).BindAsync(human, mine);

        // Both machines connected; only one is somebody's own (§12, §8.3).
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register(mine.ToString(), new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.Register(unbound.ToString(), new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);

        var machines = await new DashboardQueries(db, registry).GetMachinesAsync();

        var bound = Assert.Single(machines, m => m.MachineId == mine.ToString());
        Assert.Equal(human, bound.BoundToHuman);
        Assert.Equal(clock.GetUtcNow(), bound.BoundAt);
        var free = Assert.Single(machines, m => m.MachineId == unbound.ToString());
        Assert.Null(free.BoundToHuman);
        Assert.Null(free.BoundAt);
    }

    // ── The Lead-consumer forward ─────────────────────────────────────────────

    [SkippableFact]
    public async Task Lead_forward_binds_the_consumer_end_on_the_bound_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var producerTask = await WorkingServiceAsync(db, clock, team, "db");
        var leadMachine = await EnrollAsync(db, clock, "slavas-laptop");
        var human = Guid.NewGuid();
        Assert.IsType<LeadMachineBindResult.Bound>(
            await new LeadMachineBindingService(db, clock).BindAsync(human, leadMachine));

        // Two live runner connections: the producer's, and the Lead's own machine.
        var registry = new RunnerConnectionRegistry(clock);
        var waiters = new ForwardWaiters();
        var sent = new ConcurrentBag<(string Machine, OpenForwardCommand Command)>();
        const int boundPort = 45999;

        registry.Register(leadMachine.ToString(), new HashSet<string> { "default" }, (command, _) =>
        {
            if (command is OpenForwardCommand c)
            {
                sent.Add((leadMachine.ToString(), c));
                // The consumer docketd binds loopback and reports the port (§8.3).
                waiters.Complete(c.ForwardId, boundPort);
            }
            return Task.CompletedTask;
        });
        registry.Register("producer-machine", new HashSet<string> { "default" }, (command, _) =>
        {
            if (command is OpenForwardCommand c)
                sent.Add(("producer-machine", c));
            return Task.CompletedTask;
        });
        registry.TrackDispatch("producer-machine", producerTask);

        var grants = new RelayGrantService(db, clock);
        var issued = Assert.IsType<RelayGrantResult.Issued>(await grants.IssueForLeadAsync(team, "db"));
        var orchestrator = new ForwardOrchestrator(
            registry, waiters, NullLogger<ForwardOrchestrator>.Instance);

        var established = Assert.IsType<ForwardEstablishResult.Established>(
            await orchestrator.EstablishForLeadAsync(leadMachine.ToString(), issued, "db", RelayUrl));

        Assert.Equal(boundPort, established.Port);

        // The consumer end went to the BOUND machine with a bind instruction; the
        // producer end to the service owner's machine with its dial target. Same
        // grant, same forward id, one role each (§8.3).
        var consumer = Assert.Single(sent, s => s.Command.Role == RelayTunnel.ConsumerRole);
        Assert.Equal(leadMachine.ToString(), consumer.Machine);
        Assert.Equal(0, consumer.Command.Port);
        Assert.Equal(issued.Grant, consumer.Command.Grant);
        Assert.Equal(RelayUrl, consumer.Command.RelayUrl);
        // No task of its own: the consumer command carries the producer's id, for
        // forward-opened/forward-closed correlation only.
        Assert.Equal(producerTask, consumer.Command.Task);

        var producer = Assert.Single(sent, s => s.Command.Role == RelayTunnel.ProducerRole);
        Assert.Equal("producer-machine", producer.Machine);
        Assert.Equal(producerTask, producer.Command.Task);
        Assert.Equal(5432, producer.Command.Port);
        Assert.Equal(issued.Grant, producer.Command.Grant);
        Assert.Equal(consumer.Command.ForwardId, producer.Command.ForwardId);

        // The grant is minted against the producer with no consumer task bound —
        // the consumer is a person's docketd, not a worker instance (§8.3).
        var row = await db.RelayGrants.AsNoTracking().SingleAsync(g => g.ForwardId == issued.ForwardId);
        Assert.Equal(producerTask.Value, row.ProducerTaskId);
        Assert.Null(row.ConsumerTaskId);
        Assert.Null(row.ConsumerInstanceId);
        Assert.Equal(team.Value, row.TeamId);
        // Single-use per role, exactly as the worker path.
        Assert.True(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Consumer));
        Assert.True(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Producer));
        Assert.False(await grants.ValidateAsync(issued.Grant, issued.ForwardId, RelayGrantRole.Consumer));
    }

    [SkippableFact]
    public async Task Lead_forward_fails_when_the_bound_machine_is_not_connected()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var producerTask = await WorkingServiceAsync(db, clock, team, "db");
        var leadMachine = await EnrollAsync(db, clock, "shut-laptop");

        // Producer is live; the Lead's machine is enrolled and bound but has no
        // runner connection (docketd down, laptop closed).
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register("producer-machine", new HashSet<string> { "default" }, (_, _) => Task.CompletedTask);
        registry.TrackDispatch("producer-machine", producerTask);

        var issued = (RelayGrantResult.Issued)await new RelayGrantService(db, clock).IssueForLeadAsync(team, "db");
        var orchestrator = new ForwardOrchestrator(
            registry, new ForwardWaiters(), NullLogger<ForwardOrchestrator>.Instance);

        var failed = Assert.IsType<ForwardEstablishResult.Failed>(
            await orchestrator.EstablishForLeadAsync(leadMachine.ToString(), issued, "db", RelayUrl));

        Assert.Contains("bound machine", failed.Reason);
        Assert.Contains("not connected", failed.Reason);
    }

    [SkippableFact]
    public async Task Lead_forward_to_another_teams_service_reads_as_not_registered()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        await WorkingServiceAsync(db, clock, TeamId.New(), "secret-db");

        // Check 11 is Team-scoped for a Lead exactly as for a worker: another Team's
        // service is indistinguishable from one that does not exist (§8.2).
        var refused = Assert.IsType<RelayGrantResult.Refused>(
            await new RelayGrantService(db, clock).IssueForLeadAsync(TeamId.New(), "secret-db"));

        Assert.Equal(Rule.ForwardsRequireRegistration, refused.Rule);
        Assert.Contains("no service 'secret-db'", refused.Reason);
    }

    [SkippableFact]
    public async Task Lead_forward_is_refused_once_the_owning_task_leaves_working()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var producerTask = await WorkingServiceAsync(db, clock, team, "db");
        var grants = new RelayGrantService(db, clock);

        // A live grant while the owner works…
        Assert.IsType<RelayGrantResult.Issued>(await grants.IssueForLeadAsync(team, "db"));

        // …and nothing once it leaves working: ClearServicesAndForwards took the
        // registration and the live grants with it (§6, §9 check 11).
        await new TaskStore(db, clock).ApplyAsync(
            producerTask, new LivenessLost(LivenessLossReason.LivenessTimeout));

        var refused = Assert.IsType<RelayGrantResult.Refused>(await grants.IssueForLeadAsync(team, "db"));
        Assert.Equal(Rule.ForwardsRequireRegistration, refused.Rule);
        Assert.Contains("no service 'db'", refused.Reason);
    }
}
