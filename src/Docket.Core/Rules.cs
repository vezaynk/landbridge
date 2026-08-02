namespace Docket.Core;

/// <summary>
/// The enforcement checks of spec §9 (values 1–14, numbered to match) plus
/// the structural invariants of §6's table (values ≥ 100). A rejected
/// transition names exactly one of these, so every refusal is traceable to a
/// spec line.
///
/// Not every check is the engine's to enforce. Enforcement sites:
///   engine — 1, 3, 4, 8, 9 (creation gate), 12, 14, and the §6 invariants
///   store  — 2 (namespace assignment), 5 (SKIP LOCKED single dispatch,
///            machine-eligibility half in the engine), 6 (one lead per team)
///   plane timers — 7 (expressed as LivenessLost / WaitTtlExpired commands)
///   relay  — 10, 11
///   auth   — 13
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
    TeamBudgetCeiling = 9,
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
