using Docket.Contracts;
using Docket.Core;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The §7/§10 profile routing projection — what a Lead's <c>list_profiles</c> reads.
/// Pure registry, no store and no wire: the whole question here is whether the view a Lead
/// is shown agrees with what dispatch would actually match.
///
/// The load-bearing test is
/// <see cref="Dispatchable_agrees_with_the_pick_dispatch_itself_would_make"/>: everything else
/// checks that the right names and machines appear, but a view that reported reachability
/// dispatch disagreed with would be worse than no view at all — a Lead would route a task
/// somewhere confidently and it would still never start.
/// </summary>
public sealed class ProfileRoutingTests
{
    private readonly FakeTimeProvider _clock = new();

    [Fact]
    public void A_profile_a_machine_declares_appears_with_that_machine_and_one_nothing_declares_does_not()
    {
        var registry = new RunnerConnectionRegistry(_clock);
        Connect(registry, "m1", "default", "gpu");
        Connect(registry, "m2", "default");

        var view = registry.ProfileRouting();

        // Exactly the declared set — the names create_task can match, and no others.
        Assert.Equal(new[] { "default", "gpu" }, view.Profiles.Select(p => p.Profile));
        // Both machines declaring `default` are listed under it; only m1 offers `gpu`.
        Assert.Equal(new[] { "m1", "m2" }, Entry(view, "default").Machines.Select(m => m.MachineId));
        Assert.Equal(new[] { "m1" }, Entry(view, "gpu").Machines.Select(m => m.MachineId));
        Assert.Equal(2, view.ConnectedMachines);

        // The unclaimable-profile case this read exists to end: a name nothing declares is
        // simply absent, so a Lead reading the list never sends a task after it.
        Assert.DoesNotContain("restricted", view.Profiles.Select(p => p.Profile));
    }

    [Fact]
    public void Dispatchable_agrees_with_the_pick_dispatch_itself_would_make()
    {
        // The invariant that makes this view trustworthy rather than merely informative:
        // `dispatchable` must mean what dispatch means. TryPickMachine is the registry read
        // a dispatch pass uses to find a machine for a required profile, so the two answers
        // are compared directly across the states a machine can be in — ready, saturated,
        // and connected-but-never-heartbeat.
        var registry = new RunnerConnectionRegistry(_clock);
        Connect(registry, "ready", "default", "gpu");
        ConnectSaturated(registry, "saturated", "gpu", "restricted");
        registry.Register("silent", new HashSet<string> { "quiet" }, Send); // no heartbeat yet

        var view = registry.ProfileRouting();

        Assert.Equal(
            new[] { "default", "gpu", "quiet", "restricted" }, view.Profiles.Select(p => p.Profile));
        foreach (var entry in view.Profiles)
            Assert.Equal(registry.TryPickMachine(entry.Profile) is not null, entry.Dispatchable);

        // And concretely, so the loop above cannot pass by both sides being wrong together.
        Assert.True(Entry(view, "default").Dispatchable);
        Assert.True(Entry(view, "gpu").Dispatchable);         // the ready machine also offers it
        Assert.False(Entry(view, "restricted").Dispatchable); // only the saturated one does
        Assert.False(Entry(view, "quiet").Dispatchable);      // declared, never heartbeat
    }

    [Fact]
    public void A_saturated_profile_is_listed_as_present_but_not_dispatchable_rather_than_vanishing()
    {
        // Why the projection enumerates every machine instead of only the ready ones: a
        // profile whose machines are all busy must read as "wait", not as "no machine
        // declares this". They are opposite instructions — one says queue the task, the
        // other says do not create it — and a Lead cannot tell them apart from an absence.
        var registry = new RunnerConnectionRegistry(_clock);
        ConnectSaturated(registry, "m1", "restricted");

        var entry = Assert.Single(registry.ProfileRouting().Profiles);

        Assert.Equal("restricted", entry.Profile);
        Assert.False(entry.Dispatchable);
        var machine = Assert.Single(entry.Machines);
        Assert.Equal("m1", machine.MachineId);
        Assert.False(machine.Ready);
        Assert.True(machine.UnderBackPressure); // saturated, not broken — a different wait
    }

