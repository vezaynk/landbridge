using Landbridge.Contracts;
using Landbridge.ControlPlane;
using Landbridge.Core;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// The §7/§10 profile routing projection — what a Lead's <c>list_profiles</c> reads.
/// Facts come from <c>machines</c> columns; the registry is the socket. Agrees
/// with <see cref="MachineLive.ReadyAsync"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProfileRoutingTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new();

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task A_profile_a_machine_declares_appears_with_that_machine_and_one_nothing_declares_does_not()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var registry = new RunnerConnectionRegistry(_clock);
        var m1 = await TestMachines.ConnectAsync(db, _clock, registry, "m1", profiles: ["default", "gpu"]);
        var m2 = await TestMachines.ConnectAsync(db, _clock, registry, "m2", profiles: ["default"]);

        var view = await MachineLive.RoutingAsync(db, registry, CancellationToken.None);

        Assert.Equal(new[] { "default", "gpu" }, view.Profiles.Select(p => p.Profile));
        Assert.Equal(new[] { m1.ToString(), m2.ToString() }.OrderBy(s => s),
            Entry(view, "default").Machines.Select(m => m.MachineId).OrderBy(s => s));
        Assert.Equal(new[] { m1.ToString() }, Entry(view, "gpu").Machines.Select(m => m.MachineId));
        Assert.Equal(2, view.ConnectedMachines);
        Assert.DoesNotContain("restricted", view.Profiles.Select(p => p.Profile));
    }

    [SkippableFact]
    public async Task Dispatchable_agrees_with_the_pick_dispatch_itself_would_make()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var registry = new RunnerConnectionRegistry(_clock);
        await TestMachines.ConnectAsync(db, _clock, registry, "ready", profiles: ["default", "gpu"]);
        await TestMachines.ConnectAsync(
            db, _clock, registry, "saturated", ready: true, underBackPressure: true,
            profiles: ["gpu", "restricted"]);
        var silent = await TestMachines.EnrollAsync(db, _clock, "silent");
        TestMachines.Register(registry, silent);

        var view = await MachineLive.RoutingAsync(db, registry, CancellationToken.None);
        var ready = await MachineLive.ReadyAsync(
            db, registry, _clock.GetUtcNow(), WaitTtlSweeper.DefaultMachineLivenessWindow, CancellationToken.None);

        Assert.Equal(new[] { "default", "gpu", "restricted" }, view.Profiles.Select(p => p.Profile));
        foreach (var entry in view.Profiles)
            Assert.Equal(ready.Any(r => r.Snapshot.DeclaredProfiles.Contains(entry.Profile)), entry.Dispatchable);

        Assert.True(Entry(view, "default").Dispatchable);
        Assert.True(Entry(view, "gpu").Dispatchable);
        Assert.False(Entry(view, "restricted").Dispatchable);
        Assert.DoesNotContain(view.Profiles, p => p.Profile == "quiet");
        Assert.Equal(3, view.ConnectedMachines);
    }

    [SkippableFact]
    public async Task A_saturated_profile_is_listed_as_present_but_not_dispatchable_rather_than_vanishing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var registry = new RunnerConnectionRegistry(_clock);
        var m1 = await TestMachines.ConnectAsync(
            db, _clock, registry, "m1", ready: true, underBackPressure: true, profiles: ["restricted"]);

        var view = await MachineLive.RoutingAsync(db, registry, CancellationToken.None);
        var entry = Assert.Single(view.Profiles);
        Assert.Equal("restricted", entry.Profile);
        Assert.False(entry.Dispatchable);
        var machine = Assert.Single(entry.Machines);
        Assert.Equal(m1.ToString(), machine.MachineId);
        Assert.False(machine.Ready);
        Assert.True(machine.UnderBackPressure);
    }

    [SkippableFact]
    public async Task Liveness_reflects_the_connected_machines_and_a_disconnect_removes_the_profile_with_them()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var registry = new RunnerConnectionRegistry(_clock);
        var m1 = await TestMachines.ConnectAsync(db, _clock, registry, "m1", profiles: ["default", "gpu"]);
        var m2 = await TestMachines.ConnectAsync(db, _clock, registry, "m2", profiles: ["default"]);
        var heartbeatAt = _clock.GetUtcNow();

        _clock.Advance(TimeSpan.FromSeconds(30));
        var beforeDrop = await MachineLive.RoutingAsync(db, registry, CancellationToken.None);
        Assert.All(
            beforeDrop.Profiles.SelectMany(p => p.Machines),
            m => Assert.Equal(heartbeatAt, m.LastHeartbeat));

        await registry.DisconnectAsync(m1.ToString());

        var view = await MachineLive.RoutingAsync(db, registry, CancellationToken.None);

        Assert.Equal(new[] { "default" }, view.Profiles.Select(p => p.Profile));
        Assert.Equal(new[] { m2.ToString() }, Entry(view, "default").Machines.Select(m => m.MachineId));
        Assert.Equal(1, view.ConnectedMachines);
    }

    [SkippableFact]
    public async Task An_empty_fleet_and_a_declaring_nothing_fleet_are_told_apart_by_the_machine_count()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        await using var db = pg.NewContext();
        var empty = await MachineLive.RoutingAsync(db, new RunnerConnectionRegistry(_clock), CancellationToken.None);
        Assert.Empty(empty.Profiles);
        Assert.Equal(0, empty.ConnectedMachines);

        var dialling = new RunnerConnectionRegistry(_clock);
        var silent = await TestMachines.EnrollAsync(db, _clock, "m1");
        TestMachines.Register(dialling, silent);
        var view = await MachineLive.RoutingAsync(db, dialling, CancellationToken.None);
        Assert.Empty(view.Profiles);
        Assert.Equal(1, view.ConnectedMachines);
    }

    private static ProfileRoutingEntry Entry(ProfileRoutingView view, string profile) =>
        view.Profiles.Single(p => p.Profile == profile);
}
