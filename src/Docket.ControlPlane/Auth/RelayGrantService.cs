using System.Security.Cryptography;
using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane.Auth;

/// <summary>
/// Issues and validates forward grants (spec §8.3), alongside
/// <see cref="TokenService"/> and in the same clock/db-injection style. A grant
/// is the connection-establishment credential for a relay tunnel: bound to
/// <c>{consumer, service, expiry}</c>, opaque, hashed at rest (§5), and checked
/// once when each end opens its tunnel.
///
/// <para>Kept deliberately separate from <see cref="TokenService"/> and the
/// credential classes: a grant is not a Principal and authenticates nothing to
/// the MCP surface. It has its own table (<see cref="RelayGrantRow"/>) and its
/// own <c>dkt_g_</c> prefix.</para>
///
/// <para>Revocation is not this service's job — it rides the existing
/// <see cref="ClearServicesAndForwards"/> effect in <see cref="TaskStore"/>,
/// which revokes a task's live grants the moment it leaves <c>working</c>, next
/// to where it already clears that task's registered services (§6).</para>
/// </summary>
public sealed class RelayGrantService(DocketDbContext db, TimeProvider clock)
{
    /// <summary>
    /// A grant establishes a connection; it does not authorize an ongoing
    /// session (§8.3: an established splice persists past expiry). Short by
    /// design — long enough to cover the open handshake, no longer.
    /// </summary>
    public static readonly TimeSpan GrantTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Issues a grant for <paramref name="consumer"/> to reach
    /// <paramref name="serviceName"/> (spec §8.3 step 2). Checks, all scoped to
    /// the consumer's Team so another Team's services never leak (§8.2):
    /// the service is registered in the consumer's Team, and its owning task is
    /// still <c>working</c>. On success mints an opaque <c>dkt_g_</c> grant
    /// (hashed at rest), a fresh forward id, and a short expiry.
    /// </summary>
    public Task<RelayGrantResult> IssueAsync(
        WorkerCaller consumer, string serviceName, CancellationToken ct = default) =>
        // The §8.3 consumer is a worker task; bind it for attribution.
        MintAsync(consumer.Team, serviceName, consumer.Task.Value, consumer.Instance.Value, ct);

    /// <summary>
    /// Issues a grant for an §8.4 HTTP preview: the consumer is the preview
    /// frontend, not a task, so there is no consumer worker instance to bind — the
    /// mint records only the producer + Team. Runs the identical check-11 gate as
    /// <see cref="IssueAsync"/> (registered service owned by a working task in
    /// <paramref name="team"/>), and the resulting grant validates and revokes
    /// through the same paths (validation ignores the consumer binding; revocation
    /// keys on <c>ProducerTaskId</c> via <see cref="ClearServicesAndForwards"/>).
    /// The producer machine dials on demand — a fresh grant + forward id per
    /// browser connection (§8.4) — so the plane calls this once per connection.
    /// </summary>
    public Task<RelayGrantResult> IssueForPreviewAsync(
        TeamId team, string serviceName, CancellationToken ct = default) =>
        MintAsync(team, serviceName, consumerTaskId: null, consumerInstanceId: null, ct);

