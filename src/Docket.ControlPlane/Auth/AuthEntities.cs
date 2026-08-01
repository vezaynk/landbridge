namespace Docket.ControlPlane.Auth;

/// <summary>Credential classes as stored, spec §5.</summary>
public enum CredentialKind
{
    /// <summary>Human-issued, single-use, short-lived bootstrap (§5, §11).</summary>
    Enrollment,

    /// <summary>Machine access token — short, refreshed (§13: cheap eviction).</summary>
    MachineAccess,

    /// <summary>Machine refresh token — the only long-lived secret on a box, bound to its machine (§13).</summary>
    MachineRefresh,

    /// <summary>Worker token minted at dispatch, scoped to {team, task, instance} (§5).</summary>
    Worker,

    /// <summary>
    /// A human's own session (§5). Every credential descends from a human
    /// (§2 principle 5); this is the root. Obtained via OAuth 2.1 auth-code /
    /// device flow in production — see <see cref="TokenService.IssueHumanSessionAsync"/>
    /// for where that callback lands.
    /// </summary>
    Human,

    /// <summary>
    /// A human session's lead claim against one Team (§5). Lives until evicted
    /// or released; one per Team (§4, §9 check 6). Carries the claiming human's
    /// id so takeover can attribute an eviction.
    /// </summary>
    Lead,
}

/// <summary>
/// One opaque token. The token string itself is never stored — only its
/// SHA-256 — so the database cannot leak bearer credentials (§5: opaque, not
/// JWT; revocation is the priority, and revocation here is one row update).
/// </summary>
public sealed class CredentialRow
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = "";
    public CredentialKind Kind { get; set; }

    // Claims. Which are set depends on Kind.
    public Guid? MachineId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? WorkerInstanceId { get; set; }

    /// <summary>
    /// For a Lead credential: which human session holds the claim, so a takeover
    /// can name whom it evicted (§4). For a Human credential the human's own id
    /// is simply this row's <see cref="Id"/>.
    /// </summary>
    public Guid? HumanId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Enrollment tokens are single-use; set when exchanged (§5).</summary>
    public DateTimeOffset? UsedAt { get; set; }

    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Set when a Lead credential is revoked by takeover rather than voluntary
    /// release (§4). The revocation is the same, but eviction carries who did it
    /// and when — so the evicted session's next call fails with an explicit
    /// reason, never a bare 403.
    /// </summary>
    public Guid? EvictedByHuman { get; set; }
    public DateTimeOffset? EvictedAt { get; set; }
}

/// <summary>
/// The Team-lead audit log (§4: every takeover is a logged event; §12: lead
/// takeovers appear in the event log). Claims, releases, and takeovers each
/// write a row, so contention over a Team is legible afterward.
/// </summary>
public enum LeadEventKind
{
    Claimed,
    Released,
    TakenOver,
}

public sealed class LeadEventRow
{
    public long Seq { get; set; }
    public Guid TeamId { get; set; }
    public LeadEventKind Kind { get; set; }

    /// <summary>The human whose session the claim now belongs to (null for a plain release).</summary>
    public Guid? HumanId { get; set; }

    /// <summary>On a takeover, the evicted incumbent's human id.</summary>
    public Guid? PriorHumanId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// An enrolled machine (§11). Purpose/OS/specs/permission level are declared
/// at enrollment and bound server-side — a machine cannot re-declare its own
/// privileges (§13).
/// </summary>
public sealed class MachineRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Os { get; set; } = "";
    public string PermissionLevel { get; set; } = "";
    public DateTimeOffset EnrolledAt { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// What a validated token authenticates as. The worker principal carries the
/// engine's own actor type, so the MCP layer hands
/// <see cref="Docket.Core.WorkerCaller"/> straight to transitions — authority
/// stays structural end to end.
/// </summary>
public abstract record Principal
{
    public sealed record Worker(Docket.Core.WorkerCaller Caller) : Principal;

    public sealed record Machine(Guid MachineId) : Principal;

    /// <summary>A human's own session (§5). Carries its session id for lead-claim attribution.</summary>
    public sealed record Human(Guid HumanId) : Principal
    {
        public Docket.Core.HumanSession Actor { get; } = new();
    }

    /// <summary>
    /// A live lead claim (§5). Maps straight to the engine's
    /// <see cref="Docket.Core.LeadClaim"/> so authority stays structural end to end.
    /// </summary>
    public sealed record Lead(Docket.Core.TeamId Team) : Principal
    {
        public Docket.Core.LeadClaim Actor => new(Team);
    }

    /// <summary>
    /// A lead token that was evicted by takeover (§4). It authenticates only
    /// far enough to tell its holder why: the tool surface refuses the call
    /// with an explicit reason — evicted by whom, when — instead of a bare 403,
    /// which would leave an agent inventing explanations for a denial.
    /// </summary>
    public sealed record EvictedLead(Docket.Core.TeamId Team, Guid EvictedByHuman, DateTimeOffset EvictedAt) : Principal;
}

/// <summary>
/// Outcome of a lead claim (§4). One Lead per Team is a conditional claim: an
/// actively-held Team refuses a second claimant unless they take over, and the
/// refusal names the incumbent so a human can decide.
/// </summary>
public abstract record LeadClaimResult
{
    private LeadClaimResult() { }

    /// <summary>The claim (or takeover) succeeded; the lead token is minted once here.</summary>
    public sealed record Claimed(IssuedToken Token, Docket.Core.TeamId Team) : LeadClaimResult;

    /// <summary>The Team is actively led and takeover was not requested (§4).</summary>
    public sealed record Refused(Guid HeldByHuman, DateTimeOffset HeldSince) : LeadClaimResult;

    /// <summary>The presented token was not a live human session (§5: leads descend from humans).</summary>
    public sealed record NoHumanSession : LeadClaimResult;
}

/// <summary>A newly minted token. The plaintext exists only in this value, once.</summary>
public sealed record IssuedToken(string Token, Guid CredentialId, DateTimeOffset? ExpiresAt);

/// <summary>Access + refresh pair handed to docketd at enrollment (§5, §13).</summary>
public sealed record MachineCredentials(Guid MachineId, IssuedToken Access, IssuedToken Refresh);

/// <summary>Declared at enrollment, bound server-side (§11, §13).</summary>
public sealed record MachineDeclaration(string Name, string Purpose, string Os, string PermissionLevel);
