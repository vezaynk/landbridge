using System.Security.Cryptography;
using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane.Auth;

/// <summary>
/// Mints, validates, and revokes the opaque tokens of spec §5. Tokens are
/// random bytes with a class prefix; only their SHA-256 lands in the store.
///
/// The invariant (§9 check 13): token exchange is strictly narrowing, and the
/// only exchange in the system is enrollment → machine credentials. There is
/// no path from a worker credential to anything.
///
/// Worker tokens carry no lifetime of their own: they are valid exactly while
/// their worker-instance row is unrevoked — the store's
/// RevokeWorkerInstanceToken effect (requeue, park, verdict) is what kills
/// them, which wires §9 check 14 into authentication with no second
/// bookkeeping path.
/// </summary>
public sealed class TokenService(DocketDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan EnrollmentTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MachineAccessTtl = TimeSpan.FromHours(1);
    public static readonly TimeSpan MachineRefreshTtl = TimeSpan.FromDays(90);
    public static readonly TimeSpan HumanSessionTtl = TimeSpan.FromHours(12);

    // ── Issuance ────────────────────────────────────────────────────────────

    /// <summary>
    /// Mints a human session token (§5) — the root every other credential
    /// descends from (§2 principle 5).
    ///
    /// This is the seam the OAuth 2.1 flow plugs into, and it stays deliberately
    /// free of wire protocol: the control plane is the OAuth authorization server
    /// (§5), but the authorization-code exchange itself — PKCE, the redirect dance,
    /// CIMD — lives in <c>Docket.Mcp.OAuthEndpoints</c>, which calls exactly this
    /// method to turn a verified human into a session token. The token is identical
    /// either way, which is what keeps the human/Lead path testable headlessly: the
    /// tests mint through here directly. No device flow is built.
    /// </summary>
    public async Task<IssuedToken> IssueHumanSessionAsync(CancellationToken ct = default)
    {
        var (token, row) = NewCredential(CredentialKind.Human, HumanSessionTtl);
        db.Set<CredentialRow>().Add(row);
        await db.SaveChangesAsync(ct);
        return new IssuedToken(token, row.Id, row.ExpiresAt);
    }

    /// <summary>
    /// Claims the Lead of a Team for a human session (§4, §5). One Lead per Team
    /// is enforced as a conditional claim (§9 check 6): a leadless Team is
    /// claimed silently; an actively-led Team refuses a second claimant unless
    /// <paramref name="takeover"/> is set, in which case the incumbent is
    /// evicted — its token revoked with attribution so its next call learns why
    /// (§4) — and the takeover is written to the lead event log.
    ///
    /// The incumbent read is only advisory: the invariant itself is the
    /// database's partial unique index (one unrevoked Lead row per Team), so
    /// two concurrent claimants cannot both win — the loser's insert fails and
    /// is returned as the same <see cref="LeadClaimResult.Refused"/> a
    /// sequential second claimant would get.
    /// </summary>
    public async Task<LeadClaimResult> ClaimLeadAsync(
        string humanToken, TeamId team, bool takeover = false, CancellationToken ct = default)
    {
        if (await ValidateAsync(humanToken, ct) is not Principal.Human human)
            return new LeadClaimResult.NoHumanSession();

        var incumbent = await db.Set<CredentialRow>()
            .FirstOrDefaultAsync(c => c.Kind == CredentialKind.Lead && c.TeamId == team.Value && !c.Revoked, ct);

        if (incumbent is not null)
        {
            if (!takeover)
                return new LeadClaimResult.Refused(incumbent.HumanId ?? Guid.Empty, incumbent.CreatedAt);

            // Takeover evicts the incumbent (§4): a revoke that carries who and
            // when, distinct from a voluntary release, so ValidateAsync can hand
            // the evicted holder an explicit reason rather than a null.
            var now = clock.GetUtcNow();
            incumbent.Revoked = true;
            incumbent.RevokedAt = now;
            incumbent.EvictedByHuman = human.HumanId;
            incumbent.EvictedAt = now;
            db.Set<LeadEventRow>().Add(new LeadEventRow
            {
                TeamId = team.Value,
                Kind = LeadEventKind.TakenOver,
                HumanId = human.HumanId,
                PriorHumanId = incumbent.HumanId,
                OccurredAt = now,
            });
        }
        else
        {
            db.Set<LeadEventRow>().Add(new LeadEventRow
            {
                TeamId = team.Value,
                Kind = LeadEventKind.Claimed,
                HumanId = human.HumanId,
                OccurredAt = clock.GetUtcNow(),
            });
        }

        var (token, row) = NewCredential(CredentialKind.Lead, ttl: null, teamId: team.Value, humanId: human.HumanId);
        db.Set<CredentialRow>().Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
                                           && pg.ConstraintName == "ix_credentials_one_live_lead_per_team")
        {
            // A concurrent claimant won the race between our incumbent read and
            // our insert. The unique index made the loss safe; report it exactly
            // like an ordinary refusal, naming the winner (best-effort — the
            // winner could release in the same instant).
            db.ChangeTracker.Clear();
            var winner = await db.Set<CredentialRow>().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Kind == CredentialKind.Lead && c.TeamId == team.Value && !c.Revoked, ct);
            return new LeadClaimResult.Refused(winner?.HumanId ?? Guid.Empty, winner?.CreatedAt ?? clock.GetUtcNow());
        }
        return new LeadClaimResult.Claimed(new IssuedToken(token, row.Id, null), team);
    }

    /// <summary>
    /// Voluntary release of a lead claim (§4): the Team becomes leadless and
    /// claimable, and the release is logged. A released token stops
    /// authenticating cleanly — release, unlike eviction, carries no surprise,
    /// so no explicit reason is owed. Returns false if the token was not a live
    /// lead claim.
    /// </summary>
    public async Task<bool> ReleaseLeadAsync(string leadToken, CancellationToken ct = default)
    {
        var row = await FindLive(leadToken, ct, tracking: true);
        if (row is null || row.Kind != CredentialKind.Lead)
            return false;

        var now = clock.GetUtcNow();
        row.Revoked = true;
        row.RevokedAt = now;
        db.Set<LeadEventRow>().Add(new LeadEventRow
        {
            TeamId = row.TeamId!.Value,
            Kind = LeadEventKind.Released,
            HumanId = row.HumanId,
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Human-issued bootstrap token: single-use, short TTL (§5, §11).</summary>
    public async Task<IssuedToken> IssueEnrollmentTokenAsync(CancellationToken ct = default)
    {
        var (token, row) = NewCredential(CredentialKind.Enrollment, EnrollmentTtl);
        db.Set<CredentialRow>().Add(row);
        await db.SaveChangesAsync(ct);
        return new IssuedToken(token, row.Id, row.ExpiresAt);
    }

    /// <summary>
    /// The one exchange in the system (§9 check 13): a live, unused enrollment
    /// token becomes a machine identity plus its access/refresh pair. Any
    /// other credential class presented here is refused.
    ///
    /// <para><b>Single-use is a conditional update, not a read-then-write.</b> The
    /// read below only decides whether to try; the gate is the <c>WHERE</c> on the
    /// burn, which re-asserts unused/unrevoked/unexpired and therefore takes the
    /// row lock that serializes two concurrent exchanges. Deciding on the read and
    /// then writing <c>UsedAt</c> would let both callers pass the check and both
    /// mint — one enrollment token, two machine identities, which is the whole
    /// blast radius §11 hands a single short-lived bootstrap secret. Same gate, and
    /// the same reasoning, as <see cref="OAuthAuthorizationCodeService.ConsumeAsync"/>.</para>
    ///
    /// <para>The burn and the mint share one transaction, so a failure between them
    /// cannot spend the token without issuing anything (which would strand an
    /// enrolling machine with no way to retry) or issue credentials against a token
    /// still open for a second exchange.</para>
    /// </summary>
    public async Task<MachineCredentials?> ExchangeEnrollmentAsync(
        string enrollmentToken, MachineDeclaration declaration, CancellationToken ct = default)
    {
        // No-tracking: the burn below is an ExecuteUpdate, which bypasses the change
        // tracker, so nothing here relies on this entity being persisted.
        var row = await FindLive(enrollmentToken, ct);
        if (row is null || row.Kind != CredentialKind.Enrollment || row.UsedAt is not null)
            return null;

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var burned = await db.Set<CredentialRow>()
            .Where(c => c.Id == row.Id && c.UsedAt == null && !c.Revoked
                        && (c.ExpiresAt == null || c.ExpiresAt > now))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        if (burned != 1)
            // A concurrent exchange (or a revoke, or expiry) got there between the
            // read and here. Refused exactly like a sequential second exchange —
            // the caller cannot tell the race from the replay, and neither can it
            // matter to them.
            return null;

        var machine = new MachineRow
        {
            Id = Guid.NewGuid(),
            Name = declaration.Name,
            Purpose = declaration.Purpose,
            Os = declaration.Os,
            PermissionLevel = declaration.PermissionLevel,
            EnrolledAt = now,
        };
        db.Set<MachineRow>().Add(machine);

        var (access, accessRow) = NewCredential(CredentialKind.MachineAccess, MachineAccessTtl, machineId: machine.Id);
        var (refresh, refreshRow) = NewCredential(CredentialKind.MachineRefresh, MachineRefreshTtl, machineId: machine.Id);
        db.Set<CredentialRow>().AddRange(accessRow, refreshRow);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new MachineCredentials(
            machine.Id,
            new IssuedToken(access, accessRow.Id, accessRow.ExpiresAt),
            new IssuedToken(refresh, refreshRow.Id, refreshRow.ExpiresAt));
    }

    /// <summary>
    /// Mints a fresh access token from a live refresh token. The refresh is bound to
    /// its machine (§13) in the only sense the store can express: the row names which
    /// machine it mints for, so a revoked machine's refresh mints nothing.
    ///
    /// <para><b>That binding is not a host binding.</b> The presented token is the
    /// whole credential — nothing here can tell the enrolled box from a copy of its
    /// <c>credentials</c> file on another host, and neither can <c>/runner</c>. A
    /// leaked refresh token is a working machine identity anywhere until the machine
    /// is revoked, which is why revocation has to be complete and immediate
    /// (<see cref="MachineRevocationService"/>). Binding the refresh to a
    /// host-derived secret that never lands in the credentials file is the real
    /// answer and is not built (§13 as-built).</para>
    /// </summary>
    public async Task<IssuedToken?> RefreshMachineAccessAsync(string refreshToken, CancellationToken ct = default)
    {
        var row = await FindLive(refreshToken, ct);
        if (row is null || row.Kind != CredentialKind.MachineRefresh)
            return null;
        if (!await MachineIsLive(row.MachineId!.Value, ct))
            return null;

        var (access, accessRow) = NewCredential(CredentialKind.MachineAccess, MachineAccessTtl, machineId: row.MachineId);
        db.Set<CredentialRow>().Add(accessRow);
        await db.SaveChangesAsync(ct);
        return new IssuedToken(access, accessRow.Id, accessRow.ExpiresAt);
    }

    /// <summary>
    /// Minted at dispatch for one worker instance (§5). No expiry of its own:
    /// validity tracks the instance row.
    /// </summary>
    public async Task<IssuedToken> MintWorkerTokenAsync(
        TeamId team, TaskId task, WorkerInstanceId instance, CancellationToken ct = default)
    {
        var (token, row) = NewCredential(CredentialKind.Worker, ttl: null,
            teamId: team.Value, taskId: task.Value, instanceId: instance.Value);
        db.Set<CredentialRow>().Add(row);
        await db.SaveChangesAsync(ct);
        return new IssuedToken(token, row.Id, null);
    }

    // ── Validation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a presented token to a principal, or null. Class-specific
    /// liveness applies: machine tokens require a live machine; worker tokens
    /// require an unrevoked worker-instance row (§9 check 14).
    /// </summary>
    public async Task<Principal?> ValidateAsync(string token, CancellationToken ct = default)
    {
        // One lookup serves both the live path and the evicted-lead reason: the
        // row is fetched dead-or-alive, so an ordinary invalid/expired token
        // costs a single query.
        var row = await Find(token, ct);
        if (row is null)
            return null;
        if (!IsLive(row))
            // A dead token might still be an evicted lead that is owed an
            // explicit reason — evicted by whom, when (§4). A voluntarily
            // released lead (revoked, no eviction attribution) and every other
            // dead token stay a clean null.
            return row is { Kind: CredentialKind.Lead, EvictedByHuman: { } by, EvictedAt: { } at }
                ? new Principal.EvictedLead(new TeamId(row.TeamId!.Value), by, at)
                : null;

        switch (row.Kind)
        {
            case CredentialKind.MachineAccess:
                return await MachineIsLive(row.MachineId!.Value, ct)
                    ? new Principal.Machine(row.MachineId.Value)
                    : null;

            case CredentialKind.Worker:
                var incumbent = await db.WorkerInstances.AsNoTracking()
                    .AnyAsync(w => w.Id == row.WorkerInstanceId!.Value && !w.Revoked, ct);
                return incumbent
                    ? new Principal.Worker(new WorkerCaller(
                        new TeamId(row.TeamId!.Value),
                        new TaskId(row.TaskId!.Value),
                        new WorkerInstanceId(row.WorkerInstanceId!.Value)))
                    : null;

            case CredentialKind.Human:
                return new Principal.Human(row.Id);

            // A live lead credential; eviction/release already excluded it via
            // Revoked in FindLive. The claiming human rides along (§8.3 human path:
            // a lead↔machine binding keys on the person, not the session).
            case CredentialKind.Lead:
                return new Principal.Lead(new TeamId(row.TeamId!.Value), row.HumanId);

            // Enrollment and refresh tokens authenticate nothing by themselves:
            // they exist only to be exchanged/refreshed.
            default:
                return null;
        }
    }

    // ── Revocation (§5: un-trusting a machine must take seconds) ───────────

    /// <summary>
    /// Kills a machine's row and every credential minted <em>for</em> that machine —
    /// its access and refresh tokens — so nothing it holds authenticates again and no
    /// further access can be refreshed.
    ///
    /// <para><b>This is one half of un-trusting a machine, not the whole of it.</b>
    /// The predicate is <c>CredentialRow.MachineId</c>, and a worker token carries no
    /// machine id (it is minted per {team, task, instance}), so this reaches no worker
    /// running on the box; nor does it close the <c>/runner</c> socket, which
    /// authenticated once at upgrade and is never re-checked. A machine revoked
    /// through this method alone keeps its command channel and its workers' tokens.
    /// Callers want <see cref="MachineRevocationService.RevokeAsync"/>, which composes
    /// this with both; this stays public only because credential revocation is
    /// separately meaningful to tests and to the store layer.</para>
    /// </summary>
    public async Task RevokeMachineCredentialsAsync(Guid machineId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.Set<MachineRow>().Where(m => m.Id == machineId && !m.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Revoked, true)
                .SetProperty(m => m.RevokedAt, now), ct);
        await db.Set<CredentialRow>().Where(c => c.MachineId == machineId && !c.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Revoked, true)
                .SetProperty(c => c.RevokedAt, now), ct);
    }

    public async Task RevokeCredentialAsync(Guid credentialId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await db.Set<CredentialRow>().Where(c => c.Id == credentialId && !c.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Revoked, true)
                .SetProperty(c => c.RevokedAt, now), ct);
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private (string Token, CredentialRow Row) NewCredential(
        CredentialKind kind, TimeSpan? ttl,
        Guid? machineId = null, Guid? teamId = null, Guid? taskId = null, Guid? instanceId = null,
        Guid? humanId = null)
    {
        // 64 hex chars = 256 bits of entropy, URL-safe with no munging.
        var token = $"dkt_{Prefix(kind)}_{RandomNumberGenerator.GetHexString(64)}";
        var now = clock.GetUtcNow();
        return (token, new CredentialRow
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(token),
            Kind = kind,
            MachineId = machineId,
            TeamId = teamId,
            TaskId = taskId,
            WorkerInstanceId = instanceId,
            HumanId = humanId,
            CreatedAt = now,
            ExpiresAt = ttl is { } t ? now + t : null,
        });
    }

    /// <summary>The credential row for a presented token, dead or alive.</summary>
    private async Task<CredentialRow?> Find(string token, CancellationToken ct, bool tracking = false)
    {
        var hash = Hash(token);
        // Validation reads no-tracking: revocation is applied via ExecuteUpdate
        // (which bypasses the change tracker), so a tracked read could return a
        // stale, still-live entity. Only mutate-then-save callers track.
        var query = tracking ? db.Set<CredentialRow>() : db.Set<CredentialRow>().AsNoTracking();
        return await query.FirstOrDefaultAsync(c => c.TokenHash == hash, ct);
    }

    private async Task<CredentialRow?> FindLive(string token, CancellationToken ct, bool tracking = false)
    {
        var row = await Find(token, ct, tracking);
        return row is not null && IsLive(row) ? row : null;
    }

    private bool IsLive(CredentialRow row) =>
        !row.Revoked && !(row.ExpiresAt is { } expiry && expiry <= clock.GetUtcNow());

    private async Task<bool> MachineIsLive(Guid machineId, CancellationToken ct) =>
        await db.Set<MachineRow>().AsNoTracking().AnyAsync(m => m.Id == machineId && !m.Revoked, ct);

    private static string Prefix(CredentialKind kind) => kind switch
    {
        CredentialKind.Enrollment => "e",
        CredentialKind.MachineAccess => "m",
        CredentialKind.MachineRefresh => "r",
        CredentialKind.Worker => "w",
        CredentialKind.Human => "h",
        CredentialKind.Lead => "l",
        _ => "x",
    };

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
