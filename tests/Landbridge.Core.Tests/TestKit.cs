namespace Landbridge.Core.Tests;

internal static class Given
{
    public static readonly TeamId Team = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    public static readonly TeamId OtherTeam = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    public static readonly SessionId Id = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    public static LeadClaim Lead => new(Team);
    public static LeadClaim ForeignLead => new(OtherTeam);
    public static HumanSession Human => new();

    public static MachineSnapshot Machine(
        bool ready = true,
        bool backPressure = false,
        params string[] profiles) =>
        new("machine-a", ready, backPressure,
            profiles.Length == 0 ? new HashSet<string> { "default" } : new HashSet<string>(profiles));

    public static ParkRecord Park => new("machine-a");

    public static SessionRecord Session(
        SessionState state = SessionState.Submitted,
        CompletionMode mode = CompletionMode.Lead,
        WorkerInstanceId? instance = null,
        int verificationFailures = 0,
        int retryLimit = 3,
        string? profile = "default",
        int infrastructureRequeues = 0,
        int requeueLimit = SessionRecord.DefaultInfrastructureRequeueLimit) => new()
    {
        Id = Id,
        Team = Team,
        Namespace = $"team-{Team}/session-{Id}",
        CompletionMode = mode,
        State = state,
        Profile = profile,
        Attempt = state == SessionState.Submitted ? 0 : 1,
        VerificationFailures = verificationFailures,
        VerificationRetryLimit = retryLimit,
        InfrastructureRequeues = infrastructureRequeues,
        InfrastructureRequeueLimit = requeueLimit,
        CurrentInstance = instance ??
            (state is SessionState.Working or SessionState.BlockedOnInput or SessionState.Verifying
                ? WorkerInstanceId.New()
                : null),
    };

    public static WorkerCaller IncumbentOf(SessionRecord task) =>
        new(task.Team, task.Id, task.CurrentInstance!.Value);
}

internal static class Expect
{
    public static SessionRecord Transitioned(TransitionResult result, SessionState state)
    {
        var ok = Assert.IsType<TransitionResult.Transitioned>(result);
        Assert.Equal(state, ok.Session.State);
        return ok.Session;
    }

    public static IReadOnlyList<Effect> Effects(TransitionResult result)
    {
        var ok = Assert.IsType<TransitionResult.Transitioned>(result);
        return ok.Effects;
    }

    public static void Rejected(TransitionResult result, Rule rule)
    {
        var rejected = Assert.IsType<TransitionResult.Rejected>(result);
        Assert.Equal(rule, rejected.Rule);
    }
}
