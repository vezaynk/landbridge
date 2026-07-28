using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

[Collection(PostgresCollection.Name)]
public sealed class TaskStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private static LeadClaim Lead => new(Team);

    private TaskStore NewStore(DocketDbContext db) => new(db, new FakeTimeProvider());

    private async Task<TaskId> CreateSubmitted(DocketDbContext db, string? profile = null, CompletionMode mode = CompletionMode.Automated)
    {
        var result = await NewStore(db).CreateAsync(
            new CreateTask(Lead, Team, "pnpm test", mode, profile, TeamBudgetRemains: true));
        return ((StoreResult.Applied)result).Task.Id;
    }

    private static MachineSnapshot Machine(params string[] profiles) =>
        new("m1", Ready: true, UnderBackPressure: false,
            profiles.Length == 0 ? new HashSet<string> { "default" } : [.. profiles]);

    [SkippableFact]
    public async Task Create_persists_a_submitted_task_with_a_unique_namespace()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        var id = await CreateSubmitted(db);

        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal($"team-{Team}/task-{id}", row.Namespace);
    }

    [SkippableFact]
    public async Task Dispatch_moves_to_working_and_mints_an_instance_row()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();

        var result = await NewStore(db).DispatchNextAsync(Machine(), instance);

        var applied = Assert.IsType<StoreResult.Applied>(result);
        Assert.Equal(id, applied.Task.Id);
        await using var verify = pg.NewContext();
        Assert.Equal(TaskState.Working, (await verify.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).State);
        var inst = await verify.WorkerInstances.AsNoTracking().SingleAsync(w => w.Id == instance.Value);
        Assert.False(inst.Revoked);
    }

    [SkippableFact]
    public async Task Concurrent_dispatchers_never_claim_the_same_task()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        // One submitted task, ten dispatchers on their own contexts/transactions.
        await using (var seed = pg.NewContext())
            await CreateSubmitted(seed);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var db = pg.NewContext();
            return await NewStore(db).DispatchNextAsync(Machine(), WorkerInstanceId.New());
        }));

        Assert.Equal(1, results.Count(r => r is StoreResult.Applied));
        Assert.Equal(9, results.Count(r => r is StoreResult.NotFound));
    }

    [SkippableFact]
    public async Task Dispatch_respects_profile_match()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await CreateSubmitted(db, profile: "restricted");

        Assert.IsType<StoreResult.NotFound>(
            await NewStore(db).DispatchNextAsync(Machine("default"), WorkerInstanceId.New()));
        Assert.IsType<StoreResult.Applied>(
            await NewStore(db).DispatchNextAsync(Machine("default", "restricted"), WorkerInstanceId.New()));
    }

    [SkippableFact]
    public async Task Report_result_clears_registered_services_transactionally()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await NewStore(db).DispatchNextAsync(Machine(), instance);

        db.RegisteredServices.Add(new RegisteredServiceRow
        {
            TaskId = id.Value, TeamId = Team.Value, Name = "api", Port = 5001, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await NewStore(db).ApplyAsync(id,
            new ReportResult(new WorkerCaller(Team, id, instance), "git:ref-1"));

        Assert.IsType<StoreResult.Applied>(result);
        await using var verify = pg.NewContext();
        Assert.Equal(TaskState.Verifying, (await verify.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).State);
        Assert.Empty(await verify.RegisteredServices.AsNoTracking().Where(s => s.TaskId == id.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task Incumbent_worker_registers_a_service_only_while_working()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        var caller = new WorkerCaller(Team, id, instance);

        // Submitted → not working yet: refused.
        Assert.IsType<StoreResult.Rejected>(await store.RegisterServiceAsync(caller, "api", 5001));

        await store.DispatchNextAsync(Machine(), instance);
        Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(caller, "api", 5001));

        // A non-incumbent worker cannot register on this task.
        var zombie = new WorkerCaller(Team, id, WorkerInstanceId.New());
        var rejected = Assert.IsType<StoreResult.Rejected>(await store.RegisterServiceAsync(zombie, "api", 5002));
        Assert.Equal(Rule.IncumbentInstanceOnly, rejected.Rule);

        await using var verify = pg.NewContext();
        var svc = await verify.RegisteredServices.AsNoTracking().SingleAsync(s => s.TaskId == id.Value);
        Assert.Equal("api", svc.Name);
        Assert.Equal(5001, svc.Port);
    }

    [SkippableFact]
    public async Task Liveness_loss_requeues_and_revokes_the_instance_row()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await NewStore(db).DispatchNextAsync(Machine(), instance);

        await NewStore(db).ApplyAsync(id, new LivenessLost(LivenessLossReason.MachineReboot));

        await using var verify = pg.NewContext();
        var row = await verify.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal(1, row.InfrastructureRequeues);
        Assert.Null(row.CurrentInstanceId);
        Assert.True((await verify.WorkerInstances.AsNoTracking().SingleAsync(w => w.Id == instance.Value)).Revoked);
    }

    [SkippableFact]
    public async Task Zombie_instance_is_fenced_at_the_store_after_requeue_and_redispatch()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);

        var zombie = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), zombie);
        await store.ApplyAsync(id, new LivenessLost(LivenessLossReason.LivenessTimeout));
        var successor = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), successor);

        // The orphaned predecessor's call is refused by the incumbent check.
        var zombieResult = await store.ApplyAsync(id,
            new ReportResult(new WorkerCaller(Team, id, zombie), "stale-ref"));
        var rejected = Assert.IsType<StoreResult.Rejected>(zombieResult);
        Assert.Equal(Rule.IncumbentInstanceOnly, rejected.Rule);

        // The incumbent proceeds.
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(id,
            new ReportResult(new WorkerCaller(Team, id, successor), "real-ref")));
    }

    [SkippableFact]
    public async Task Park_round_trip_persists_and_clears_the_park_record()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(id, new RequestInput(new WorkerCaller(Team, id, instance), InputRequestKind.Question));

        var park = new ParkRecord("m1", "/work/task", "sess-1", Attempt: 1);
        await store.ApplyAsync(id, new WaitTtlExpired(park));

        await using (var v = pg.NewContext())
        {
            var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
            Assert.Equal(TaskState.Parked, row.State);
            Assert.Equal("m1", row.ParkMachine);
            Assert.Equal("/work/task", row.ParkDirectory);
        }

        await store.ApplyAsync(id, new WakeParked());
        await using (var v = pg.NewContext())
        {
            var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
            Assert.Equal(TaskState.Submitted, row.State);
            // Park record survives into submitted for redispatch affinity (§11).
            Assert.Equal("m1", row.ParkMachine);
        }
    }

    [SkippableFact]
    public async Task Rejected_transition_writes_nothing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var id = await CreateSubmitted(db);

        // A worker cannot report on a submitted task.
        var result = await NewStore(db).ApplyAsync(id,
            new ReportResult(new WorkerCaller(Team, id, WorkerInstanceId.New()), "ref"));

        Assert.IsType<StoreResult.Rejected>(result);
        await using var verify = pg.NewContext();
        Assert.Equal(TaskState.Submitted, (await verify.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).State);
        Assert.Empty(await verify.WorkerInstances.AsNoTracking().Where(w => w.TaskId == id.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task Optimistic_concurrency_lets_only_one_of_two_racing_writers_win()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var id = await CreateSubmittedOnNewContext();
        var instance = WorkerInstanceId.New();
        await using (var d = pg.NewContext())
            await NewStore(d).DispatchNextAsync(Machine(), instance);

        // Two contexts load the working row, then both try to move it.
        await using var a = pg.NewContext();
        await using var b = pg.NewContext();
        var worker = new WorkerCaller(Team, id, instance);

        var ra = await NewStore(a).ApplyAsync(id, new ReportResult(worker, "ref-a"));
        var rb = await NewStore(b).ApplyAsync(id, new RequestInput(worker, InputRequestKind.Question));

        // First commit wins; the second sees a stale row. Depending on scheduling
        // the loser is either a concurrency Conflict or a clean Rejected (the row
        // it re-reads is no longer working) — never a second successful mutation.
        Assert.IsType<StoreResult.Applied>(ra);
        Assert.True(rb is StoreResult.Conflict or StoreResult.Rejected, $"unexpected {rb}");
    }

    private async Task<TaskId> CreateSubmittedOnNewContext()
    {
        await using var db = pg.NewContext();
        return await CreateSubmitted(db);
    }
}
