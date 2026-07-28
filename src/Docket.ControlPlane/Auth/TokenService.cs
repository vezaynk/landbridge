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

    // ── Issuance ────────────────────────────────────────────────────────────

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
    /// </summary>
    public async Task<MachineCredentials?> ExchangeEnrollmentAsync(
        string enrollmentToken, MachineDeclaration declaration, CancellationToken ct = default)
    {
        // Tracked: we mutate UsedAt and rely on SaveChanges to persist it.
        var row = await FindLive(enrollmentToken, ct, tracking: true);
        if (row is null || row.Kind != CredentialKind.Enrollment || row.UsedAt is not null)
            return null;

        row.UsedAt = clock.GetUtcNow();

        var machine = new MachineRow
        {
            Id = Guid.NewGuid(),
            Name = declaration.Name,
            Purpose = declaration.Purpose,
            Os = declaration.Os,
            PermissionLevel = declaration.PermissionLevel,
            EnrolledAt = clock.GetUtcNow(),
        };
        db.Set<MachineRow>().Add(machine);

        var (access, accessRow) = NewCredential(CredentialKind.MachineAccess, MachineAccessTtl, machineId: machine.Id);
        var (refresh, refreshRow) = NewCredential(CredentialKind.MachineRefresh, MachineRefreshTtl, machineId: machine.Id);
        db.Set<CredentialRow>().AddRange(accessRow, refreshRow);
        await db.SaveChangesAsync(ct);

        return new MachineCredentials(
            machine.Id,
            new IssuedToken(access, accessRow.Id, accessRow.ExpiresAt),
            new IssuedToken(refresh, refreshRow.Id, refreshRow.ExpiresAt));
    }

    /// <summary>
    /// Mints a fresh access token from a live refresh token. The refresh is
    /// bound to its machine (§13): a revoked machine's refresh mints nothing.
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

    /// <summary>Human-provisioned verifier client credential (§5): long-lived, revocable.</summary>
    public async Task<IssuedToken> ProvisionVerifierAsync(CancellationToken ct = default)
    {
        var (token, row) = NewCredential(CredentialKind.Verifier, ttl: null);
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
        var row = await FindLive(token, ct);
        if (row is null)
            return null;

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

            case CredentialKind.Verifier:
                return new Principal.Verifier();

            // Enrollment and refresh tokens authenticate nothing by themselves:
            // they exist only to be exchanged/refreshed.
            default:
                return null;
        }
    }

    // ── Revocation (§5: un-trusting a machine must take seconds) ───────────

    public async Task RevokeMachineAsync(Guid machineId, CancellationToken ct = default)
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
        Guid? machineId = null, Guid? teamId = null, Guid? taskId = null, Guid? instanceId = null)
    {
        var token = $"dkt_{Prefix(kind)}_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
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
            CreatedAt = now,
            ExpiresAt = ttl is { } t ? now + t : null,
        });
    }

    private async Task<CredentialRow?> FindLive(string token, CancellationToken ct, bool tracking = false)
    {
        var hash = Hash(token);
        // Validation reads no-tracking: revocation is applied via ExecuteUpdate
        // (which bypasses the change tracker), so a tracked read could return a
        // stale, still-live entity. Only mutate-then-save callers track.
        var query = tracking ? db.Set<CredentialRow>() : db.Set<CredentialRow>().AsNoTracking();
        var row = await query.FirstOrDefaultAsync(c => c.TokenHash == hash, ct);
        if (row is null || row.Revoked)
            return null;
        if (row.ExpiresAt is { } expiry && expiry <= clock.GetUtcNow())
            return null;
        return row;
    }

    private async Task<bool> MachineIsLive(Guid machineId, CancellationToken ct) =>
        await db.Set<MachineRow>().AsNoTracking().AnyAsync(m => m.Id == machineId && !m.Revoked, ct);

    private static string Prefix(CredentialKind kind) => kind switch
    {
        CredentialKind.Enrollment => "e",
        CredentialKind.MachineAccess => "m",
        CredentialKind.MachineRefresh => "r",
        CredentialKind.Worker => "w",
        CredentialKind.Verifier => "v",
        _ => "x",
    };

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
