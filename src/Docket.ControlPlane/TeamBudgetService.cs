using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane;

/// <summary>
/// Team budget accounting, spec §9 check 9 / §9.9 — <b>containment, not metering</b>.
///
/// <para>The distinction is the whole design. Docket does not sit between a harness and
/// its model provider and ingests no token telemetry (§10 describes the intent; no
/// receiver exists), so measured spend is not available to enforce on. What is knowable
/// is what Docket <em>authorized</em>: each dispatch is handed a hard per-dispatch cap
/// its harness enforces locally, so committing that cap at dispatch bounds the Team's
/// exposure without measuring anything — and cannot be defeated by a telemetry signal
/// that never arrives, which is exactly how a metered ceiling fails.</para>
///
/// <para>Two properties make it honest rather than merely convenient:
/// <list type="bullet">
/// <item><b>Commit per dispatch, not per task.</b> Every dispatch is a fresh process
/// that can burn the whole cap, so a task on its third attempt has committed three
/// caps. Conservative and true — and it makes requeue amplification visibly expensive,
/// which bounds the infrastructure-requeue loop instead of hiding it.</item>
/// <item><b>Committed only rises, with no exceptions.</b> It is authorized-spend-to-date
/// for the Team's life, never released on completion, because "unspent" is the one
/// quantity that cannot be known and because releasing would turn a lifetime ceiling into
/// a concurrency limiter (§4: a Team owns a budget and terminates). Nor is there anything
/// to release earlier: the commitment is made <em>inside the dispatch transaction</em>
/// (<see cref="TryCommitDispatchAsync"/>), so a task cancelled before it ever dispatched
/// never charged the ceiling in the first place — creation only asks whether headroom
/// exists (<see cref="HasHeadroomAsync"/>) and commits nothing. The escape valve is a
/// human raising the ceiling, which is the control this exists to give them.</item>
/// </list></para>
///
/// <para>Scoped alongside <see cref="TaskStore"/>, same clock/db injection style.</para>
/// </summary>
public sealed class TeamBudgetService(DocketDbContext db, TimeProvider clock)
{
    /// <summary>
    /// The Team's ceiling, what is committed against it, and the per-dispatch cap — or
    /// an unconfigured view when no row exists. Read by <c>get_team_state</c> (a Lead may
    /// see its own ceiling) and by the §12 dashboard.
    /// </summary>
    public async Task<TeamBudgetView> ReadAsync(TeamId team, CancellationToken ct = default)
    {
        var row = await db.TeamBudgets.AsNoTracking().FirstOrDefaultAsync(t => t.TeamId == team.Value, ct);
        return TeamBudgetView.From(row);
    }