    [Fact]
    public void Liveness_reflects_the_connected_machines_and_a_disconnect_removes_the_profile_with_them()
    {
        var registry = new RunnerConnectionRegistry(_clock);
        var m1 = Connect(registry, "m1", "default", "gpu");
        Connect(registry, "m2", "default");
        var heartbeatAt = _clock.GetUtcNow();

        _clock.Advance(TimeSpan.FromSeconds(30));
        var beforeDrop = registry.ProfileRouting();
        // The heartbeat timestamp is the machine's own, so a Lead can judge the answer's age.
        Assert.All(
            beforeDrop.Profiles.SelectMany(p => p.Machines),
            m => Assert.Equal(heartbeatAt, m.LastHeartbeat));

        // m1's socket closes. Its profiles are process-local registry state (§10), so `gpu`
        // — which only m1 declared — goes with it, while `default` survives on m2.
        registry.Unregister(m1);
        var view = registry.ProfileRouting();

        Assert.Equal(new[] { "default" }, view.Profiles.Select(p => p.Profile));
        Assert.Equal(new[] { "m2" }, Entry(view, "default").Machines.Select(m => m.MachineId));
        Assert.Equal(1, view.ConnectedMachines);
    }

    [Fact]
    public void An_empty_fleet_and_a_declaring_nothing_fleet_are_told_apart_by_the_machine_count()
    {
        // Both answers have no profiles, and they need different responses: nothing
        // connected means the fleet is down, while a connected machine that has not yet
        // declared means its first heartbeat is still coming (§10). The count is the only
        // thing distinguishing them.
        var empty = new RunnerConnectionRegistry(_clock).ProfileRouting();
        Assert.Empty(empty.Profiles);
        Assert.Equal(0, empty.ConnectedMachines);

        var dialling = new RunnerConnectionRegistry(_clock);
        dialling.Register("m1", new HashSet<string>(), Send);
        var view = dialling.ProfileRouting();
        Assert.Empty(view.Profiles);
        Assert.Equal(1, view.ConnectedMachines);
    }

    [Fact]
    public void The_view_names_the_profile_an_omitted_create_task_profile_resolves_to()
    {
        // A Lead that omits `profile` still routes on a name — `default` — and the engine
        // matches it exactly like any other (TaskStateMachine's ApplyDispatch). So a fleet
        // where nothing declares `default` cannot run plain tasks at all, and this field is
        // what lets a Lead notice that rather than watch them queue forever.
        var registry = new RunnerConnectionRegistry(_clock);
        Connect(registry, "m1", "gpu");

        var view = registry.ProfileRouting();

        Assert.Equal(MachineSnapshot.DefaultProfile, view.DefaultProfile);
        Assert.DoesNotContain(view.DefaultProfile, view.Profiles.Select(p => p.Profile));
    }

    /// <summary>A machine dialled in and heartbeating its declared profiles on the fake
    /// clock — exactly what the runner endpoint sets up (§10).</summary>
    private RunnerConnectionRegistry.ConnectionToken Connect(
        RunnerConnectionRegistry registry, string machineId, params string[] profiles) =>
        Heartbeat(registry, machineId, underBackPressure: false, profiles);

    /// <summary>The same, but reporting back-pressure — connected and declaring, not
    /// accepting dispatch (§10).</summary>
    private RunnerConnectionRegistry.ConnectionToken ConnectSaturated(
        RunnerConnectionRegistry registry, string machineId, params string[] profiles) =>
        Heartbeat(registry, machineId, underBackPressure: true, profiles);

    private RunnerConnectionRegistry.ConnectionToken Heartbeat(
        RunnerConnectionRegistry registry, string machineId, bool underBackPressure, string[] profiles)
    {
        var registration = registry.Register(machineId, new HashSet<string>(profiles), Send);
        registry.ApplyHeartbeat(registration.Token, new MachineHeartbeat(
            machineId, Ready: true, underBackPressure, new SystemLoad(0, 0, 0),
            RunningTasks: 0, profiles, _clock.GetUtcNow()));
        return registration.Token;
    }

    private static ProfileRoutingEntry Entry(ProfileRoutingView view, string profile) =>
        view.Profiles.Single(p => p.Profile == profile);

    private static Task Send(RunnerCommand command, CancellationToken ct) => Task.CompletedTask;
}
