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
        WorkerInstanceId? instance = null,
        int verificationFailures = 0,
        int retryLimit = 3,
        string? profile = "default",
        int infrastructureRequeues = 0,
        int requeueLimit = SessionRecord.DefaultInfrastructureRequeueLimit,
        MessageState? message = null)
    {
        var seated = state is SessionState.Working or SessionState.BlockedOnInput;
        var current = instance ?? (seated ? WorkerInstanceId.New() : null);
        var msgState = message ?? state switch
        {
            SessionState.BlockedOnInput => MessageState.AwaitingPermission,
            _ => MessageState.Idle,
        };
        var task = new SessionRecord
        {
            Id = Id,
            Team = Team,
            Namespace = $"team-{Team}/session-{Id}",
            Profile = profile,
            Attempt = state == SessionState.Submitted ? 0 : 1,
            VerificationFailures = verificationFailures,
            VerificationRetryLimit = retryLimit,
            InfrastructureRequeues = infrastructureRequeues,
            InfrastructureRequeueLimit = requeueLimit,
            CurrentInstance = current,
            OccupancyDesired = state switch
            {
                SessionState.Parked or SessionState.Completed or SessionState.Rejected
                    or SessionState.Canceled => Occupancy.OnDisk,
                SessionState.Failed => Occupancy.Running,
                _ => Occupancy.Running,
            },
            OccupancyObserved = state switch
            {
                SessionState.Submitted => Occupancy.None,
                SessionState.Parked or SessionState.Completed or SessionState.Rejected
                    or SessionState.Canceled or SessionState.Failed => Occupancy.OnDisk,
                _ => Occupancy.Running,
            },
            Health = state == SessionState.Failed ? SessionHealth.Failed : SessionHealth.Ok,
            Hidden = state is SessionState.Completed or SessionState.Rejected or SessionState.Canceled,
            MessageState = msgState,
            MessageVerdict = state switch
            {
                SessionState.Completed => MessageVerdict.Accepted,
                SessionState.Rejected => MessageVerdict.Discarded,
                _ => null,
            },
            MessageId = msgState == MessageState.Idle
                ? null
                : Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            LastMessageId = state is SessionState.Completed or SessionState.Rejected or SessionState.Canceled
                ? Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
                : null,
            LastMessageTerminal = state switch
            {
                SessionState.Canceled => MessageTerminal.Cancelled,
                SessionState.Completed or SessionState.Rejected => MessageTerminal.Completed,
                _ => null,
            },
            PendingSpawn = state == SessionState.Submitted ? PendingSpawn.New : null,
            LastRequeueReason = state == SessionState.Failed ? LivenessLossReason.ProcessExited : null,
            Park = state is SessionState.Parked or SessionState.Failed ? Park : null,
        };
        return task with { State = SessionRecord.DeriveState(task) };
    }

    public static WorkerCaller IncumbentOf(SessionRecord task) =>
        new(task.Team, task.Id, task.CurrentInstance!.Value);

    /// <summary>A seated worker that has mailed a report. Derived state is Working.</summary>
    public static SessionRecord Reported(
        WorkerInstanceId? instance = null,
        int verificationFailures = 0,
        int retryLimit = 3) =>
        Session(SessionState.Working, instance, verificationFailures, retryLimit)
            with { ReportUnread = true };

    /// <summary>A seated worker waiting on a prose question. Derived state is Working.</summary>
    public static SessionRecord Asking(WorkerInstanceId? instance = null) =>
        Session(SessionState.Working, instance, message: MessageState.AwaitingLead);
}

internal static class Expect
{
    public static SessionRecord Transitioned(TransitionResult result, SessionState state)
    {
        var ok = Assert.IsType<TransitionResult.Transitioned>(result);
        Assert.Equal(state, ok.Session.State);
        return ok.Session;
    }

    public static SessionRecord Reported(TransitionResult result)
    {
        var task = Transitioned(result, SessionState.Working);
        Assert.Equal(MessageState.Idle, task.MessageState);
        Assert.True(task.ReportUnread);
        return task;
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
