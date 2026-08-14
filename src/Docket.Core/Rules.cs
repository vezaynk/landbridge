namespace Docket.Core;

/// <summary>
/// The enforcement checks of spec §9 (values 1–14, numbered to match) plus
/// the structural invariants of §6's table (values ≥ 100). A rejected
/// transition names exactly one of these, so every refusal is traceable to a
/// spec line.
///
/// Not every check is the engine's to enforce. Enforcement sites:
///   engine — 1, 3, 4, 8, 12, 14, and the §6 invariants
///   store  — 2 (namespace assignment), 5 (SKIP LOCKED single dispatch,
///            machine-eligibility half in the engine), 6 (one lead per team)
///   plane timers — 7 (expressed as LivenessLost / WaitTtlExpired commands)
///   relay  — 10, 11
///   auth   — 13
///
/// <para><b>9 is retired, not reassigned.</b> The dollar budget ceiling was removed
/// 2026-08-12 (§9's own note), and its value stays vacant so the remaining checks keep
/// the numbers every "§9 check N" reference in this codebase already uses. Renumbering
/// would silently redirect all of them.</para>
/// </summary>
public enum Rule
{
    CompletionCriteriaNonEmpty = 1,
    NamespaceServerAssigned = 2,
    OnlyLeadCreatesTasks = 3,
    CompletionByLeadOrHuman = 4,
    SingleDispatchPerTask = 5,
    OneLeadPerTeam = 6,
    LivenessTimeoutRequeue = 7,
    VerificationRetriesExhausted = 8,
    // 9 was TeamBudgetCeiling; see the type remarks.
    TeamByteAllowance = 10,
    ForwardsRequireRegistration = 11,
    CancellationCarriesDisposition = 12,
    TokenExchangeNarrowing = 13,
    IncumbentInstanceOnly = 14,

    // §6 structural invariants
    TerminalStatesAreFinal = 100,
    InvalidSourceState = 101,
    ActorLacksAuthority = 102,
    MachineIneligibleForDispatch = 103,
    ResultReferenceRequired = 104,
    TypedRequestKindRequired = 105,

    // §6/§11 continuation-targeting creation gates
    ContinuationSameTeamOnly = 106,
    ContinuationProfileDeclaredByPreferredMachine = 107,

    // §10 in-band worker report size cap
    ReportWithinSizeCap = 108,

    // §10/§11 in-band question/answer size caps — the same length gate as the
    // report, on the two halves of the human-in-the-loop exchange.
    QuestionWithinSizeCap = 109,
    AnswerWithinSizeCap = 110,

    // §11 permission bridge. The permission flavor of blocked_on_input is answered by a
    // verdict rather than by prose, so it needs its own gates: the two answer paths must
    // not be usable on each other's requests (111, 112), a refusal must be legible to the
    // agent it refuses (113), an escalation must say why (114), escalation must actually
    // remove the Lead's authority (115), and a verdict must have a live worker to return
    // to (116).
    PermissionRequestNamesItsTool = 111,
    PermissionVerdictAnswersPermissionRequests = 112,
    PermissionDenialCarriesMessage = 113,
    PermissionEscalationCarriesReason = 114,
    EscalatedPermissionIsHumanOnly = 115,
    PermissionWaiterStillIncumbent = 116,

    /// <summary>
    /// §8.2: a service name is the Team-scoped <em>address</em> of an endpoint, so at most one
    /// live registration may hold it. Store-enforced (and backed by a unique index), because
    /// it is an invariant about rows rather than about one task's state: everything that
    /// resolves a forward — <c>open_forward</c>, an §8.4 preview, the §8.3 human path — is
    /// handed a name and a Team and nothing else, so a second task registering a name already
    /// held made the resolution a raffle between two ports. Refused rather than silently
    /// reassigned: the second worker learns the name is taken and can pick another, where a
    /// last-writer-wins upsert would have quietly redirected the first worker's consumers.
    /// </summary>
    ServiceNameUniqueInTeam = 117,
}

public abstract record TransitionResult
{
    private TransitionResult() { }

    public sealed record Transitioned(TaskRecord Task, IReadOnlyList<Effect> Effects) : TransitionResult;

    public sealed record Rejected(Rule Rule, string Reason) : TransitionResult;

    internal static TransitionResult Ok(TaskRecord task, params Effect[] effects) =>
        new Transitioned(task, effects);

    internal static TransitionResult Reject(Rule rule, string reason) =>
        new Rejected(rule, reason);
}
