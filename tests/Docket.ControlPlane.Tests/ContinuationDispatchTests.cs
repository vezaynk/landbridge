using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// §6/§11 continuation targeting at the dispatch seam (live path, Postgres-backed).
/// A continuation task is seeded with park-record-style affinity — a preferred
/// machine, an inherited harness session ref, and a machine-gone policy — and these
/// tests pin what <see cref="TaskStore.DispatchNextAsync"/> does with it: prefer the
/// machine and carry the session ref (so the runner --resumes the transcript there),
/// DEGRADE to a cold start elsewhere when that machine is gone (logging the lost
/// memory), or PIN and wait for it to return. Forking one task into several
/// continuations is legal and each carries the same inherited ref.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContinuationDispatchTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TeamId Team = TeamId.New();
    private static LeadClaim Lead => new(Team);

    private TaskStore NewStore(DocketDbContext db) => new(db, new FakeTimeProvider());

    private static MachineSnapshot Machine(string id, params string[] profiles) =>
        new(id, Ready: true, UnderBackPressure: false,
            profiles.Length == 0 ? new HashSet<string> { "default" } : [.. profiles]);

    /// <summary>Creates a submitted continuation task seeded exactly as the tool would
    /// (same-Team, resolved preferred machine + inherited session ref + policy).</summary>
    private async Task<(TaskId Task, TaskId Continued)> CreateContinuation(
        DocketDbContext db, string preferredMachine, string? sessionRef, MachineGonePolicy policy,
        string? profile = null)
    {
        var continued = TaskId.New();
        var result = await NewStore(db).CreateAsync(new CreateTask(
            Lead, Team, "criteria", CompletionMode.Lead, profile, TeamBudgetRemains: true,
            Continues: new Continuation(continued, Team, preferredMachine, sessionRef, policy, PreferredMachineProfiles: null)));
        return (((StoreResult.Applied)result).Task.Id, continued);
    }

    [SkippableFact]
    public async Task Creation_seeds_the_continuation_affinity_onto_the_row()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        var (task, continued) = await CreateContinuation(db, "m1", "sess-1", MachineGonePolicy.Degrade);

        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Value);
        Assert.Equal(continued.Value, row.ContinuesTaskId);
        Assert.Equal("m1", row.PreferredMachine);
        Assert.Equal(MachineGonePolicy.Degrade, row.OnMachineGone);
        // The inherited session ref lands on the same column §11 resume reads at dispatch.
        Assert.Equal("sess-1", row.HarnessSessionRef);
    }

    [SkippableFact]
    public async Task Continuation_prefers_its_machine_and_carries_the_inherited_session_ref()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await CreateContinuation(db, "m1", "sess-1", MachineGonePolicy.Degrade);

        // m1 (the preferred machine) is still connected, so another machine asking is
        // not eligible — the task waits for m1 rather than cold-starting on m2.
        Assert.IsType<StoreResult.NotFound>(
            await NewStore(db).DispatchNextAsync(Machine("m2"), WorkerInstanceId.New(), default, ["m1", "m2"]));

        // m1 claims it and the dispatch carries the inherited ref back — the runner
        // --resumes the transcript on the machine that holds it (§11 resume seam).
        var applied = Assert.IsType<StoreResult.Applied>(
            await NewStore(db).DispatchNextAsync(Machine("m1"), WorkerInstanceId.New(), default, ["m1", "m2"]));
        Assert.Equal("sess-1", applied.HarnessSessionRef);
    }

    [SkippableFact]
    public async Task Degrade_cold_starts_elsewhere_when_the_preferred_machine_is_gone()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (task, _) = await CreateContinuation(db, "m1", "sess-1", MachineGonePolicy.Degrade);

        // m1 is gone (absent from the connected set); DEGRADE lets m2 claim it.
        var applied = Assert.IsType<StoreResult.Applied>(
            await NewStore(db).DispatchNextAsync(Machine("m2"), WorkerInstanceId.New(), default, ["m2"]));
        // Cold start: the resume ref is suppressed so the runner does not --resume a
        // session that lives on the gone machine.
        Assert.Null(applied.HarnessSessionRef);

        await using var v = pg.NewContext();
        var row = await v.Tasks.AsNoTracking().SingleAsync(t => t.Id == task.Value);
        Assert.Equal(TaskState.Working, row.State);
        // The affinity is cleared so a later requeue treats this as an ordinary task
        // on the machine it actually cold-started on.
        Assert.Null(row.PreferredMachine);
        Assert.Null(row.OnMachineGone);
        Assert.Null(row.HarnessSessionRef);
        // …and the lost conversational memory is recorded so the Lead can see it (§11).
        var memoryLost = await v.TaskEvents.AsNoTracking()
            .SingleAsync(e => e.TaskId == task.Value && e.Kind == TaskEventRow.ContinuationMemoryLostKind);
        Assert.Contains("m1", memoryLost.Detail);
        Assert.Contains("m2", memoryLost.Detail);
    }

    [SkippableFact]
    public async Task Pin_waits_in_submitted_and_dispatches_when_the_machine_returns()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        await CreateContinuation(db, "m1", "sess-1", MachineGonePolicy.Pin);

        // m1 gone, PIN → no machine may claim it; it stays submitted.
        Assert.IsType<StoreResult.NotFound>(
            await NewStore(db).DispatchNextAsync(Machine("m2"), WorkerInstanceId.New(), default, ["m2"]));
        await using (var v = pg.NewContext())
            Assert.Equal(TaskState.Submitted, (await v.Tasks.AsNoTracking().SingleAsync()).State);

        // m1 returns → it claims the task and resumes with the inherited ref.
        var applied = Assert.IsType<StoreResult.Applied>(
            await NewStore(db).DispatchNextAsync(Machine("m1"), WorkerInstanceId.New(), default, ["m1", "m2"]));
        Assert.Equal("sess-1", applied.HarnessSessionRef);
    }

    [SkippableFact]
    public async Task Forking_one_task_into_two_continuations_both_inherit_the_same_session_ref()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        // Two continuations of the same prior task — both legal, both seeded with the
        // shared inherited session ref (§11: each stamps its OWN new ref after start,
        // via the existing SessionStartedEvent path).
        await CreateContinuation(db, "m1", "sess-1", MachineGonePolicy.Degrade);
        await CreateContinuation(db, "m1", "sess-1", MachineGonePolicy.Degrade);

        var first = Assert.IsType<StoreResult.Applied>(
            await NewStore(db).DispatchNextAsync(Machine("m1"), WorkerInstanceId.New(), default, ["m1"]));
        var second = Assert.IsType<StoreResult.Applied>(
            await NewStore(db).DispatchNextAsync(Machine("m1"), WorkerInstanceId.New(), default, ["m1"]));

        Assert.NotEqual(first.Task.Id, second.Task.Id);
        Assert.Equal("sess-1", first.HarnessSessionRef);
        Assert.Equal("sess-1", second.HarnessSessionRef);
    }

    [SkippableFact]
    public async Task Cross_team_continuation_is_rejected_at_creation()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        // The engine gate fires through the store: a continuation whose continued task
        // belongs to another Team is refused before any row is written.
        var result = await NewStore(db).CreateAsync(new CreateTask(
            Lead, Team, "criteria", CompletionMode.Lead, null, TeamBudgetRemains: true,
            Continues: new Continuation(TaskId.New(), TeamId.New(), "m1", "sess-1", MachineGonePolicy.Degrade, null)));

        var rejected = Assert.IsType<StoreResult.Rejected>(result);
        Assert.Equal(Rule.ContinuationSameTeamOnly, rejected.Rule);
    }

    [SkippableFact]
    public async Task Profile_the_preferred_machine_cannot_declare_is_rejected_at_creation()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();

        var result = await NewStore(db).CreateAsync(new CreateTask(
            Lead, Team, "criteria", CompletionMode.Lead, "gpu", TeamBudgetRemains: true,
            Continues: new Continuation(
                TaskId.New(), Team, "m1", "sess-1", MachineGonePolicy.Degrade,
                PreferredMachineProfiles: new HashSet<string> { "default" })));

        var rejected = Assert.IsType<StoreResult.Rejected>(result);
        Assert.Equal(Rule.ContinuationProfileDeclaredByPreferredMachine, rejected.Rule);
    }

    [SkippableFact]
    public async Task Get_team_state_renders_the_continues_lineage()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var (task, continued) = await CreateContinuation(db, "m1", "sess-1", MachineGonePolicy.Degrade);

        var view = await NewStore(db).GetTeamStateAsync(Team);
        var summary = view.Tasks.Single(t => t.TaskId == task.Value);
        Assert.Equal(continued.Value, summary.ContinuesTaskId);
    }
}
