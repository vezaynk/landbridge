using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// §9 check 9 / §9.9 budget accounting — <b>containment, not metering</b>. Nothing ingests
/// spend telemetry, so the ceiling is enforced on what Docket AUTHORIZED: each dispatch
/// commits the per-dispatch cap it hands the harness. These tests pin the three properties
/// that make that honest rather than merely convenient — monotonic commitment, per-dispatch
/// (not per-task) accounting, and an unconfigured Team staying unbounded.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TeamBudgetTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly MachineSnapshot Machine =
        new("box-1", Ready: true, UnderBackPressure: false, new HashSet<string> { "default" });

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── The unconfigured default ──────────────────────────────────────────────

    [SkippableFact]
    public async Task A_team_with_no_budget_row_is_unbounded_and_caps_nothing()
    {
        // A Docket with no budgets configured must behave exactly as it did before this
        // existed: work is admitted, and no cap is imposed on the harness.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();

        var view = await budgets.ReadAsync(team);

        Assert.Null(view.CeilingUsd);
        Assert.Null(view.Remaining);   // unbounded, NOT zero
        Assert.False(view.Exhausted);
        Assert.True(await budgets.HasHeadroomAsync(team));

        var applied = await DispatchOneAsync(db, team);
        Assert.Null(applied.BudgetCapUsd);
    }

    // ── Reservation, and its monotonicity ─────────────────────────────────────

    [SkippableFact]
    public async Task Each_dispatch_commits_the_per_dispatch_cap_and_hands_it_to_the_harness()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();
        await budgets.SetLimitsAsync(team, ceilingUsd: 10m, perTaskUsd: 4m);

        var applied = await DispatchOneAsync(db, team);

        // The cap rides back so DispatchService can put it on the wire — this is what
        // finally makes §9.9's harness-local backstop carry a number.
        Assert.Equal(4m, applied.BudgetCapUsd);
        var view = await budgets.ReadAsync(team);
        Assert.Equal(4m, view.CommittedUsd);
        Assert.Equal(6m, view.Remaining);
    }

    [SkippableFact]
    public async Task A_requeued_task_commits_twice_because_each_dispatch_is_a_fresh_process()
    {
        // Per DISPATCH, not per task. Attempt 2 is a new process that can burn the whole cap
        // again, so it costs the ceiling again — which also makes requeue amplification
        // visibly expensive instead of hiding it (§9.9).
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();
        await budgets.SetLimitsAsync(team, ceilingUsd: 100m, perTaskUsd: 7m);

        var first = await DispatchOneAsync(db, team);
        // Liveness loss returns it to submitted; the next pass dispatches the same task again.
        await new TaskStore(db, TimeProvider.System, budgets)
            .ApplyAsync(first.Task.Id, new LivenessLost(LivenessLossReason.MachineReboot));
        var second = await new TaskStore(db, TimeProvider.System, budgets)
            .DispatchNextAsync(Machine, WorkerInstanceId.New());

        var applied = Assert.IsType<StoreResult.Applied>(second);
        Assert.Equal(first.Task.Id, applied.Task.Id);   // the same task, attempt 2
        Assert.Equal(2, applied.Task.Attempt);
        Assert.Equal(14m, (await budgets.ReadAsync(team)).CommittedUsd);
    }

    [SkippableFact]
    public async Task Commitment_is_never_released_when_a_task_completes()
    {
        // "Unspent" is precisely what cannot be measured, and releasing would turn a
        // LIFETIME ceiling into a concurrency limiter — a $100 Team could run ten thousand
        // sequential $10 tasks. So a finished task's authorization stands (§4, §9.9).
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();
        await budgets.SetLimitsAsync(team, ceilingUsd: 20m, perTaskUsd: 5m);
        var store = new TaskStore(db, TimeProvider.System, budgets);

        var applied = await DispatchOneAsync(db, team);
        var instance = applied.Task.CurrentInstance!.Value;
        await store.ApplyAsync(applied.Task.Id, new ReportResult(new WorkerCaller(team, applied.Task.Id, instance), "ref"));
        await store.ApplyAsync(applied.Task.Id, new VerdictAccept(new LeadClaim(team)));

        Assert.Equal(5m, (await budgets.ReadAsync(team)).CommittedUsd);
    }

    [SkippableFact]
    public async Task Only_a_never_dispatched_commitment_is_released()
    {
        // The one exception: no process ever ran, so no spend was possible, and holding the
        // commitment would overstate authorization as badly as releasing a finished task's
        // would understate it.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();
        await budgets.SetLimitsAsync(team, ceilingUsd: 20m, perTaskUsd: 5m);
        await DispatchOneAsync(db, team);

        await budgets.ReleaseUndispatchedAsync(team, 5m);

        Assert.Equal(0m, (await budgets.ReadAsync(team)).CommittedUsd);

        // And a release can never manufacture headroom that was never committed.
        await budgets.ReleaseUndispatchedAsync(team, 999m);
        Assert.Equal(0m, (await budgets.ReadAsync(team)).CommittedUsd);
    }

    // ── The ceiling biting ────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_dispatch_that_would_cross_the_ceiling_is_refused_and_commits_nothing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();
        // Room for exactly two dispatches at 5.
        await budgets.SetLimitsAsync(team, ceilingUsd: 10m, perTaskUsd: 5m);

        await DispatchOneAsync(db, team);
        await DispatchOneAsync(db, team);
        var third = await SeedAndDispatchAsync(db, team);

        var rejected = Assert.IsType<StoreResult.Rejected>(third);
        Assert.Equal(Rule.TeamBudgetCeiling, rejected.Rule);
        Assert.Contains("a human must raise it", rejected.Reason);

        var view = await budgets.ReadAsync(team);
        Assert.Equal(10m, view.CommittedUsd);   // the refused dispatch committed nothing
        Assert.True(view.Exhausted);
        Assert.Equal(0m, view.Remaining);
        // And the task it refused is still submitted, ready for when a human raises the ceiling.
        await using var check = pg.NewContext();
        Assert.Contains(TaskState.Submitted, await check.Tasks.Select(t => t.State).ToListAsync());
    }

    [SkippableFact]
    public async Task An_exhausted_team_stops_admitting_new_tasks_and_is_listed_for_containment()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();
        await budgets.SetLimitsAsync(team, ceilingUsd: 5m, perTaskUsd: 5m);

        Assert.True(await budgets.HasHeadroomAsync(team));
        await DispatchOneAsync(db, team);

        // create_task's real gate now says no (§9 check 9, previously hardcoded true).
        Assert.False(await budgets.HasHeadroomAsync(team));
        // And the Team shows up for the sweep that stops its working tasks.
        Assert.Contains(team, await budgets.ExhaustedTeamsAsync());
    }

    [SkippableFact]
    public async Task Raising_the_ceiling_unblocks_the_team_without_erasing_what_was_committed()
    {
        // The escape valve is a human raising the ceiling — the control this whole mechanism
        // exists to give them. It must not also wipe the record of prior authorization.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();
        await budgets.SetLimitsAsync(team, ceilingUsd: 5m, perTaskUsd: 5m);
        await DispatchOneAsync(db, team);
        Assert.False(await budgets.HasHeadroomAsync(team));

        await budgets.SetLimitsAsync(team, ceilingUsd: 50m, perTaskUsd: 5m);

        var view = await budgets.ReadAsync(team);
        Assert.Equal(5m, view.CommittedUsd);   // history intact
        Assert.Equal(45m, view.Remaining);
        Assert.True(await budgets.HasHeadroomAsync(team));
    }

    [SkippableFact]
    public async Task A_ceiling_with_no_per_dispatch_cap_admits_work_but_offers_no_backstop()
    {
        // Honest degenerate case: a ceiling can be set without a cap, and then there is
        // nothing to commit and nothing to enforce harness-side. It must not silently
        // behave as if it were enforcing something (§9.9).
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();
        await budgets.SetLimitsAsync(team, ceilingUsd: 10m, perTaskUsd: null);

        var applied = await DispatchOneAsync(db, team);

        Assert.Null(applied.BudgetCapUsd);   // no cap reaches the harness
        Assert.Equal(0m, (await budgets.ReadAsync(team)).CommittedUsd);
        Assert.True(await budgets.HasHeadroomAsync(team));
    }

    [SkippableFact]
    public async Task Limits_must_be_positive()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var team = TeamId.New();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => budgets.SetLimitsAsync(team, 0m, 1m));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => budgets.SetLimitsAsync(team, -5m, 1m));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => budgets.SetLimitsAsync(team, 5m, 0m));
        // Clearing both is how a human removes a ceiling entirely.
        await budgets.SetLimitsAsync(team, null, null);
        Assert.Null((await budgets.ReadAsync(team)).CeilingUsd);
    }

    // ── The containment sweep (§9.9: the ceiling stops work, not only admission) ──

    [SkippableFact]
    public async Task An_exhausted_teams_working_task_is_stopped_and_the_reason_recorded()
    {
        // Refusing new dispatch bounds what a Team can START. Without this, work already
        // running when the ceiling was reached would run on past it — so this is the half that
        // makes the ceiling containment rather than an admission check.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var team = TeamId.New();
        TaskId task;
        await using (var db = pg.NewContext())
        {
            var budgets = new TeamBudgetService(db, clock);
            await budgets.SetLimitsAsync(team, ceilingUsd: 5m, perTaskUsd: 5m);
            task = (await DispatchOneAsync(db, team)).Task.Id;
            Assert.True((await budgets.ReadAsync(team)).Exhausted);
        }

        var sent = new List<RunnerCommand>();
        var registry = LiveMachine(clock, "box-1", task, sent);
        await NewDispatchService(clock, registry).CheckLivenessAsync(CancellationToken.None);

        // The graceful §10/§11 wind-down, not a bare kill: the work is not wrong, the Team
        // merely ran out of authorization, so the transcript is worth keeping for whoever
        // raises the ceiling.
        var stop = Assert.IsType<StopCommand>(Assert.Single(sent));
        Assert.Equal(task, stop.Task);
        Assert.Equal(StopDisposition.Preserve, stop.Disposition);
        Assert.Contains("ceiling", stop.Reason);

        // And the event row that lets the dashboard say WHY nothing is progressing — the
        // question a silence cannot answer for itself.
        await using var check = pg.NewContext();
        var evt = await check.TaskEvents.SingleAsync(
            e => e.TaskId == task.Value && e.Kind == TaskEventRow.BudgetExhaustedStopKind);
        Assert.Equal(team.Value, evt.TeamId);
        Assert.Contains("5 USD committed of 5 USD authorized", evt.Detail);
        Assert.Null(evt.FromState);   // telemetry, not a transition
        Assert.Null(evt.ToState);
    }

    [SkippableFact]
    public async Task A_team_within_its_ceiling_is_left_alone_by_the_sweep()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var team = TeamId.New();
        TaskId task;
        await using (var db = pg.NewContext())
        {
            var budgets = new TeamBudgetService(db, clock);
            await budgets.SetLimitsAsync(team, ceilingUsd: 100m, perTaskUsd: 5m);
            task = (await DispatchOneAsync(db, team)).Task.Id;
            Assert.False((await budgets.ReadAsync(team)).Exhausted);
        }

        var sent = new List<RunnerCommand>();
        var registry = LiveMachine(clock, "box-1", task, sent);
        await NewDispatchService(clock, registry).CheckLivenessAsync(CancellationToken.None);

        Assert.Empty(sent);
        await using var check = pg.NewContext();
        Assert.False(await check.TaskEvents.AnyAsync(e => e.Kind == TaskEventRow.BudgetExhaustedStopKind));
    }

    [SkippableFact]
    public async Task The_sweep_does_not_re_stop_a_task_it_has_already_stopped()
    {
        // A stopped task stays working for its whole wind-down window — comfortably longer
        // than one liveness tick — so without an idempotency record every pass would re-stop it
        // and write another event row. The row IS the record, which also survives a restart.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var team = TeamId.New();
        TaskId task;
        await using (var db = pg.NewContext())
        {
            await new TeamBudgetService(db, clock).SetLimitsAsync(team, ceilingUsd: 5m, perTaskUsd: 5m);
            task = (await DispatchOneAsync(db, team)).Task.Id;
        }

        var sent = new List<RunnerCommand>();
        var registry = LiveMachine(clock, "box-1", task, sent);
        var dispatch = NewDispatchService(clock, registry);

        await dispatch.CheckLivenessAsync(CancellationToken.None);
        await dispatch.CheckLivenessAsync(CancellationToken.None);

        Assert.Single(sent);
        await using var check = pg.NewContext();
        Assert.Equal(1, await check.TaskEvents.CountAsync(
            e => e.TaskId == task.Value && e.Kind == TaskEventRow.BudgetExhaustedStopKind));
    }

    [SkippableFact]
    public async Task A_task_whose_machine_is_gone_is_left_for_the_next_pass()
    {
        // Nothing to stop: no live connection holds it, and the exhausted ceiling already
        // refuses its re-dispatch. Writing the event anyway would claim a stop that was never
        // sent, so the candidate is left for a pass where delivery can actually happen.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = TimeProvider.System;
        var team = TeamId.New();
        TaskId task;
        await using (var db = pg.NewContext())
        {
            await new TeamBudgetService(db, clock).SetLimitsAsync(team, ceilingUsd: 5m, perTaskUsd: 5m);
            task = (await DispatchOneAsync(db, team)).Task.Id;
        }

        // A registry that knows no machine for the task at all.
        var registry = new RunnerConnectionRegistry(clock);
        await NewDispatchService(clock, registry).CheckLivenessAsync(CancellationToken.None);

        await using var check = pg.NewContext();
        Assert.False(await check.TaskEvents.AnyAsync(e => e.Kind == TaskEventRow.BudgetExhaustedStopKind));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DispatchService NewDispatchService(TimeProvider clock, RunnerConnectionRegistry registry) =>
        new(ScopeFactory(clock), registry, clock, NullLogger<DispatchService>.Instance);

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddScoped<TaskStore>();
        services.AddScoped<TeamBudgetService>();
        services.AddScoped<TokenService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>A registry with one ready machine holding <paramref name="task"/>, recording
    /// every command the plane ships down its connection.</summary>
    private static RunnerConnectionRegistry LiveMachine(
        TimeProvider clock, string machineId, TaskId task, List<RunnerCommand> sent)
    {
        var registry = new RunnerConnectionRegistry(clock);
        registry.Register(
            machineId, new HashSet<string>(["default"], StringComparer.Ordinal),
            (cmd, _) => { sent.Add(cmd); return Task.CompletedTask; });
        registry.ApplyHeartbeat(machineId, new MachineHeartbeat(
            machineId, Ready: true, UnderBackPressure: false, new SystemLoad(0, 0, 0),
            RunningTasks: 1, ["default"], DateTimeOffset.UtcNow));
        registry.TrackDispatch(machineId, task);
        return registry;
    }

    /// <summary>Create one task and dispatch it, asserting both succeeded.</summary>
    private async Task<StoreResult.Applied> DispatchOneAsync(DocketDbContext db, TeamId team) =>
        Assert.IsType<StoreResult.Applied>(await SeedAndDispatchAsync(db, team));

    /// <summary>Create one task and run a dispatch pass, returning whatever the store said.</summary>
    private async Task<StoreResult> SeedAndDispatchAsync(DocketDbContext db, TeamId team)
    {
        var budgets = new TeamBudgetService(db, TimeProvider.System);
        var store = new TaskStore(db, TimeProvider.System, budgets);
        // Created with headroom asserted separately; this seeds the row the dispatch claims.
        await store.CreateAsync(new CreateTask(
            new LeadClaim(team), team, "criteria", CompletionMode.Lead, Profile: null,
            TeamBudgetRemains: true));
        return await store.DispatchNextAsync(Machine, WorkerInstanceId.New());
    }
}
