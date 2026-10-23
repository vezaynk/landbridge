namespace Landbridge.ControlPlane.Auth;

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
    /// device flow in production — see <see cref="TokenService.IssueHumanSessionAsync(string?, System.Threading.CancellationToken)"/>
    /// for where that callback lands.
    /// </summary>
    Human,

    /// <summary>
    /// A human session's Lead factory credential (§5). Authorizes
    /// <c>create_team</c> and addressing any existing Team by its id.
    /// <see cref="LeadTeamRow"/> records which Teams this factory created,
    /// for lists. Lives until released. <see cref="CredentialRow.HumanId"/>
    /// is the claiming human, which a machine binding keys on.
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
    public Guid? SessionId { get; set; }
    public Guid? WorkerInstanceId { get; set; }

    /// <summary>
    /// For a Lead credential: the human session it descends from. A machine
    /// binding keys on this (§8.3). For a Human credential the human's own id
    /// is simply this row's <see cref="Id"/>.
    /// </summary>
    public Guid? HumanId { get; set; }

    /// <summary>
    /// RFC 8707: the resource this credential was minted <em>for</em> — the audience a
    /// bearer of it may present it to. Set on a human session that came through the OAuth
    /// flow, where the client named a resource and the authorization server checked it.
    ///
    /// <para>Null means the credential is not audience-bound, and that is the common
    /// case, not an omission: machine, worker and Lead credentials are minted inside the
    /// plane rather than requested by a client, and a human session predating this column
    /// has no recorded audience either. A null is accepted by any resource server —
    /// tightening that would revoke every credential in flight, and the mint-time check
    /// already holds for a deployment with one resource id.</para>
    /// </summary>
    public string? Resource { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Enrollment tokens are single-use; set when exchanged (§5).</summary>
    public DateTimeOffset? UsedAt { get; set; }

    public bool Revoked { get; set; }

    /// <summary>When the credential was revoked. Written but read by nothing — every
    /// authorization predicate reads <see cref="Revoked"/>. Kept for §13 forensics.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Set on a Lead credential an older build revoked while moving a Team.
    /// Nothing new writes these. A row that still has them fails its next
    /// call with who and when, rather than a bare 403.
    /// </summary>
    public Guid? EvictedByHuman { get; set; }
    public DateTimeOffset? EvictedAt { get; set; }
}

/// <summary>
/// Which factory's list includes a Team. The id (guid or slug) is how any
/// live Lead addresses the Team. One row per Team, so a second factory token
/// is refused while the first is live. Lists use this row. There is no MCP
/// list of Teams — an agent either minted the id or a human supplied it.
/// </summary>
public sealed class LeadTeamRow
{
    public Guid TeamId { get; set; }
    public Guid LeadCredentialId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Allocated dashboard alias (<c>adjective-noun-NNNN</c>). The Team Guid stays
    /// the capability and PK; humans and dashboard routes use this. Unique, minted
    /// at insert.
    /// </summary>
    public string Slug { get; set; } = "";
}

/// <summary>
/// The Team-lead audit log (§4, §12). Claims and releases each write a row.
/// <see cref="LeadEventKind.TakenOver"/> remains so older rows still read.
/// </summary>
public enum LeadEventKind
{
    Claimed,
    Released,
    /// <summary>Historical. New claims write <see cref="Claimed"/> or <see cref="Released"/>.</summary>
    TakenOver,
}

public sealed class LeadEventRow
{
    public long Seq { get; set; }
    public Guid TeamId { get; set; }
    public LeadEventKind Kind { get; set; }

    /// <summary>The human whose session the claim now belongs to (null for a plain release).</summary>
    public Guid? HumanId { get; set; }

