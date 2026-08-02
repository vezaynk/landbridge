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

    private async Task<TaskId> CreateSubmitted(DocketDbContext db, string? profile = null, CompletionMode mode = CompletionMode.Lead)
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
    public async Task Report_result_persists_the_reference_on_the_row()
    {
        // #23, §7: report_result's opaque reference is dropped by CopyFrom; the store
        // must capture it on working → verifying so a later read (the Lead reading the
        // result before adjudicating, §9 check 4) finds it.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);

        var applied = Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(id,
            new ReportResult(new WorkerCaller(Team, id, instance), "git:branch/result-42")));
        Assert.Equal(TaskState.Verifying, applied.Task.State);

        await using var v = pg.NewContext();
        Assert.Equal("git:branch/result-42",
            (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).ResultReference);
    }

    [SkippableFact]
    public async Task Lead_verdict_completes_a_lead_task_and_records_provenance()
    {
        // §9 check 4: in lead mode the Lead session's accept completes the task with
        // no human confirmation, and the completion records lead-session provenance.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db, mode: CompletionMode.Lead);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(id, new ReportResult(new WorkerCaller(Team, id, instance), "ref"));

        var applied = Assert.IsType<StoreResult.Applied>(
            await store.ApplyAsync(id, new VerdictAccept(Lead)));
        Assert.Equal(TaskState.Completed, applied.Task.State);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(VerdictProvenance.LeadSession, row.CompletionProvenance);
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
    public async Task Answer_wakes_a_parked_task_and_leaves_it_ready_for_redispatch()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await SeedBlocked(store);

        // The sweeper's outcome (§11): the task parked with the record the plane held.
        var park = new ParkRecord("m1", "/work/task", "sess-1", Attempt: 1);
        await store.ApplyAsync(id, new WaitTtlExpired(park));

        // The Lead answers — through the routing method — with no knowledge that
        // it parked; the answer landing wakes it (§6, §11). The task is already
        // parked, so the held-lease machine is moot (the wake keeps the park record).
        var woken = Assert.IsType<StoreResult.Applied>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: null));
        Assert.Equal(TaskState.Submitted, woken.Task.State);

        await using (var v = pg.NewContext())
        {
            var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
            Assert.Equal(TaskState.Submitted, row.State);
            // Park record survives into submitted for redispatch affinity (§11).
            Assert.Equal("m1", row.ParkMachine);
            Assert.Equal("/work/task", row.ParkDirectory);
        }

        // Redispatch resumes it; the successor sees the incremented attempt (§11).
        var successor = WorkerInstanceId.New();
        var dispatched = Assert.IsType<StoreResult.Applied>(await store.DispatchNextAsync(Machine(), successor));
        Assert.Equal(id, dispatched.Task.Id);
        Assert.Equal(2, dispatched.Task.Attempt);
    }

    [SkippableFact]
    public async Task Answering_a_blocked_worker_gone_task_parks_with_resume_and_redispatches()
    {
        // The twin-bug fix (#60), end to end through the store: a headless worker
        // blocked and its process exited (§11), then the Lead answers. The answer
        // must NOT sit the task in working with no process — it writes a park record
        // preferring the held-lease machine and the stamped resume ref, requeues
        // (→ submitted), and the very next dispatch pass redispatches it WITH resume.
        Skip.IfNot(pg.Available, pg.SkipReason);
        TaskId id;
        await using (var seed = pg.NewContext())
            id = await SeedBlocked(NewStore(seed));

        // The work session reported its harness session id before blocking; the sink
        // stamped it on the row (§11 resume). Own context, mirroring RunnerEventSink.
        await using (var stamp = pg.NewContext())
            await NewStore(stamp).StampHarnessSessionRefAsync(id, "sess-answer");

        // The Lead answers on a fresh per-request store; m1 still holds the lease.
        await using (var db = pg.NewContext())
        {
            var applied = Assert.IsType<StoreResult.Applied>(
                await NewStore(db).AnswerOrWakeAsync(Lead, id, leaseMachine: "m1"));
            Assert.Equal(TaskState.Submitted, applied.Task.State);
        }

        await using (var v = pg.NewContext())
        {
            var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
            Assert.Equal(TaskState.Submitted, row.State);      // requeued, not left working
            Assert.Equal("m1", row.ParkMachine);               // preferred machine (§11)
            Assert.Equal("sess-answer", row.ParkSessionRef);   // resume ref rides the park
            Assert.Equal(0, row.InfrastructureRequeues);       // a Lead answer is not an infra requeue (§6)
            Assert.Null(row.CurrentInstanceId);
        }

        // No liveness-timeout self-heal needed: the dispatch pass claims it now and
        // surfaces the resume session ref on the dispatch (§11).
        await using (var db = pg.NewContext())
        {
            var dispatched = Assert.IsType<StoreResult.Applied>(
                await NewStore(db).DispatchNextAsync(Machine(), WorkerInstanceId.New()));
            Assert.Equal(id, dispatched.Task.Id);
            Assert.Equal("sess-answer", dispatched.HarnessSessionRef);
            Assert.Equal(2, dispatched.Task.Attempt);
        }
    }

    [SkippableFact]
    public async Task Answering_a_blocked_task_whose_machine_is_gone_requeues_for_a_cold_start()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await SeedBlocked(store);

        // The dispatched machine is gone (no held lease), so there is no park record
        // to write. The answer still requeues the task rather than rejecting: it goes
        // to submitted and redispatch cold-starts it elsewhere from the workspace
        // (§6, §11). No park machine, and the infrastructure counter is untouched.
        var applied = Assert.IsType<StoreResult.Applied>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: null));
        Assert.Equal(TaskState.Submitted, applied.Task.State);
        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Null(row.ParkMachine);
        Assert.Equal(0, row.InfrastructureRequeues);
    }

    [SkippableFact]
    public async Task Waking_a_parked_task_from_a_foreign_leads_claim_is_refused()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await SeedBlocked(store);
        await store.ApplyAsync(id, new WaitTtlExpired(new ParkRecord("m1", null, null, Attempt: 1)));

        // A Lead for another Team cannot wake this Team's parked task (§4, §5) —
        // the same Team scope the AnswerInput engine check enforces on the blocked
        // path, applied here because parked → submitted carries no engine actor check.
        var foreign = new LeadClaim(TeamId.New());
        var rejected = Assert.IsType<StoreResult.Rejected>(
            await store.AnswerOrWakeAsync(foreign, id, leaseMachine: null));
        Assert.Equal(Rule.ActorLacksAuthority, rejected.Rule);
        await using var v = pg.NewContext();
        Assert.Equal(TaskState.Parked, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).State);
    }

    [SkippableFact]
    public async Task Answer_racing_the_sweep_answer_first_the_answer_wins()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await SeedBlocked(store);

        // The Lead answers a beat before the sweep decides to park: the task requeues
        // for redispatch (→ submitted)…
        Assert.IsType<StoreResult.Applied>(await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1"));
        // …and the sweep's park lands on a now-submitted task, which the engine refuses.
        // Exactly one transition, no lost answer, no double move.
        var rejected = Assert.IsType<StoreResult.Rejected>(
            await store.ApplyAsync(id, new WaitTtlExpired(new ParkRecord("m1", null, null, Attempt: 1))));
        Assert.Equal(Rule.InvalidSourceState, rejected.Rule);
        await using var v = pg.NewContext();
        Assert.Equal(TaskState.Submitted, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).State);
    }

    [SkippableFact]
    public async Task Answer_racing_the_sweep_sweep_first_the_wake_wins()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await SeedBlocked(store);

        // The sweep parks a beat before the Lead answers…
        Assert.IsType<StoreResult.Applied>(
            await store.ApplyAsync(id, new WaitTtlExpired(new ParkRecord("m1", null, null, Attempt: 1))));
        // …and the same one answer call now routes to the wake and requeues it.
        // One call, correct outcome either way — exactly one transition, no double move.
        var woken = Assert.IsType<StoreResult.Applied>(await store.AnswerOrWakeAsync(Lead, id, leaseMachine: null));
        Assert.Equal(TaskState.Submitted, woken.Task.State);
        await using var v = pg.NewContext();
        Assert.Equal(TaskState.Submitted, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).State);
    }

    /// <summary>Create → dispatch → block, so the task sits in blocked_on_input.</summary>
    private async Task<TaskId> SeedBlocked(TaskStore store)
    {
        var created = (StoreResult.Applied)await store.CreateAsync(
            new CreateTask(Lead, Team, "needs input", CompletionMode.Lead, null, TeamBudgetRemains: true));
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(created.Task.Id,
            new RequestInput(new WorkerCaller(Team, created.Task.Id, instance), InputRequestKind.Question));
        return created.Task.Id;
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
