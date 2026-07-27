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

    /// <summary>Human-provisioned verifier client credential (§5).</summary>
    Verifier,
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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Enrollment tokens are single-use; set when exchanged (§5).</summary>
    public DateTimeOffset? UsedAt { get; set; }

    public bool Revoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
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

    public sealed record Verifier : Principal
    {
        public Docket.Core.VerifierCredential Actor { get; } = new();
    }
}

/// <summary>A newly minted token. The plaintext exists only in this value, once.</summary>
public sealed record IssuedToken(string Token, Guid CredentialId, DateTimeOffset? ExpiresAt);

/// <summary>Access + refresh pair handed to docketd at enrollment (§5, §13).</summary>
public sealed record MachineCredentials(Guid MachineId, IssuedToken Access, IssuedToken Refresh);

/// <summary>Declared at enrollment, bound server-side (§11, §13).</summary>
public sealed record MachineDeclaration(string Name, string Purpose, string Os, string PermissionLevel);