    /// <summary>On a historical <see cref="LeadEventKind.TakenOver"/> row, the previous human id.</summary>
    public Guid? PriorHumanId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// A human's claim on one enrolled machine as <em>their own</em> box (§8.3 human
/// path): the fact that lets the control plane route a Lead-consumer forward to
/// where the person is actually sitting. A Lead is a harness client with no
/// machine of its own (§4) — enrollment and attachment are independent choices —
/// so the binding is the explicit, revocable, visible statement that closes that
/// gap.
///
/// <para>Keyed on the <b>human</b>, not the Lead credential or the Team: the
/// machine on a person's desk does not change when their session ends or when
/// they address another Team. Each human has their own binding or none (§4).</para>
///
/// <para>One live binding per human and per machine, enforced by two partial
/// unique indexes (<see cref="LandbridgeDbContext.OneLiveBindingPerHumanIndex"/>,
/// <see cref="LandbridgeDbContext.OneLiveBindingPerMachineIndex"/>) rather than by a
/// read — the same shape as one-live-Lead-per-Team. Revocation is a soft delete
/// so an unbind/rebind history survives.</para>
/// </summary>
public sealed class LeadMachineBindingRow
{
    public Guid Id { get; set; }

    /// <summary>The human session's own credential id (a Lead credential's <see cref="CredentialRow.HumanId"/>).</summary>
    public Guid HumanId { get; set; }

    /// <summary>The enrolled machine (<see cref="MachineRow.Id"/>) this human sits at.</summary>
    public Guid MachineId { get; set; }

    public DateTimeOffset BoundAt { get; set; }

    /// <summary>Set by an explicit unbind; a revoked row is kept rather than deleted and frees
    /// both unique slots. <see cref="Revoked"/> is the fact every predicate reads.</summary>
    public bool Revoked { get; set; }

    /// <summary>When the unbind happened. Written but read by no query or surface — retained
    /// deliberately, because "when did this stop being true" is what a §13 incident review asks
    /// and the row is the only place that could answer it. Do not mistake it for a live input.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// A live lead↔machine binding as read back (§8.3 human path): the machine's id,
/// its enrollment name (so a confirmation names something a person recognizes),
/// and when the binding was made.
/// </summary>
public sealed record LeadMachineBinding(Guid MachineId, string MachineName, DateTimeOffset BoundAt);

/// <summary>
/// Outcome of a bind attempt. <see cref="Refused"/> carries a Lead-facing reason
/// rather than a <see cref="Landbridge.Core.Rule"/> — a binding precondition is
/// plane-side routing state, not one of §9's enforcement checks — and is rendered
/// by the tool surface exactly like <see cref="ForwardEstablishResult.Failed"/>.
/// </summary>
public abstract record LeadMachineBindResult
{
    private LeadMachineBindResult() { }

    public sealed record Bound(LeadMachineBinding Binding) : LeadMachineBindResult;

    public sealed record Refused(string Reason) : LeadMachineBindResult;
}

/// <summary>
/// An enrolled machine (§11). <see cref="Name"/> and <see cref="Os"/> are
/// declared once at enrollment and written server-side, so a machine cannot
/// re-declare them for itself (§13). Liveness (last spoke, ready, profiles) is
/// last-value columns this heartbeat upserts. The registry is the socket.
/// </summary>
public sealed class MachineRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Os { get; set; } = "";
    public DateTimeOffset EnrolledAt { get; set; }
    public bool Revoked { get; set; }

    /// <summary>
    /// Allocated dashboard alias (<c>adjective-noun-NNNN</c>). The Guid stays the
    /// wire id; humans see this. Unique, minted at enrollment.
    /// </summary>
    public string Slug { get; set; } = "";

    /// <summary>When the machine was revoked. Written but read by nothing; kept for §13
    /// forensics, like every other <c>revoked_at</c> in the schema.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Last applied heartbeat. Null until the first beat after enroll.</summary>
    public DateTimeOffset? LastSpokeAt { get; set; }

    /// <summary>Ready unless under back-pressure, as of <see cref="LastSpokeAt"/>.</summary>
    public bool Ready { get; set; }

