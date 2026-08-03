using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane;

/// <summary>
/// Per-Team relay byte accounting, spec §9 check 10 / §9.10 — <b>measurement without
/// enforcement</b>, and the one number in §9's budget story that is actually measured rather
/// than authorized.
///
/// <para><b>Nothing is enforced on it, deliberately.</b> No ceiling is checked against these
/// bytes, because §8.3 forbids severing an established splice mid-flight — a database session
/// or websocket is never cut by policy once spliced — so what a reached byte ceiling should
/// actually <em>do</em> is an unresolved design question, not missing plumbing. Check 10's
/// enforceable half is the forward rate limit at grant mint
/// (<see cref="Auth.RelayGrantService"/>), which bounds how many forwards a Team may open
/// without needing to measure anything.</para>
///
/// <para><b>Best-effort by construction.</b> The relay reports asynchronously and in batches,
/// so a relay that dies loses whatever it had not yet sent, and the figure trails live traffic
/// by up to one report interval. That is why <see cref="TeamForwardUsageRow.UpdatedAt"/> is
/// surfaced alongside the total: a reader should be able to see how stale the number is instead
/// of trusting it as current. A containment signal, never an invoice.</para>
///
/// <para>Attribution comes from the forward id, which the plane minted and therefore owns: the
/// relay knows only opaque forward ids and never learns which Team it is moving bytes for
/// (§8.3 — it moves opaque bytes and interprets nothing). Resolution goes through the grant
/// row, which survives revocation, so a splice that outlives its grant still attributes
/// correctly.</para>
/// </summary>
public sealed class TeamForwardUsageService(DocketDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Attributes a batch of per-forward byte deltas to their Teams and adds them to each
    /// Team's running total. Returns how many reports were attributed, so the caller can see
    /// that a batch naming forward ids the plane has never issued was dropped rather than
    /// silently counted.
    ///
    /// <para>Unknown forward ids are skipped, not rejected: the batch is a best-effort report
    /// from a component that may be racing a grant's lifetime, and failing the whole batch
    /// would throw away good measurements for one bad id. Non-positive deltas are skipped for
    /// the same reason — this counter only rises, and a negative report is either a bug or a
    /// lie, neither of which should be able to reduce a total.</para>
    /// </summary>
    public async Task<int> RecordAsync(
        IReadOnlyList<ForwardByteReport> reports, CancellationToken ct = default)
    {
        var deltas = reports.Where(r => r.Bytes > 0).ToList();
        if (deltas.Count == 0)
            return 0;

        var forwardIds = deltas.Select(r => r.ForwardId).Distinct().ToList();
        var teamByForward = await db.RelayGrants.AsNoTracking()
            .Where(g => forwardIds.Contains(g.ForwardId))
            .Select(g => new { g.ForwardId, g.TeamId })
            .ToDictionaryAsync(g => g.ForwardId, g => g.TeamId, ct);

        // Aggregate per Team first so a batch touching one Team's several forwards is a single
        // row update rather than one per forward.
        var byTeam = new Dictionary<Guid, long>();
        var attributed = 0;
        foreach (var report in deltas)
        {
            if (!teamByForward.TryGetValue(report.ForwardId, out var team))
                continue;
            byTeam[team] = byTeam.GetValueOrDefault(team) + report.Bytes;
            attributed++;
        }
        if (byTeam.Count == 0)
            return 0;

        var teamIds = byTeam.Keys.ToList();
        var existing = await db.TeamForwardUsage
            .Where(u => teamIds.Contains(u.TeamId))
            .ToDictionaryAsync(u => u.TeamId, ct);

        var now = clock.GetUtcNow();
        foreach (var (team, bytes) in byTeam)
        {
            if (existing.TryGetValue(team, out var row))
            {
                row.ForwardedBytes += bytes;
                row.UpdatedAt = now;
            }
            else
            {
                db.TeamForwardUsage.Add(new TeamForwardUsageRow
                {
                    TeamId = team, ForwardedBytes = bytes, UpdatedAt = now,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return attributed;
    }

    /// <summary>
    /// A Team's reported byte total, or an explicit <c>null</c>-timestamped zero when nothing
    /// has ever been reported. Zero-with-no-timestamp is meaningfully different from a measured
    /// zero: it means no relay has spoken, which is what a reader needs to know before drawing
    /// any conclusion from a low number.
    /// </summary>
    public async Task<TeamForwardUsageView> ReadAsync(TeamId team, CancellationToken ct = default) =>
        TeamForwardUsageView.From(
            await db.TeamForwardUsage.AsNoTracking().FirstOrDefaultAsync(u => u.TeamId == team.Value, ct));
}

/// <summary>One forward's byte delta as the relay reports it (§9.10). A delta since the relay's
/// last report, not a running total, so a report lost in flight costs that interval's bytes
/// rather than corrupting the total.</summary>
public sealed record ForwardByteReport(Guid ForwardId, long Bytes);

/// <summary>
/// A Team's relay byte usage as a reader sees it (§9.10). <see cref="ReportedAt"/> is null when
/// no relay has ever reported — the honest distinction between "no traffic measured" and "no
/// measurement taken".
/// </summary>
public sealed record TeamForwardUsageView(long ForwardedBytes, DateTimeOffset? ReportedAt)
{
    /// <summary>Whether any relay has reported at all. False makes
    /// <see cref="ForwardedBytes"/> an absence of data, not a measurement of zero.</summary>
    public bool Measured => ReportedAt is not null;

    /// <summary>The one row→view mapping, shared by the service and the §12 dashboard's own
    /// read so the two cannot drift on what an absent row means.</summary>
    public static TeamForwardUsageView From(TeamForwardUsageRow? row) =>
        row is null ? new TeamForwardUsageView(0, null) : new TeamForwardUsageView(row.ForwardedBytes, row.UpdatedAt);
}
