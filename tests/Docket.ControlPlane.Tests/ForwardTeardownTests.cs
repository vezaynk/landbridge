using System.Collections.Concurrent;
using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// §8.3's other bound, at the control plane: "an established splice persists <b>until the
/// owning task leaves <c>working</c></b>". The plane already cleared the task's registered
/// services and revoked its grants at that moment — but a grant gates only the <em>next</em>
/// open, so a splice already running had nothing to end it. These pin the command that does
/// (<c>close-forward</c>, <see cref="ForwardTeardownService"/>), observed at the same
/// runner-channel seam as the two <c>open-forward</c> commands that set the forward up.
///
/// <para>The transitions here are the ones that matter: <c>report_result</c> and
/// <c>cancel_task</c>, where <b>nothing is killed</b>. A liveness loss tree-kills the worker
/// (§10, #84), so the producer's sockets die with it and the splice ended by accident even
/// before this existed — which is exactly why the gap went unnoticed.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ForwardTeardownTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string ProducerMachine = "producer-machine";
    private const string ConsumerMachine = "consumer-machine";
    private const string ServiceName = "db";

    [SkippableFact]
    public async Task Report_result_closes_both_ends_of_the_tasks_live_forward()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var rig = new Rig(clock);

        var producer = await WorkingOnAsync(db, clock, team, ProducerMachine, rig);
        Assert.IsType<StoreResult.Applied>(await NewStore(db, clock, rig).RegisterServiceAsync(
            new WorkerCaller(team, producer.Task, producer.Instance), ServiceName, 5432));
        var consumer = await WorkingOnAsync(db, clock, team, ConsumerMachine, rig);

        // A worker consumer's forward: the grant binds both ends' tasks (§8.3).
        var issued = Assert.IsType<RelayGrantResult.Issued>(
            await new RelayGrantService(db, clock).IssueAsync(
                new WorkerCaller(team, consumer.Task, consumer.Instance), ServiceName));
        Assert.Equal(producer.Task, issued.Producer);

        // The producer reports and leaves working. Nothing is killed here — this is the
        // transition where the splice used to simply carry on.
        Assert.IsType<StoreResult.Applied>(await NewStore(db, clock, rig).ApplyAsync(
            producer.Task, new ReportResult(new WorkerCaller(team, producer.Task, producer.Instance), "ref")));

        var forwardId = issued.ForwardId.ToString();
        var closes = rig.Closes;
        Assert.Equal(2, closes.Count);

        // Producer end: its own machine, its own task id.
        var toProducer = Assert.Single(closes, c => c.Machine == ProducerMachine);
        Assert.Equal(forwardId, toProducer.Command.ForwardId);
        Assert.Equal(producer.Task, toProducer.Command.Task);

        // Consumer end: its own machine, and named by ITS task — the id its open-forward
        // carried, since §10's task field is per-message correlation and the consumer end
        // received one for the consumer's task.
        var toConsumer = Assert.Single(closes, c => c.Machine == ConsumerMachine);
        Assert.Equal(forwardId, toConsumer.Command.ForwardId);
        Assert.Equal(consumer.Task, toConsumer.Command.Task);

        // And the grant is revoked as before: the door is shut as well as the splice ended.
        Assert.True((await db.RelayGrants.AsNoTracking().SingleAsync(g => g.ForwardId == issued.ForwardId)).Revoked);
    }

    [SkippableFact]
    public async Task Cancel_closes_the_forward_of_a_task_that_was_never_killed()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var rig = new Rig(clock);

        var producer = await WorkingOnAsync(db, clock, team, ProducerMachine, rig);
        Assert.IsType<StoreResult.Applied>(await NewStore(db, clock, rig).RegisterServiceAsync(
            new WorkerCaller(team, producer.Task, producer.Instance), ServiceName, 5432));
        var issued = Assert.IsType<RelayGrantResult.Issued>(
            await new RelayGrantService(db, clock).IssueForPreviewAsync(team, producer.Task, ServiceName));

        Assert.IsType<StoreResult.Applied>(await NewStore(db, clock, rig).ApplyAsync(
            producer.Task, new Cancel(new LeadClaim(team), CancelDisposition.Preserve)));

        // An §8.4 preview consumer is a browser, so there is no consumer docketd to command
        // and the grant records no consumer task — the producer end is the whole of what the
        // plane can close, and closing it ends the browser's half through the relay's pairing.
        var close = Assert.Single(rig.Closes);
        Assert.Equal(ProducerMachine, close.Machine);
        Assert.Equal(issued.ForwardId.ToString(), close.Command.ForwardId);
        Assert.Equal(producer.Task, close.Command.Task);
    }

    [SkippableFact]
    public async Task A_permission_request_keeps_its_splice()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var rig = new Rig(clock);

        var producer = await WorkingOnAsync(db, clock, team, ProducerMachine, rig);
        Assert.IsType<StoreResult.Applied>(await NewStore(db, clock, rig).RegisterServiceAsync(
            new WorkerCaller(team, producer.Task, producer.Instance), ServiceName, 5432));
        Assert.IsType<RelayGrantResult.Issued>(
            await new RelayGrantService(db, clock).IssueForPreviewAsync(team, producer.Task, ServiceName));

        // §11's one exception: a worker blocked on a PERMISSION request is parked inside a
        // live tool call and returns as the same incumbent, so it emits no clearing effect
        // and keeps its registrations. Tearing its splice down would break a worker
        // mid-turn for asking a question — the close must follow the effect, not the state.
        Assert.IsType<StoreResult.Applied>(await NewStore(db, clock, rig).ApplyAsync(
            producer.Task,
            new RequestInput(
                new WorkerCaller(team, producer.Task, producer.Instance),
                InputRequestKind.Permission, "may I run this", PermissionTool: "Bash")));

        Assert.Empty(rig.Closes);
        Assert.False((await db.RelayGrants.AsNoTracking().SingleAsync()).Revoked);
    }

    [SkippableFact]
    public async Task A_task_holding_no_forwards_sends_nothing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var clock = new FakeTimeProvider();
        var team = TeamId.New();
        var rig = new Rig(clock);

        var worker = await WorkingOnAsync(db, clock, team, ProducerMachine, rig);

        Assert.IsType<StoreResult.Applied>(await NewStore(db, clock, rig).ApplyAsync(
            worker.Task, new ReportResult(new WorkerCaller(team, worker.Task, worker.Instance), "ref")));

        // The overwhelmingly common transition: no grants, so no reads to make and no
        // machines to tell. The effect still fires (services are cleared) — this pins that
        // the close costs nothing when there is nothing to close.
        Assert.Empty(rig.Closes);
    }

    // ── Rig ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The runner-channel seam two machines' <c>docketd</c> would occupy: every
    /// <c>close-forward</c> the plane writes, with the machine it was written to.
    /// </summary>
    private sealed class Rig
    {
        public Rig(TimeProvider clock)
        {
            Registry = new RunnerConnectionRegistry(clock);
            foreach (var machine in new[] { ProducerMachine, ConsumerMachine })
            {
                var id = machine;
                Registry.Register(id, new HashSet<string> { "default" }, (command, _) =>
                {
                    if (command is CloseForwardCommand close)
                        Closes.Add((id, close));
                    return Task.CompletedTask;
                });
            }
            Teardown = new ForwardTeardownService(Registry, NullLogger<ForwardTeardownService>.Instance);
        }

        public RunnerConnectionRegistry Registry { get; }

        public ForwardTeardownService Teardown { get; }

        public ConcurrentBag<(string Machine, CloseForwardCommand Command)> Closes { get; } = [];
    }

    private static TaskStore NewStore(DocketDbContext db, TimeProvider clock, Rig rig) =>
        new(db, clock, policy: null, forwards: rig.Teardown);

    /// <summary>
    /// A task dispatched to <paramref name="machine"/> and tracked there, so the plane can
    /// resolve its machine the way a live forward's ends are resolved (§8.3).
    /// </summary>
    private static async Task<(TaskId Task, WorkerInstanceId Instance)> WorkingOnAsync(
        DocketDbContext db, TimeProvider clock, TeamId team, string machine, Rig rig)
    {
        var store = NewStore(db, clock, rig);
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(
            new MachineSnapshot(machine, Ready: true, UnderBackPressure: false, new HashSet<string> { "default" }),
            instance));
        rig.Registry.TrackDispatch(machine, created.Task.Id);
        return (created.Task.Id, instance);
    }
}
