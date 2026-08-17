using Docket.ControlPlane.Auth;
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
            new CreateTask(Lead, Team, "pnpm test", mode, profile));
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
    public async Task A_profile_less_task_waits_for_a_machine_that_declares_default()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var id = await CreateSubmitted(db);

        // §15: "absent a request, `default`" — and both halves of check 5 have to say so. The
        // SQL pre-filter used to read a null profile as "runs anywhere", so on a fleet where
        // nothing declares `default` this row was claimed here and then refused by the engine's
        // half, which resolves it to `default`. A bounced claim ends that machine's turn in the
        // pass, so the task was picked, bounced and picked again on every wake — taking one
        // claim per pass with it — instead of waiting for a machine that could run it.
        Assert.IsType<StoreResult.NotFound>(
            await NewStore(db).DispatchNextAsync(Machine("restricted"), WorkerInstanceId.New()));

        await using var verify = pg.NewContext();
        var row = await verify.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal(0, row.Attempt);

        // Claimable the moment a machine that declares `default` asks — the unchanged half.
        Assert.IsType<StoreResult.Applied>(
            await NewStore(db).DispatchNextAsync(Machine("restricted", "default"), WorkerInstanceId.New()));
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
        Assert.Single(await verify.RegisteredServices.AsNoTracking().Where(s => s.TaskId == id.Value).ToListAsync());
        Assert.IsType<StoreResult.Applied>(await NewStore(db).ApplyAsync(id, new VerdictAccept(new LeadClaim(Team))));
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
        // #81: and the read surface actually returns it. Asserting only the raw row is what
        // let this column go write-only — the Lead's per-task fetch is the §7 read.
        Assert.Equal("git:branch/result-42",
            (await NewStore(v).GetTaskReportAsync(Team, id))!.ResultReference);
    }

    [SkippableFact]
    public async Task Report_result_persists_the_in_band_report_and_surfaces_it_to_lead_and_worker()
    {
        // §10: the worker's optional in-band report is captured verbatim on the row
        // next to the reference. get_team_state stays prose-free (a has_report FLAG,
        // never the text); the Lead pulls the text per task via get_task_report; a
        // successor worker sees it on get_task.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        var caller = new WorkerCaller(Team, id, instance);
        await store.DispatchNextAsync(Machine(), instance);

        const string report = "ran pnpm test (green); touched 3 files; proposes task Y on profile gpu";
        Assert.IsType<StoreResult.Applied>(
            await store.ApplyAsync(id, new ReportResult(caller, "git:ref", report)));

        await using var v = pg.NewContext();
        var vstore = NewStore(v);
        // On the row, verbatim.
        Assert.Equal(report, (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).WorkerReport);
        // get_team_state carries only the FLAG, not the prose (§10 stays prose-free).
        var summary = (await vstore.GetTeamStateAsync(Team)).Tasks.Single(t => t.TaskId == id.Value);
        Assert.True(summary.HasReport);
        // The Lead fetches the text deliberately, per task.
        var fetched = await vstore.GetTaskReportAsync(Team, id);
        Assert.Equal(report, fetched!.Report);
        // On get_task (the incumbent/successor worker's read).
        var assignment = await vstore.GetAssignmentAsync(caller);
        Assert.Equal(report, assignment!.Report);
    }

    [SkippableFact]
    public async Task Report_result_without_a_report_leaves_it_null()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);

        await store.ApplyAsync(id, new ReportResult(new WorkerCaller(Team, id, instance), "git:ref"));

        await using var v = pg.NewContext();
        var vstore = NewStore(v);
        Assert.Null((await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).WorkerReport);
        // No report: the flag is false, and the per-task fetch finds the task (it is
        // the Lead's) but returns a null report.
        Assert.False((await vstore.GetTeamStateAsync(Team)).Tasks.Single(t => t.TaskId == id.Value).HasReport);
        var fetched = await vstore.GetTaskReportAsync(Team, id);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.Report);
        // #81, the asymmetry that makes the reference worth surfacing: §6 REQUIRED it for
        // this transition while the report was optional, so on a task like this one it is
        // the only thing the worker said — and the Lead still has to adjudicate.
        Assert.Equal("git:ref", fetched.ResultReference);
    }

    [SkippableFact]
    public async Task Get_task_report_is_team_scoped_and_refuses_a_cross_team_task()
    {
        // §13: the per-task report fetch is scoped to the caller's Team, so a task in
        // another Team returns null — indistinguishable from not-found, leaking nothing.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(id, new ReportResult(new WorkerCaller(Team, id, instance), "git:ref", "secret report"));

        Assert.Null(await store.GetTaskReportAsync(TeamId.New(), id)); // another Team → null
    }

    [SkippableFact]
    public async Task Report_over_the_cap_is_rejected_and_the_task_stays_working()
    {
        // §10: over-cap is refused; the task does not advance to verifying, so the
        // worker can re-report with a summary (detail in the workspace).
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);

        var oversized = new string('x', ReportResult.MaxReportBytes + 1);
        var rejected = Assert.IsType<StoreResult.Rejected>(
            await store.ApplyAsync(id, new ReportResult(new WorkerCaller(Team, id, instance), "git:ref", oversized)));
        Assert.Equal(Rule.ReportWithinSizeCap, rejected.Rule);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Working, row.State);
        Assert.Null(row.WorkerReport);
    }

    [SkippableFact]
    public async Task Request_input_persists_the_question_and_kind_and_surfaces_them_to_lead_and_worker()
    {
        // §10/§11: the worker's ask is captured verbatim on the row beside the kind, so
        // every surface that answers it can show WHAT is being asked. get_team_state
        // stays prose-free (kind + a flag); the Lead pulls the text per task; the
        // worker sees its own question back on get_task.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        var caller = new WorkerCaller(Team, id, instance);
        await store.DispatchNextAsync(Machine(), instance);

        const string question = "migrate the legacy rows or drop them? dropping loses audit history";
        Assert.IsType<StoreResult.Applied>(
            await store.ApplyAsync(id, new RequestInput(caller, InputRequestKind.Question, question)));

        await using var v = pg.NewContext();
        var vstore = NewStore(v);
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(question, row.InputQuestion);
        Assert.Equal(InputRequestKind.Question, row.InputKind);
        Assert.Null(row.InputAnswer); // nobody has answered yet

        // get_team_state: the kind and a flag, never the prose (§10).
        var summary = (await vstore.GetTeamStateAsync(Team)).Tasks.Single(t => t.TaskId == id.Value);
        Assert.True(summary.HasQuestion);
        Assert.Equal(InputRequestKind.Question, summary.InputKind);

        // The Lead's deliberate per-task fetch carries the text.
        var fetched = await vstore.GetTaskQuestionAsync(Team, id);
        Assert.Equal(question, fetched!.Question);
        Assert.Equal(TaskState.Working, fetched.State);
        Assert.Null(fetched.Answer);

        // And the incumbent's own get_task read.
        var assignment = await vstore.GetAssignmentAsync(caller);
        Assert.Equal(question, assignment!.Question);
    }

    [SkippableFact]
    public async Task Answering_persists_the_answer_for_the_redispatched_worker()
    {
        // §11: the answer's whole purpose. The worker that asked is gone, so the answer
        // waits on the row and reaches the SUCCESSOR instance's get_task.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var asker = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), asker);
        await store.ApplyAsync(id, new RequestInput(
            new WorkerCaller(Team, id, asker), InputRequestKind.Question, "which database?"));

        Assert.IsType<StoreResult.Applied>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", answer: "staging-pg"));

        await using var v = pg.NewContext();
        var vstore = NewStore(v);
        Assert.Equal("staging-pg", (await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value)).InputAnswer);

        // Redispatch: the successor instance reads both halves of the exchange.
        var successor = WorkerInstanceId.New();
        Assert.IsType<StoreResult.Applied>(await vstore.DispatchNextAsync(Machine(), successor));
        var assignment = await vstore.GetAssignmentAsync(new WorkerCaller(Team, id, successor));
        Assert.Equal("which database?", assignment!.Question);
        Assert.Equal("staging-pg", assignment.Answer);

        // The predecessor instance is revoked, so its read is refused — the answer is
        // not a way around instance fencing (§5, §9 check 14).
        Assert.Null(await vstore.GetAssignmentAsync(new WorkerCaller(Team, id, asker)));
    }

    [SkippableFact]
    public async Task A_new_question_clears_the_previous_answer()
    {
        // Otherwise a worker that asks a SECOND question resumes seeing that question
        // paired with the answer to the first — the most confusing possible state.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var first = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), first);
        await store.ApplyAsync(id, new RequestInput(
            new WorkerCaller(Team, id, first), InputRequestKind.Question, "which database?"));
        await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", answer: "staging-pg");

        // Redispatch, then ask something else.
        var second = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), second);
        await store.ApplyAsync(id, new RequestInput(
            new WorkerCaller(Team, id, second), InputRequestKind.AuthHelp, "I need a staging-pg credential"));

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal("I need a staging-pg credential", row.InputQuestion);
        Assert.Equal(InputRequestKind.AuthHelp, row.InputKind);
        Assert.Null(row.InputAnswer); // the stale answer is gone
    }

    [SkippableFact]
    public async Task Answering_a_parked_task_persists_the_answer_on_the_wake_branch_too()
    {
        // §11 one-call answer path: a Lead cannot know whether the wait-TTL sweeper got
        // there first, so the WakeParked branch must keep the words too.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(id, new RequestInput(
            new WorkerCaller(Team, id, instance), InputRequestKind.Question, "which database?"));
        // The sweeper parks it first.
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(id,
            new WaitTtlExpired(new ParkRecord("m1"))));

        Assert.IsType<StoreResult.Applied>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", answer: "staging-pg"));

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal("staging-pg", row.InputAnswer);
    }

    [SkippableFact]
    public async Task A_wordless_wake_leaves_a_live_exchange_untouched()
    {
        // An endpoint_wait consumer woken because its service registered answers
        // nothing in words. That wake must not erase the exchange the row already
        // holds — clearing is the asking side's job alone.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(id, new RequestInput(
            new WorkerCaller(Team, id, instance), InputRequestKind.EndpointWait, "waiting on service 'api'"));
        await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", answer: "'api' is up on 5173");

        // Park it again (a Lead stop), then wake with no words.
        var second = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), second);
        await store.ApplyAsync(id, new StopPreserveAndPark(
            Lead, new ParkRecord("m1")));
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(id, new WakeParked()));

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal("waiting on service 'api'", row.InputQuestion);
        Assert.Equal("'api' is up on 5173", row.InputAnswer);
    }

    [SkippableFact]
    public async Task Get_task_question_is_team_scoped_and_refuses_a_cross_team_task()
    {
        // §13: same scoping as GetTaskReportAsync — another Team's task returns null,
        // indistinguishable from not-found.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(id, new RequestInput(
            new WorkerCaller(Team, id, instance), InputRequestKind.Question, "secret question"));

        Assert.Null(await store.GetTaskQuestionAsync(TeamId.New(), id));
        Assert.Null(await store.GetTaskQuestionAsync(Team, new TaskId(Guid.NewGuid())));
    }

    [SkippableFact]
    public async Task A_task_that_never_asked_carries_no_question_anywhere()
    {
        // Back-compat: every column stays null, the flag is false, and the per-task
        // fetch finds the task (it is the Lead's) but reports nothing asked.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);

        await using var v = pg.NewContext();
        var vstore = NewStore(v);
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Null(row.InputQuestion);
        Assert.Null(row.InputAnswer);
        Assert.Null(row.InputKind);

        var summary = (await vstore.GetTeamStateAsync(Team)).Tasks.Single(t => t.TaskId == id.Value);
        Assert.False(summary.HasQuestion);
        Assert.Null(summary.InputKind);

        var fetched = await vstore.GetTaskQuestionAsync(Team, id);
        Assert.NotNull(fetched);
        Assert.Null(fetched!.Question);
        Assert.Null(fetched.Answer);

        Assert.Null((await vstore.GetAssignmentAsync(new WorkerCaller(Team, id, instance)))!.Question);
    }

    [SkippableFact]
    public async Task An_over_cap_question_is_rejected_and_the_task_stays_working()
    {
        // §10 cap at the store boundary: the engine refuses, so nothing is written and
        // the worker is still working and free to ask again, shorter.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);

        var oversized = new string('x', RequestInput.MaxQuestionBytes + 1);
        var rejected = Assert.IsType<StoreResult.Rejected>(await store.ApplyAsync(id,
            new RequestInput(new WorkerCaller(Team, id, instance), InputRequestKind.Question, oversized)));
        Assert.Equal(Rule.QuestionWithinSizeCap, rejected.Rule);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Working, row.State);
        Assert.Null(row.InputQuestion);
        Assert.Null(row.BlockedAt);
    }

    [SkippableFact]
    public async Task An_over_cap_answer_is_rejected_and_the_task_stays_blocked()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        await store.ApplyAsync(id, new RequestInput(
            new WorkerCaller(Team, id, instance), InputRequestKind.Question, "which database?"));

        var oversized = new string('x', AnswerInput.MaxAnswerBytes + 1);
        var rejected = Assert.IsType<StoreResult.Rejected>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", answer: oversized));
        Assert.Equal(Rule.AnswerWithinSizeCap, rejected.Rule);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Working, row.State); // still waiting; re-answerable
        Assert.Null(row.InputAnswer);
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

    /// <summary>
    /// §8.2: a service name is an address, so re-registering one a task already holds
    /// <b>corrects</b> that row rather than adding a second. The write used to be an
    /// unconditional insert, so a service restarted on a new port left two rows for one name
    /// and the resolver picked between them — including picking the dead port, which is
    /// precisely §8.2's "successful dial into the wrong stack".
    /// </summary>
    [SkippableFact]
    public async Task Re_registering_a_name_the_task_already_holds_corrects_its_port()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        var caller = new WorkerCaller(Team, id, instance);
        await store.DispatchNextAsync(Machine(), instance);

        Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(caller, "api", 5001));
        Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(caller, "api", 5002));

        await using var verify = pg.NewContext();
        var svc = await verify.RegisteredServices.AsNoTracking().SingleAsync(s => s.TaskId == id.Value);
        Assert.Equal("api", svc.Name);
        Assert.Equal(5002, svc.Port);
        // A different name on the same task is a different address, and still its own row.
        Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(caller, "metrics", 5003));
        Assert.Equal(2, await verify.RegisteredServices.AsNoTracking().CountAsync(s => s.TaskId == id.Value));
    }

    /// <summary>
    /// The other half: another task in the Team may not take a live name. Everything that
    /// resolves a forward is handed a name and a Team and nothing else, so two holders made
    /// the resolution a raffle — refused rather than silently reassigned, so the second worker
    /// learns to pick another name instead of believing it advertised something.
    /// </summary>
    [SkippableFact]
    public async Task A_second_task_cannot_take_a_service_name_that_is_live_in_the_Team()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);

        var first = await CreateSubmitted(db);
        var firstInstance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), firstInstance);
        Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(
            new WorkerCaller(Team, first, firstInstance), "api", 5001));

        var second = await CreateSubmitted(db);
        var secondInstance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), secondInstance);
        var secondCaller = new WorkerCaller(Team, second, secondInstance);

        var rejected = Assert.IsType<StoreResult.Rejected>(
            await store.RegisterServiceAsync(secondCaller, "api", 5002));
        Assert.Equal(Rule.ServiceNameUniqueInTeam, rejected.Rule);

        await using var verify = pg.NewContext();
        var svc = await verify.RegisteredServices.AsNoTracking().SingleAsync(s => s.Name == "api");
        Assert.Equal(first.Value, svc.TaskId);
        Assert.Equal(5001, svc.Port);

        // The holder finishing frees the name: a report keeps the process (and the
        // lease); accept is what ends the assignment.
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            first, new ReportResult(new WorkerCaller(Team, first, firstInstance), "ref")));
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(first, new VerdictAccept(new LeadClaim(Team))));
        Assert.IsType<StoreResult.Applied>(await store.RegisterServiceAsync(secondCaller, "api", 5002));
        Assert.Equal(second.Value,
            (await verify.RegisteredServices.AsNoTracking().SingleAsync(s => s.Name == "api")).TaskId);
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
        Assert.Equal(TaskState.Failed, row.State);
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
        await store.ApplyAsync(id, new WakeParked("retry"));
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

        var park = new ParkRecord("m1");
        await store.ApplyAsync(id, new WaitTtlExpired(park));

        await using (var v = pg.NewContext())
        {
            var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
            Assert.Equal(TaskState.Parked, row.State);
            Assert.Equal("m1", row.ParkMachine);
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
        var park = new ParkRecord("m1");
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
                await NewStore(db).AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", sessionLive: false));
            Assert.Equal(TaskState.Submitted, applied.Task.State);
        }

        await using (var v = pg.NewContext())
        {
            var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
            Assert.Equal(TaskState.Submitted, row.State);      // requeued, not left working
            Assert.Equal("m1", row.ParkMachine);               // preferred machine (§11)
            Assert.Equal("sess-answer", row.HarnessSessionRef); // the ref redispatch resumes
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
    public async Task Answering_a_live_session_keeps_the_incumbent_and_lands_the_answer()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await SeedBlocked(store);

        var applied = Assert.IsType<StoreResult.Applied>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", answer: "use staging-pg", sessionLive: true));
        Assert.Equal(TaskState.Working, applied.Task.State);
        Assert.NotNull(applied.Task.CurrentInstance);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Working, row.State);
        Assert.NotNull(row.CurrentInstanceId);
        Assert.Equal("use staging-pg", row.InputAnswer);
        Assert.Null(row.ParkMachine);
        Assert.Null(row.BlockedAt);
        Assert.False((await NewStore(v).GetTeamStateAsync(Team)).Tasks.Single(t => t.TaskId == id.Value).HasQuestion);
    }

    [SkippableFact]
    public async Task A_lead_follow_up_on_a_working_session_with_no_question_stays_working()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);

        var applied = Assert.IsType<StoreResult.Applied>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", answer: "keep going on the tests", sessionLive: true));
        Assert.Equal(TaskState.Working, applied.Task.State);
        Assert.Equal(instance, applied.Task.CurrentInstance);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Working, row.State);
        Assert.Equal(instance.Value, row.CurrentInstanceId);
        Assert.Equal("keep going on the tests", row.InputAnswer);
        Assert.Null(row.ParkMachine);
        Assert.Null(row.BlockedAt);
    }

    [SkippableFact]
    public async Task A_lead_reply_to_a_report_on_a_live_session_returns_to_working()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        var instance = WorkerInstanceId.New();
        await store.DispatchNextAsync(Machine(), instance);
        Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(
            id, new ReportResult(new WorkerCaller(Team, id, instance), "git:ref")));

        var applied = Assert.IsType<StoreResult.Applied>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: "m1", answer: "add a test", sessionLive: true));
        Assert.Equal(TaskState.Working, applied.Task.State);
        Assert.Equal(instance, applied.Task.CurrentInstance);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Working, row.State);
        Assert.Equal("add a test", row.InputAnswer);
    }

    [SkippableFact]
    public async Task Waking_a_failed_task_requeues_it_with_the_note()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var store = NewStore(db);
        var id = await CreateSubmitted(db);
        await store.DispatchNextAsync(Machine(), WorkerInstanceId.New());
        Assert.IsType<StoreResult.Applied>(
            await store.ApplyAsync(id, new LivenessLost(LivenessLossReason.ProcessExited)));

        var woken = Assert.IsType<StoreResult.Applied>(
            await store.AnswerOrWakeAsync(Lead, id, leaseMachine: null, answer: "handshake flake — try again"));
        Assert.Equal(TaskState.Submitted, woken.Task.State);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Submitted, row.State);
        Assert.Equal("handshake flake — try again", row.InputAnswer);
        Assert.Equal(LivenessLossReason.ProcessExited, row.LastRequeueReason);
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
        await store.ApplyAsync(id, new WaitTtlExpired(new ParkRecord("m1")));

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
            await store.ApplyAsync(id, new WaitTtlExpired(new ParkRecord("m1"))));
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
            await store.ApplyAsync(id, new WaitTtlExpired(new ParkRecord("m1"))));
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
            new CreateTask(Lead, Team, "needs input", CompletionMode.Lead, null));
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

    [SkippableFact]
    public async Task A_transition_that_loses_the_race_takes_its_effects_down_with_it()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var id = await CreateSubmittedOnNewContext();
        var instance = WorkerInstanceId.New();
        await using (var d = pg.NewContext())
            await new TaskStore(d, clock).DispatchNextAsync(Machine(), instance);

        // The dispatched worker's live credential — the thing the effect at issue destroys,
        // and the reason this matters beyond bookkeeping.
        string workerToken;
        await using (var t = pg.NewContext())
            workerToken = (await new TokenService(t, clock).MintWorkerTokenAsync(Team, id, instance)).Token;

        await using var db = pg.NewContext();
        // This context reads the working row at its current version...
        _ = await db.Tasks.FirstAsync(t => t.Id == id.Value);
        // ...and the runner then stamps the harness session this dispatch started (§11), an
        // ordinary out-of-band write: the row's xmin moves and its state does not. Every
        // part of the task is healthy, which is what makes this the interesting failure —
        // no crash, no outage, just two writers.
        await using (var runner = pg.NewContext())
            await new TaskStore(runner, clock).StampHarnessSessionRefAsync(id, "session-1");

        // Now the liveness scan requeues off its stale read. The engine agrees (the row this
        // context holds still says working), so the transition runs its effects — revoking
        // the instance — and then loses on the concurrency token.
        var result = await new TaskStore(db, clock)
            .ApplyAsync(id, new LivenessLost(LivenessLossReason.LivenessTimeout));
        Assert.IsType<StoreResult.Conflict>(result);

        // Conflict means nothing happened, and that has to include the effects. When the
        // revoke committed on its own — as it did, ExecuteUpdate running the moment
        // ApplyEffects reached it, before any transaction was open — the row stayed working
        // while the worker actually doing that work lost its authorization: every call it
        // made 401d (§9 check 14), so its result never landed and the task sat working until
        // a clock reclaimed it.
        await using var verify = pg.NewContext();
        var row = await verify.Tasks.AsNoTracking().SingleAsync(t => t.Id == id.Value);
        Assert.Equal(TaskState.Working, row.State);
        Assert.Equal(instance.Value, row.CurrentInstanceId);
        Assert.False(await verify.WorkerInstances.AsNoTracking()
            .Where(w => w.Id == instance.Value).Select(w => w.Revoked).SingleAsync());
        Assert.IsType<Principal.Worker>(await new TokenService(verify, clock).ValidateAsync(workerToken));
    }

    private async Task<TaskId> CreateSubmittedOnNewContext()
    {
        await using var db = pg.NewContext();
        return await CreateSubmitted(db);
    }
}
