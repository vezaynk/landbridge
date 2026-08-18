using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// §10 telemetry ingest / §12 measured view: storing what a harness said it consumed.
///
/// <para>These tests pin the properties that make a best-effort figure safe to show. Reports are
/// cumulative and §10's outbound ring may drop or reorder them, so the store keeps a high-water
/// mark: a redelivered report must not double a total and a reordered one must not walk it back.
/// Cost is the deliberate exception — it is the harness's arithmetic over the same tokens, so a
/// later smaller figure is a correction rather than a regression.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TaskUsageTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-08-12T12:00:00Z"));

    private async Task<(SessionId Session, TeamId Team)> SeedTaskAsync()
    {
        await using var db = pg.NewContext();
        var team = TeamId.New();
        var created = (StoreResult.Applied)await new SessionStore(db, _clock).CreateAsync(
            new CreateSession(new LeadClaim(team), team, "criteria", CompletionMode.Lead, null));
        return (created.Session.Id, team);
    }

    private static UsageReportedEvent Report(
        SessionId task,
        string? model = "claude-sonnet-5[1m]",
        long input = 100,
        long output = 20,
        long cacheRead = 900,
        long cacheWrite = 50,
        long? reasoning = null,
        decimal? cost = 0.25m) =>
        new(task, model, input, output, cacheRead, cacheWrite, reasoning, cost,
            DateTimeOffset.Parse("2026-08-12T12:00:00Z"));

    [SkippableFact]
    public async Task A_report_lands_as_one_row_readable_per_model()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (task, team) = await SeedTaskAsync();

        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(Report(task));

        await using (var db = pg.NewContext())
        {
            var row = await db.SessionUsage.AsNoTracking().SingleAsync();
            Assert.Equal(task.Value, row.SessionId);
            // The Team is denormalized off the task so the §12 roll-up is one indexed scan.
            Assert.Equal(team.Value, row.TeamId);
            Assert.Equal(100, row.InputTokens);
            Assert.Equal(900, row.CacheReadTokens);
            Assert.Equal(0.25m, row.CostUsd);
        }
    }

    [SkippableFact]
    public async Task A_redelivered_report_does_not_double_the_total()
    {
        // §10's ring is best-effort in both directions: the same cumulative report can arrive
        // twice. Summing would double a spend figure, which is the failure this shape avoids.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (task, _) = await SeedTaskAsync();

        for (var i = 0; i < 3; i++)
            await using (var db = pg.NewContext())
                await new SessionStore(db, _clock).RecordUsageAsync(Report(task, input: 100));

        await using (var db = pg.NewContext())
        {
            var row = await db.SessionUsage.AsNoTracking().SingleAsync();
            Assert.Equal(100, row.InputTokens);
        }
    }

    [SkippableFact]
    public async Task A_reordered_report_never_walks_a_total_backwards()
    {
        // Cumulative counters only rise, so an older report arriving late is stale rather than a
        // correction — clamping to the high-water mark is what makes a dropped report cost only
        // freshness.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (task, _) = await SeedTaskAsync();

        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(Report(task, input: 500, output: 90));
        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(Report(task, input: 100, output: 20));

        await using (var db = pg.NewContext())
        {
            var row = await db.SessionUsage.AsNoTracking().SingleAsync();
            Assert.Equal(500, row.InputTokens);
            Assert.Equal(90, row.OutputTokens);
        }
    }

    [SkippableFact]
    public async Task A_later_cost_correction_is_taken_even_when_it_is_smaller()
    {
        // Cost is not an independent counter — it is the harness's arithmetic over the same
        // tokens, so a revised figure is a correction to honour rather than a regression to
        // clamp. The counters beside it still clamp.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (task, _) = await SeedTaskAsync();

        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(Report(task, cost: 0.90m));
        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(Report(task, cost: 0.42m));

        await using (var db = pg.NewContext())
            Assert.Equal(0.42m, (await db.SessionUsage.AsNoTracking().SingleAsync()).CostUsd);
    }

    [SkippableFact]
    public async Task A_report_without_a_cost_does_not_erase_one_already_stated()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (task, _) = await SeedTaskAsync();

        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(Report(task, cost: 0.77m));
        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(Report(task, cost: null));

        await using (var db = pg.NewContext())
            Assert.Equal(0.77m, (await db.SessionUsage.AsNoTracking().SingleAsync()).CostUsd);
    }

    [SkippableFact]
    public async Task Two_models_on_one_dispatch_are_two_rows_not_a_collision()
    {
        // A real claude dispatch reports several models — its main loop and whatever auxiliary
        // work ran cheaper. Keying per (task, model) is what keeps both figures.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (task, _) = await SeedTaskAsync();

        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, _clock);
            await store.RecordUsageAsync(Report(task, model: "claude-sonnet-5[1m]", input: 100, cost: 0.90m));
            await store.RecordUsageAsync(Report(task, model: "claude-haiku-4-5-20251001", input: 521, cost: 0.0006m));
        }

        await using (var db = pg.NewContext())
        {
            var rows = await db.SessionUsage.AsNoTracking().OrderBy(u => u.Model).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(521, rows[0].InputTokens);
            Assert.Equal(100, rows[1].InputTokens);
        }
    }

    [SkippableFact]
    public async Task An_unattributed_report_coexists_with_a_named_one()
    {
        // The unnamed model is stored as the empty string (a composite key cannot hold a NULL)
        // and must not collide with a named row, nor leak the empty string out through the view.
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (task, team) = await SeedTaskAsync();

        await using (var db = pg.NewContext())
        {
            var store = new SessionStore(db, _clock);
            await store.RecordUsageAsync(Report(task, model: null, input: 10, cost: null));
            await store.RecordUsageAsync(Report(task, model: "gpt-5.1-codex", input: 20, cost: null));
        }

        await using (var db = pg.NewContext())
        {
            var rows = await db.SessionUsage.AsNoTracking().Where(u => u.TeamId == team.Value).ToListAsync();
            Assert.Equal(2, rows.Count);
            var view = TeamUsageView.From(rows);
            Assert.True(view.Measured);
            Assert.Equal(30, view.InputTokens);
            // Null out of the view, never the empty string that storage uses.
            Assert.Contains(view.ByModel, m => m.Model is null);
            // Neither model reported a cost, so the Team has no cost total at all — and the
            // partial flag says the token figures are nonetheless real.
            Assert.Null(view.ReportedCostUsd);
            Assert.True(view.CostIsPartial);
        }
    }

    /// <summary>
    /// The <c>""</c>-versus-<c>null</c> contract on the <b>per-task</b> read path, pinned at $0.
    ///
    /// <para>The Team roll-up above already covers this through <see cref="TeamUsageView"/>, but
    /// the single-row path had no fast test of its own — and that is the gap a real dispatch found
    /// the expensive way. A harness that names no model (OpenCode reports none anywhere) writes
    /// the empty string storage requires, because a composite primary key cannot hold a NULL; the
    /// invariant every reader above the row depends on is that <c>model is null ⟺ unnamed</c>, and
    /// <see cref="SessionUsageView.From"/> is the one place that translation happens. A reader that
    /// bypasses it sees <c>""</c> and can only interpret it as a harness that reported a model
    /// whose name is blank — the exact misreading the deleted attribution enum used to guard
    /// against, now guarded by this invariant instead. So it is worth a test that costs nothing.</para>
    /// </summary>
    [SkippableFact]
    public async Task An_unnamed_models_empty_string_never_escapes_the_per_task_view()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var (task, _) = await SeedTaskAsync();

        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(
                Report(task, model: null, input: 900, output: 120, cost: 0.0123m));

        await using (var db = pg.NewContext())
        {
            var row = await db.SessionUsage.AsNoTracking().SingleAsync(u => u.SessionId == task.Value);

            // Storage: the empty string, deliberately. Asserted so a future change that made this
            // column nullable has to come here and say so rather than silently drifting.
            Assert.Equal("", row.Model);

            // The view: null. Everything else passes through untouched, so this is the one field
            // the mapping is allowed to change.
            var view = SessionUsageView.From(row);
            Assert.Null(view.Model);
            Assert.Equal(900, view.InputTokens);
            Assert.Equal(0.0123m, view.CostUsd);

            // And a named model is NOT rewritten — the mapping keys off emptiness, not presence,
            // so the fix for the unnamed case cannot quietly erase an attributed one.
            var named = SessionUsageView.From(new SessionUsageRow { Model = "anthropic/claude-haiku-4-5-20251001" });
            Assert.Equal("anthropic/claude-haiku-4-5-20251001", named.Model);
        }
    }

    [SkippableFact]
    public async Task A_team_nothing_reported_for_is_unmeasured_rather_than_zero()
    {
        // The §9.10 distinction, applied to the same kind of number: an absence of measurement
        // is not a measurement of nothing, and the §12 view renders the two differently.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await SeedTaskAsync();

        await using var db = pg.NewContext();
        var view = TeamUsageView.From(await db.SessionUsage.AsNoTracking().ToListAsync());

        Assert.False(view.Measured);
        Assert.Equal(0, view.TotalTokens);
        Assert.Null(view.ReportedCostUsd);
    }

    [SkippableFact]
    public async Task A_report_for_a_task_that_no_longer_exists_is_dropped()
    {
        // The event carries only a task id (§10), so the Team comes off the row. These rows are
        // read by nothing but the dashboard, so a report arriving after the task is gone is
        // discarded rather than fatal on the receive loop.
        Skip.IfNot(pg.Available, pg.SkipReason);

        await using (var db = pg.NewContext())
            await new SessionStore(db, _clock).RecordUsageAsync(Report(SessionId.New()));

        await using (var db = pg.NewContext())
            Assert.Empty(await db.SessionUsage.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task The_team_roll_up_sums_across_tasks_and_flags_a_partial_cost()
    {
        // Two tasks, one Team: one harness reports cost and one does not. The token total is
        // whole, the cost total is a floor, and the view says which.
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var seed = pg.NewContext();
        var team = TeamId.New();
        var store = new SessionStore(seed, _clock);
        var a = ((StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "a", CompletionMode.Lead, null))).Session.Id;
        var b = ((StoreResult.Applied)await store.CreateAsync(
            new CreateSession(new LeadClaim(team), team, "b", CompletionMode.Lead, null))).Session.Id;

        await using (var db = pg.NewContext())
        {
            var s = new SessionStore(db, _clock);
            await s.RecordUsageAsync(Report(a, model: "claude-sonnet-5[1m]", input: 100, cost: 0.50m));
            await s.RecordUsageAsync(Report(b, model: "gpt-5.1-codex", input: 300, cost: null));
        }

        await using (var db = pg.NewContext())
        {
            var view = TeamUsageView.From(
                await db.SessionUsage.AsNoTracking().Where(u => u.TeamId == team.Value).ToListAsync());
            Assert.Equal(400, view.InputTokens);
            Assert.Equal(0.50m, view.ReportedCostUsd);
            Assert.True(view.CostIsPartial);
        }
    }

    [SkippableFact]
    public void The_pricing_multiplier_is_a_stub_that_yields_no_figure_rather_than_zero()
    {
        // The user's ruling made concrete: today the derived path produces NOTHING, not a
        // 1.0-multiplied number and not $0.00. A zero would claim the work was free.
        var pricing = new ModelPricing();

        Assert.False(pricing.TryDerive("gpt-5.1-codex", 1000, 100, 50, 10, out var cost));
        Assert.Equal(0m, cost);
    }
}