    /// <summary>
    /// Set (or clear) a Team's ceiling and per-dispatch cap. <b>Human-only path</b> — the
    /// §12 dashboard calls this; there is deliberately no MCP tool, because a Lead able to
    /// raise its own ceiling is enforcement where a model can reason past it (§2
    /// principle 3). Committed-to-date is never touched here: raising the ceiling is how a
    /// human un-blocks a Team, and that must not also erase the record of what was already
    /// authorized.
    /// </summary>
    public async Task SetLimitsAsync(
        TeamId team, decimal? ceilingUsd, decimal? perTaskUsd, CancellationToken ct = default)
    {
        if (ceilingUsd is < 0 or 0 && ceilingUsd is not null)
            throw new ArgumentOutOfRangeException(nameof(ceilingUsd), ceilingUsd, "a ceiling must be positive");
        if (perTaskUsd is < 0 or 0 && perTaskUsd is not null)
            throw new ArgumentOutOfRangeException(nameof(perTaskUsd), perTaskUsd, "a per-dispatch cap must be positive");

        var row = await db.TeamBudgets.FirstOrDefaultAsync(t => t.TeamId == team.Value, ct);
        if (row is null)
        {
            row = new TeamBudgetRow { TeamId = team.Value, CommittedUsd = 0m };
            db.TeamBudgets.Add(row);
        }
        row.CeilingUsd = ceilingUsd;
        row.PerTaskUsd = perTaskUsd;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether the Team can afford to authorize one more dispatch — the real input to
    /// <c>CreateTask.TeamBudgetRemains</c>, which until now was hardcoded true and made §9
    /// check 9 a lie.
    ///
    /// <para>An unset ceiling admits work: a Docket with no budgets configured behaves as
    /// it always did. With a ceiling but no per-dispatch cap there is nothing to commit and
    /// no backstop, so the ceiling can only be judged against what is already committed.</para>
    /// </summary>
    public async Task<bool> HasHeadroomAsync(TeamId team, CancellationToken ct = default)
    {
        var view = await ReadAsync(team, ct);
        return view.Remaining is null || view.Remaining >= (view.PerTaskUsd ?? 0m);
    }

    /// <summary>
    /// Commit one dispatch's cap against the Team, in the caller's transaction, and return
    /// the cap to hand the harness — or <see cref="BudgetCommit.Exhausted"/> when the
    /// ceiling has no room for another dispatch.
    ///
    /// <para>Called from inside <see cref="TaskStore.DispatchNextAsync"/>'s transaction and
    /// after its row lock, so the commit lands atomically with the dispatch transition: a
    /// dispatch that happens is always paid for, and one that is refused commits nothing.
    /// The returned cap becomes <c>DispatchCommand.BudgetUsd</c> — which is what finally
    /// makes §9.9's harness-local backstop carry a number.</para>
    /// </summary>
    public async Task<BudgetCommit> TryCommitDispatchAsync(TeamId team, CancellationToken ct = default)
    {
        var row = await db.TeamBudgets.FirstOrDefaultAsync(t => t.TeamId == team.Value, ct);
        // No row, or no ceiling: unconfigured Teams are unbounded, exactly as before this
        // existed. Nothing to commit and nothing to cap.
        if (row?.CeilingUsd is null)
            return new BudgetCommit.Allowed(row?.PerTaskUsd);

        var cap = row.PerTaskUsd ?? 0m;
        if (row.CommittedUsd + cap > row.CeilingUsd.Value)
            return new BudgetCommit.Exhausted(row.CeilingUsd.Value, row.CommittedUsd);

        row.CommittedUsd += cap;
        row.UpdatedAt = clock.GetUtcNow();
        // Flushed here, but still atomic with the dispatch: the caller holds an open
        // transaction around this call, so the commitment and the transition land together or
        // not at all. (Mutating the tracked row without saving would silently lose the
        // commitment — the dispatch path has no later SaveChanges to ride on.)
        await db.SaveChangesAsync(ct);
        return new BudgetCommit.Allowed(row.PerTaskUsd);
    }

    /// <summary>Every Team currently over its ceiling, for the containment sweep that
    /// stops their working tasks (§9.9: the ceiling drives refuse-new-dispatch AND
    /// <c>stop</c>).</summary>
    public async Task<IReadOnlyList<TeamId>> ExhaustedTeamsAsync(CancellationToken ct = default) =>
        (await db.TeamBudgets.AsNoTracking()
            .Where(t => t.CeilingUsd != null && t.CommittedUsd >= t.CeilingUsd)
            .Select(t => t.TeamId)
            .ToListAsync(ct))
        .Select(id => new TeamId(id))
        .ToList();
}

/// <summary>
/// A Team's budget as a reader sees it (§9.9). <see cref="Remaining"/> is null when no
/// ceiling is set — unbounded, not zero, which is the unconfigured default.
/// </summary>
public sealed record TeamBudgetView(
    decimal? CeilingUsd, decimal? PerTaskUsd, decimal CommittedUsd, DateTimeOffset? UpdatedAt)
{
    /// <summary>Ceiling minus committed, or null when unbounded. Never negative.</summary>
    public decimal? Remaining => CeilingUsd is { } ceiling ? Math.Max(0m, ceiling - CommittedUsd) : null;

    /// <summary>Whether the ceiling is set and reached — the state that refuses new
    /// dispatch and stops working tasks.</summary>
    public bool Exhausted => CeilingUsd is { } ceiling && CommittedUsd >= ceiling;

    /// <summary>
    /// The one row→view mapping, shared by <see cref="TeamBudgetService.ReadAsync"/> and the
    /// §12 dashboard's own read (<see cref="DashboardQueries"/>), so the two cannot drift on
    /// what an absent row means. A null row is the unconfigured Team: no ceiling, nothing
    /// committed, unbounded — not a zero budget.
    /// </summary>
    public static TeamBudgetView From(TeamBudgetRow? row) =>
        row is null
            ? new TeamBudgetView(null, null, 0m, null)
            : new TeamBudgetView(row.CeilingUsd, row.PerTaskUsd, row.CommittedUsd, row.UpdatedAt);
}

/// <summary>
/// A working task the §9.9 containment sweep owes a <c>stop</c>: its Team is over its
/// ceiling and no stop has been recorded for it yet. Read by
/// <see cref="TaskStore.WorkingTasksAwaitingBudgetStopAsync"/>; the Team rides along
/// because the sweep needs it to name the ceiling in the event row it writes.
/// </summary>
public sealed record BudgetStopCandidate(TaskId Task, TeamId Team);

/// <summary>Outcome of committing one dispatch against a Team's ceiling (§9.9).</summary>
public abstract record BudgetCommit
{
    private BudgetCommit() { }

    /// <summary>Authorized. <see cref="CapUsd"/> is the per-dispatch cap to hand the
    /// harness (null when the Team sets none — then there is no backstop).</summary>
    public sealed record Allowed(decimal? CapUsd) : BudgetCommit;

    /// <summary>The ceiling has no room for another dispatch.</summary>
    public sealed record Exhausted(decimal CeilingUsd, decimal CommittedUsd) : BudgetCommit;
}
