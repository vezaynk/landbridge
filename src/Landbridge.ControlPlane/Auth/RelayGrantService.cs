using System.Security.Cryptography;
using Landbridge.ControlPlane;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;


namespace Landbridge.ControlPlane.Auth;

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
/// own <c>lbr_g_</c> prefix.</para>
///
/// <para>Revocation is not this service's job — it rides the existing
/// <see cref="ClearServicesAndForwards"/> effect in <see cref="SessionStore"/>,
/// which revokes a task's live grants next to where it already clears that task's
/// registered services (§6). That effect fires on leaving <c>working</c> in every case
/// but one: a producer blocked on a <b>permission</b> request is still alive inside its
/// tool call and emits nothing (§11), so its grants remain live — through a later park or
/// requeue too. Expiry is what bounds a grant there.</para>
///
/// <para>Revoking bounds only the <em>next</em> open. Ending the splices already running is
/// the same effect's other arm — <c>close-forward</c> to both machines
/// (<see cref="Landbridge.ControlPlane.ForwardTeardownService"/>) — because §8.3 bounds an
/// established splice by its owning task's <c>working</c> state, and no row can enforce
/// that.</para>
///
/// <para>The mint is also where §9 check 10's <b>forward rate limit</b> is enforced, since a
/// grant is the one thing no forward can happen without and the plane is the only place the
/// limit holds without a live relay. Check 10's other half — a Team <em>byte</em> allowance —
/// is measured but never enforced: the relay counts bytes per forward and reports them, and
/// the plane attributes them per Team (<see cref="TeamForwardUsageService"/>, §9.10). Nothing
/// is checked against that total, because §8.3 forbids severing an established splice
/// mid-flight, which leaves what a reached ceiling should actually do unresolved.</para>
/// </summary>
public sealed class RelayGrantService(
    LandbridgeDbContext db, TimeProvider clock, int? forwardsPerWindow = null, TimeSpan? forwardWindow = null)
{
    /// <summary>
    /// A grant establishes a connection; it does not authorize an ongoing
    /// session (§8.3: an established splice persists past expiry). Short by
    /// design — long enough to cover the open handshake, no longer.
    /// </summary>
    public static readonly TimeSpan GrantTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The §9 check 10 forward rate limit: how many grants one Team may be issued per
    /// <see cref="DefaultForwardWindow"/>. Deliberately generous — an §8.4 preview mints a
    /// fresh grant per browser connection, so a page load is legitimately several — while
    /// still bounding a runaway loop, which is the containment this exists for.
    /// </summary>
    public const int DefaultForwardsPerWindow = 120;

    /// <summary>The window <see cref="DefaultForwardsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan DefaultForwardWindow = TimeSpan.FromMinutes(1);

    private readonly int _forwardsPerWindow = forwardsPerWindow ?? DefaultForwardsPerWindow;
    private readonly TimeSpan _forwardWindow = forwardWindow ?? DefaultForwardWindow;

    /// <summary>
    /// Issues a grant for <paramref name="consumer"/> to reach
    /// <paramref name="serviceName"/> (spec §8.3 step 2). Checks, all scoped to
    /// the consumer's Team so another Team's services never leak (§8.2):
    /// the service is registered in the consumer's Team, and its owning task is
    /// still <c>working</c>. On success mints an opaque <c>lbr_g_</c> grant
    /// (hashed at rest), a fresh forward id, and a short expiry.
    /// </summary>
    public Task<RelayGrantResult> IssueAsync(
        WorkerCaller consumer, string serviceName, CancellationToken ct = default) =>
        // The §8.3 consumer is a worker task; bind it for attribution.
        MintAsync(consumer.Team, serviceName, producer: null,
            consumer.Session.Value, consumer.Instance.Value, ct);

    /// <summary>
    /// Issues a grant for an §8.4 HTTP preview: the consumer is the preview
    /// frontend, not a task, so there is no consumer worker instance to bind — the
    /// mint records only the producer + Team. Runs the identical check-11 gate as
    /// <see cref="IssueAsync"/> (registered service owned by a working task in
    /// <paramref name="team"/>), and the resulting grant validates and revokes
    /// through the same paths (validation ignores the consumer binding; revocation
    /// keys on <c>ProducerSessionId</c> via <see cref="ClearServicesAndForwards"/>).
    /// The producer machine dials on demand — a fresh grant + forward id per
    /// browser connection (§8.4) — so the plane calls this once per connection.
    ///
    /// <para><b><paramref name="producer"/> is the authority, not a hint.</b> A preview URL
    /// is minted against one task's service (<c>PreviewMappingRow.SessionId</c>), and this
    /// resolves that exact registration rather than whatever now answers to
    /// <paramref name="serviceName"/> in the Team. Without it the mapping's task was written
    /// and never read, so a label minted for task A's <c>web</c> could splice a browser to
    /// task B's <c>web</c> — a URL reaching a service its holder never exposed. Distinct
    /// from <see cref="IssueAsync"/> deliberately: <c>open_forward</c> resolves by name
    /// because a name is exactly what a worker asks with, while a preview carries a durable
    /// record of what it was minted for and is held to it.</para>
    /// </summary>
    public Task<RelayGrantResult> IssueForPreviewAsync(
        TeamId team, SessionId producer, string serviceName, CancellationToken ct = default) =>
        MintAsync(team, serviceName, producer.Value, consumerSessionId: null, consumerInstanceId: null, ct);

    /// <summary>
    /// Issues a grant for the §8.3 <b>human</b> path: the consumer is the Lead's own
    /// bound machine's <c>landbridged</c>, not a task, so — exactly as for a preview —
    /// there is no consumer worker instance to bind and the mint records only the
    /// producer + Team. Runs the identical check-11 gate as <see cref="IssueAsync"/>
    /// (registered service owned by a working task in <paramref name="team"/>, which
    /// for a Lead is a Team it owns), and the grant validates, is single-use per role,
    /// and revokes through the same paths. The Lead's authority is what selects
    /// <paramref name="team"/>; nothing here is broader than what a worker in that
    /// Team could already reach.
    /// </summary>
    public Task<RelayGrantResult> IssueForLeadAsync(
        TeamId team, string serviceName, CancellationToken ct = default) =>
        MintAsync(team, serviceName, producer: null, consumerSessionId: null, consumerInstanceId: null, ct);

    /// <summary>
    /// The shared check-11 gate + mint behind <see cref="IssueAsync"/> (§8.3) and
    /// <see cref="IssueForPreviewAsync"/> (§8.4). Everything is scoped to
    /// <paramref name="team"/> so another Team's services never leak (§8.2).
    /// </summary>
    /// <param name="producer">
    /// When set, the <em>only</em> task whose registration may satisfy this mint — §8.4's
    /// preview, which was minted against one task's service and must not resolve another's.
    /// Null for the two by-name surfaces (<c>open_forward</c> and the §8.3 human path), where
    /// the Team-scoped name is the whole of what the caller asked for.
    /// </param>
    private async Task<RelayGrantResult> MintAsync(
        TeamId team, string serviceName, Guid? producer,
        Guid? consumerSessionId, Guid? consumerInstanceId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // §9 check 10, the forward rate limit — enforced HERE, at mint, and not at the relay.
        //
        // The plane is the only place it holds unconditionally: a grant is the one thing a
        // forward cannot happen without, and refusing to mint costs nothing and needs no
        // cooperation from a relay that may be unreachable, overloaded, or (in a fleet) not
        // the one this forward will use. Enforcing at the relay instead would put the control
        // downstream of the thing it is meant to bound.
        //
        // Counted per Team over a rolling window, on MINTS rather than open tunnels — the same
        // choice as the §9.9 ceiling and for the same reason: what Landbridge authorized is
        // knowable, what a peer then did with it is not. A grant minted and never used still
        // spent authorization. The check runs before the service lookup so a Team in a mint
        // loop is cut off at the cheapest possible point.
        var since = now - _forwardWindow;
        var recent = await db.RelayGrants.AsNoTracking()
            .CountAsync(g => g.TeamId == team.Value && g.CreatedAt > since, ct);
        if (recent >= _forwardsPerWindow)
            return new RelayGrantResult.Refused(
                Rule.TeamByteAllowance,
                $"your Team has opened {recent} forwards in the last {_forwardWindow.TotalSeconds:0}s, " +
                $"which is its limit ({_forwardsPerWindow}); wait for the window to roll");

        // Registered in this Team at all? Scoped to the Team, so a service that
        // exists only in another Team is indistinguishable from one that does not
        // exist — cross-Team reads as not-registered (§8.2). A preview additionally
        // narrows to the task its mapping named, so a stale label reads as
        // not-registered rather than resolving whatever holds the name now.
        var registered = await db.RegisteredServices.AsNoTracking()
            .AnyAsync(s => s.TeamId == team.Value && s.Name == serviceName
                           && (producer == null || s.SessionId == producer), ct);
        if (!registered)
            return new RelayGrantResult.Refused(Rule.ForwardsRequireRegistration,
                $"no service '{serviceName}' is registered in your Team");

        // Owned by a live producer? (§9 check 11.) Working, or blocked on a
        // permission request: that worker is still inside its tool call, still
        // the incumbent, and ClearServicesAndForwards does not fire (§11). A
        // registered row for a submitted/parked/failed task is the defensive
        // case this check exists for — the store refuses to register there, so
        // we only see one if a test (or a future bug) wrote it directly.
        // Grab the producer task id AND the service's loopback port in one read:
        // both ride the Issued result so the plane can send the producer end its
        // dial target without re-querying (§8.3).
        //
        // ORDERED, and that is not cosmetic. (TeamId, Name) is unique now (§8.2,
        // Rule.ServiceNameUniqueInTeam), so this reads at most one row — but an unordered
        // FirstOrDefault is what turned a duplicate name into a raffle for whichever row
        // Postgres happened to return, and re-adding a second row is a schema change away.
        // Oldest-first, so if one ever exists again this resolves the registration that held
        // the name first rather than a different port per call.
        var holder = await (
                from s in db.RegisteredServices.AsNoTracking()
                join t in db.Sessions.AsNoTracking() on s.SessionId equals t.Id
                where s.TeamId == team.Value
                      && s.Name == serviceName
                      && (producer == null || s.SessionId == producer)
                      && (t.State == SessionState.Working
                          || t.State == SessionState.BlockedOnInput)
                orderby s.Seq
                select new { t.Id, s.Port })
            .FirstOrDefaultAsync(ct);
        if (holder is null)
            return new RelayGrantResult.Refused(Rule.ForwardsRequireRegistration,
                $"service '{serviceName}' is registered but its task is no longer working");

        var (grant, hash) = NewGrant();
        var forwardId = Guid.NewGuid();
        db.RelayGrants.Add(new RelayGrantRow
        {
            Id = Guid.NewGuid(),
            GrantHash = hash,
            ForwardId = forwardId,
            ConsumerSessionId = consumerSessionId,
            ConsumerInstanceId = consumerInstanceId,
            ServiceName = serviceName,
            ProducerSessionId = holder.Id,
            TeamId = team.Value,
            CreatedAt = now,
            ExpiresAt = now + GrantTtl,
        });
        HubOutbox.Stage(db, clock, HubQueueRow.ForwardsTopic, forwardId);
        await db.SaveChangesAsync(ct);
        await HubOutbox.NotifyAsync(db, holder.Id, ct);
        return new RelayGrantResult.Issued(
            grant, forwardId, now + GrantTtl, new SessionId(holder.Id), holder.Port);
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

    /// <summary>
    /// Stamp the consumer loopback after <c>forward-opened</c>. The fleet board's
    /// Receiving list reads these two columns.
    /// </summary>
    public Task RecordConsumerBindAsync(
        Guid forwardId, string machineId, int port, CancellationToken ct = default) =>
        db.RelayGrants.Where(g => g.ForwardId == forwardId && !g.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.ConsumerMachine, machineId)
                .SetProperty(g => g.ConsumerPort, port), ct);

    /// <summary>The splice ended; the loopback is no longer a live receiving port.</summary>
    public Task ClearConsumerBindAsync(Guid forwardId, CancellationToken ct = default) =>
        db.RelayGrants.Where(g => g.ForwardId == forwardId)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.ConsumerPort, (int?)null), ct);

    public async Task<(SessionId Producer, SessionId? Consumer, string ServiceName)?> CloseConsumerAsync(
        Guid forwardId, CancellationToken ct = default)
    {
        var row = await db.RelayGrants.FirstOrDefaultAsync(g => g.ForwardId == forwardId && !g.Revoked, ct);
        if (row is null)
            return null;
        row.Revoked = true;
        row.ConsumerPort = null;
        await db.SaveChangesAsync(ct);
        return (
            new SessionId(row.ProducerSessionId),
            row.ConsumerSessionId is { } c ? new SessionId(c) : null,
            row.ServiceName);
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static (string Grant, string Hash) NewGrant()
    {
        // 64 hex chars = 256 bits, URL-safe, same shape as TokenService's opaque
        // credentials — but its own class prefix so a grant is never mistaken for
        // a Principal-bearing token.
        var grant = $"lbr_g_{RandomNumberGenerator.GetHexString(64)}";
        return (grant, Hash(grant));
    }

    private static string Hash(string grant) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(grant)));
}
