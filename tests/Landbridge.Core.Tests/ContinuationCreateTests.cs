namespace Landbridge.Core.Tests;

/// <summary>
/// §6/§11 continuation-targeting creation rules, as pure engine checks. The store
/// and tool resolve the <see cref="Continuation"/> facts from the continued task's
/// row and the live registry; these tests pin what the engine itself validates —
/// same-Team, and (when the preferred machine's profiles are known) profile-declarable —
/// and that everything else rides through untouched.
/// </summary>
public class ContinuationCreateTests
{
    private static readonly SessionId Continued = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    private static Continuation Cont(
        TeamId? continuedTeam = null,
        string? preferredMachine = "machine-a",
        string? sessionRef = "sess-1",
        MachineGonePolicy policy = MachineGonePolicy.Degrade,
        IReadOnlySet<string>? declaredProfiles = null) =>
        new(Continued, continuedTeam ?? Given.Team, preferredMachine, sessionRef, policy, declaredProfiles);

    private static TransitionResult Create(string profile, Continuation continuation) =>
        SessionStateMachine.Create(
            new CreateSession(Given.Lead, Given.Team, "criteria", Profile: profile, Continues: continuation),
            Given.Id, "team-x/task-y");

    [Fact]
    public void Continuation_in_the_same_team_creates_submitted()
    {
        var task = Expect.Transitioned(Create(profile: "default", Cont()), SessionState.Submitted);
        Assert.Equal(PendingSpawn.Load, task.PendingSpawn);
    }

    [Fact]
    public void Continuation_without_an_inherited_ref_is_a_cold_start()
    {
        var task = Expect.Transitioned(
            Create(profile: "default", Cont(sessionRef: null)), SessionState.Submitted);
        Assert.Equal(PendingSpawn.New, task.PendingSpawn);
    }

    [Fact]
    public void Continuation_referencing_another_team_is_rejected()
    {
        Expect.Rejected(
            Create(profile: "default", Cont(continuedTeam: Given.OtherTeam)),
            Rule.ContinuationSameTeamOnly);
    }

    [Fact]
    public void Continuation_with_a_profile_the_preferred_machine_declares_creates_submitted()
    {
        Expect.Transitioned(
            Create(profile: "gpu", Cont(declaredProfiles: new HashSet<string> { "gpu", "default" })),
            SessionState.Submitted);
    }

    [Fact]
    public void Continuation_with_a_profile_the_preferred_machine_lacks_is_rejected()
    {
        Expect.Rejected(
            Create(profile: "gpu", Cont(declaredProfiles: new HashSet<string> { "default" })),
            Rule.ContinuationProfileDeclaredByPreferredMachine);
    }

    [Fact]
    public void Continuation_with_empty_profile_is_rejected()
    {
        Expect.Rejected(
            Create(profile: "", Cont(declaredProfiles: new HashSet<string> { "gpu" })),
            Rule.ProfileRequired);
    }

    [Fact]
    public void Continuation_skips_the_profile_check_when_the_preferred_machine_is_gone()
    {
        // Null declared-profiles ≡ the preferred machine is not connected, so the
        // profile can't be verified at creation — creation proceeds and dispatch's
        // own profile routing applies later.
        Expect.Transitioned(
            Create(profile: "anything", Cont(declaredProfiles: null)),
            SessionState.Submitted);
    }
}