    public bool UnderBackPressure { get; set; }

    /// <summary>Declared profiles on the last heartbeat.</summary>
    public string[] Profiles { get; set; } = [];

    /// <summary>
    /// Whether this machine can answer <c>read-transcript</c> (§12), as of
    /// <see cref="LastSpokeAt"/>. A runner that cannot rejects the command at the wire
    /// boundary and never replies, which is indistinguishable from a slow machine — so
    /// the plane reads this instead of waiting to find out.
    /// </summary>
    public bool TranscriptsServable { get; set; }
}


/// <summary>
/// What a validated token authenticates as. The worker principal carries the
/// engine's own actor type, so the MCP layer hands
/// <see cref="Landbridge.Core.WorkerCaller"/> straight to transitions — authority
/// stays structural end to end.
/// </summary>
public abstract record Principal
{
    public sealed record Worker(Landbridge.Core.WorkerCaller Caller) : Principal;

    public sealed record Machine(Guid MachineId) : Principal;

    /// <summary>A human's own session (§5). Carries its session id for lead-claim attribution.</summary>
    public sealed record Human(Guid HumanId) : Principal
    {
        public Landbridge.Core.HumanSession Actor { get; } = new();
    }

    /// <summary>
    /// A live Lead factory credential (§5). <see cref="CredentialId"/> is the
    /// row whose <see cref="LeadTeamRow"/> list names the Teams it created.
    /// The engine actor is still <see cref="Landbridge.Core.LeadClaim"/> —
    /// Team-only — constructed per call once the team id resolves, not stored
    /// on this principal.
    ///
    /// <para><see cref="HumanId"/> is the claiming human's session id, carried from
    /// the credential row (§4: a Lead's authority descends from a human). The human
    /// id is what a lead↔machine binding keys on (§8.3 human path).
    /// Nullable because <see cref="CredentialRow.HumanId"/> is: a synthesized or
    /// pre-attribution lead credential authenticates but owns no binding.</para>
    /// </summary>
    public sealed record Lead(Guid CredentialId, Guid? HumanId = null) : Principal;

    /// <summary>
    /// A lead token an older build evicted while moving a Team (§4). It
    /// authenticates only far enough to tell its holder why: the tool surface
    /// refuses the call with an explicit reason — who, and when — instead of
    /// a bare 403. Nothing new mints one.
    /// </summary>
    public sealed record EvictedLead(Landbridge.Core.TeamId Team, Guid EvictedByHuman, DateTimeOffset EvictedAt) : Principal;
}

/// <summary>
/// Outcome of issuing a Lead factory token for a Team (§4). A Team that
/// already has a live factory refuses a second token and names who holds the
/// first. Address that Team by its id.
/// </summary>
public abstract record LeadClaimResult
{
    private LeadClaimResult() { }

    /// <summary>The claim succeeded; the lead token is minted once here.</summary>
    public sealed record Claimed(IssuedToken Token, Landbridge.Core.TeamId Team) : LeadClaimResult;

    /// <summary>The Team already has a live factory (§4).</summary>
    public sealed record Refused(Guid HeldByHuman, DateTimeOffset HeldSince) : LeadClaimResult;

    /// <summary>The presented token was not a live human session (§5: leads descend from humans).</summary>
    public sealed record NoHumanSession : LeadClaimResult;
}

/// <summary>A newly minted token. The plaintext exists only in this value, once.</summary>
public sealed record IssuedToken(string Token, Guid CredentialId, DateTimeOffset? ExpiresAt);

/// <summary>Access + refresh pair handed to landbridged at enrollment (§5, §13).</summary>
public sealed record MachineCredentials(Guid MachineId, IssuedToken Access, IssuedToken Refresh, string Slug = "");

/// <summary>Declared at enrollment, bound server-side (§11, §13). Name is the
/// display label; OS is filled by landbridged.</summary>
public sealed record MachineDeclaration(string Name, string Os);