    /// <summary>
    /// The shared check-11 gate + mint behind <see cref="IssueAsync"/> (§8.3) and
    /// <see cref="IssueForPreviewAsync"/> (§8.4). Everything is scoped to
    /// <paramref name="team"/> so another Team's services never leak (§8.2).
    /// </summary>
    private async Task<RelayGrantResult> MintAsync(
        TeamId team, string serviceName, Guid? consumerTaskId, Guid? consumerInstanceId,
        CancellationToken ct)
    {
        // Registered in this Team at all? Scoped to the Team, so a service that
        // exists only in another Team is indistinguishable from one that does not
        // exist — cross-Team reads as not-registered (§8.2).
        var registered = await db.RegisteredServices.AsNoTracking()
            .AnyAsync(s => s.TeamId == team.Value && s.Name == serviceName, ct);
        if (!registered)
            return new RelayGrantResult.Refused(Rule.ForwardsRequireRegistration,
                $"no service '{serviceName}' is registered in your Team");

        // Owned by a task that is still working? (§9 check 11.) A registered row
        // normally implies a working owner — ClearServicesAndForwards deletes them
        // on the way out — but check explicitly rather than trust the invariant.
        // Grab the producer task id AND the service's loopback port in one read:
        // both ride the Issued result so the plane can send the producer end its
        // dial target without re-querying (§8.3).
        var producer = await (
                from s in db.RegisteredServices.AsNoTracking()
                join t in db.Tasks.AsNoTracking() on s.TaskId equals t.Id
                where s.TeamId == team.Value
                      && s.Name == serviceName
                      && t.State == TaskState.Working
                select new { t.Id, s.Port })
            .FirstOrDefaultAsync(ct);
        if (producer is null)
            return new RelayGrantResult.Refused(Rule.ForwardsRequireRegistration,
                $"service '{serviceName}' is registered but its task is no longer working");

        var now = clock.GetUtcNow();
        var (grant, hash) = NewGrant();
        var forwardId = Guid.NewGuid();
        db.RelayGrants.Add(new RelayGrantRow
        {
            Id = Guid.NewGuid(),
            GrantHash = hash,
            ForwardId = forwardId,
            ConsumerTaskId = consumerTaskId,
            ConsumerInstanceId = consumerInstanceId,
            ServiceName = serviceName,
            ProducerTaskId = producer.Id,
            TeamId = team.Value,
            CreatedAt = now,
            ExpiresAt = now + GrantTtl,
        });
        await db.SaveChangesAsync(ct);
        return new RelayGrantResult.Issued(
            grant, forwardId, now + GrantTtl, new TaskId(producer.Id), producer.Port);
    }

    /// <summary>
    /// Validates a grant a tunnel presents (spec §8.3): true iff the hash matches
    /// a grant for this <paramref name="forwardId"/> that is unrevoked, unexpired,
    /// and whose slot for <paramref name="role"/> is still unused — and, as the
    /// same step, claims that role slot. Single-use per role: the consumer opens
    /// once and the producer opens once, so a replay of either side returns false
    /// and backs the relay's duplicate-role refusal with a real auth denial.
    ///
    /// <para>The check and the claim are one atomic conditional update — the WHERE
    /// encodes every condition and the affected-row count is the verdict — so two
    /// concurrent opens for the same role can never both succeed.</para>
    /// </summary>
    public async Task<bool> ValidateAsync(
        string grant, Guid forwardId, RelayGrantRole role, CancellationToken ct = default)
    {
        var hash = Hash(grant);
        var now = clock.GetUtcNow();
        var affected = role switch
        {
            RelayGrantRole.Consumer => await db.RelayGrants
                .Where(g => g.GrantHash == hash && g.ForwardId == forwardId
                            && !g.Revoked && g.ExpiresAt > now && g.UsedByConsumerAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.UsedByConsumerAt, now), ct),
            RelayGrantRole.Producer => await db.RelayGrants
                .Where(g => g.GrantHash == hash && g.ForwardId == forwardId
                            && !g.Revoked && g.ExpiresAt > now && g.UsedByProducerAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.UsedByProducerAt, now), ct),
            _ => 0,
        };
        return affected == 1;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static (string Grant, string Hash) NewGrant()
    {
        // 64 hex chars = 256 bits, URL-safe, same shape as TokenService's opaque
        // credentials — but its own class prefix so a grant is never mistaken for
        // a Principal-bearing token.
        var grant = $"dkt_g_{RandomNumberGenerator.GetHexString(64)}";
        return (grant, Hash(grant));
    }

    private static string Hash(string grant) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(grant)));
}
