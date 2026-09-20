using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using CollabProgram = Landbridge.CollabHarness.Program;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// The multi-machine collaboration crown (spec §8.3), scripted/deterministic tier:
/// several <c>landbridged</c> machines coordinating through one control plane + relay to
/// complete a task, with <b>no LLM and no fakes on any surface that matters</b>. A real
/// plane, a real relay validating grants against it, and N real runner rigs each
/// spawning the scripted <c>Landbridge.CollabHarness</c> — steered entirely by each task's
/// opaque prose description (§7).
///
/// <para>Every scenario spans ≥2 distinct machines and asserts on committed
/// control-plane state (the reported result reference, the registered service, the
/// machine a task landed on). The real-<c>claude -p</c> variants are a separate,
/// key-gated follow-up; this tier spends no tokens and needs no key.</para>
///
/// <para>Skips (rather than fails) when Postgres is unavailable, mirroring every other
/// Postgres-backed suite; otherwise it RUNS.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MultiMachineCollaborationTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Scenario 1 — relay handshake. Machine A serves an unforgeable nonce; machine B
    /// opens a forward to it and reads the nonce across the relay. B's reported result
    /// carrying A's <em>exact</em> generated nonce is the proof cross-machine bytes flowed.
    /// </summary>
    [SkippableFact]
    public async Task Handshake_carries_the_serving_machines_nonce_across_the_relay()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        // A binds the handshake service and stays working.
        var serve = await rig.CreateSessionAsync("handshake-serve", ct);
        await rig.DispatchToAsync("A", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(() => rig.ServiceExistsAsync("handshake", ct), Bound),
            "machine A never registered the handshake service. " + await rig.DiagnoseAsync(serve, ct));

        // The exact nonce A generated, read from its atomic marker.
        string? nonce = null;
        Assert.True(
            await FleetRig.WaitUntilAsync(
                async () => (nonce = await rig.ReadMarkerAsync("A", serve, "handshake-nonce.txt", ct)) is not null, Bound),
            "machine A never wrote its handshake nonce marker");

        // B opens the forward, reads the nonce, and reports it.
        var consume = await rig.CreateSessionAsync("handshake-consume", ct);
        await rig.DispatchToAsync("B", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.HasReportAsync(consume, ct), Bound),
            "machine B never mailed a report on consume. " + await rig.DiagnoseAsync(consume, ct));

        Assert.Equal($"handshake:{nonce}", await rig.ResultReferenceAsync(consume, ct));
        // The two ends really were different machines.
        Assert.Equal("A", rig.MachineOf(serve));
        Assert.Equal("B", rig.MachineOf(consume));
    }

    /// <summary>
    /// Scenario 2 — build → serve → test. Machine A stands up a deterministic
    /// <c>n → n+1</c> service; machine B opens a forward, drives inputs through it, and
    /// verifies every answer came back incremented across the relay before reporting pass.
    /// </summary>
    [SkippableFact]
    public async Task Compute_service_round_trips_requests_for_a_consumer_on_another_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        var serve = await rig.CreateSessionAsync("compute-serve", ct);
        await rig.DispatchToAsync("A", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(() => rig.ServiceExistsAsync("compute", ct), Bound),
            "machine A never registered the compute service. " + await rig.DiagnoseAsync(serve, ct));

        var test = await rig.CreateSessionAsync("compute-test", ct);
        await rig.DispatchToAsync("B", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.HasReportAsync(test, ct), Bound),
            "machine B never mailed a report on compute-test. " + await rig.DiagnoseAsync(test, ct));

        // The worker only reports pass after every input round-tripped incremented; a
        // relay that dropped or corrupted bytes would have thrown, reporting nothing.
        Assert.Equal("compute:pass", await rig.ResultReferenceAsync(test, ct));
        Assert.Equal("A", rig.MachineOf(serve));
        Assert.Equal("B", rig.MachineOf(test));
    }

    /// <summary>
    /// Scenario 3 — fan-out / aggregate. A Lead creates three <c>map:&lt;seed&gt;</c>
    /// subtasks and spreads them across two machines; all three complete with the correct
    /// deterministic transform, and the set of machines they landed on is ≥2. The fan-out
    /// plus cross-machine dispatch is the property; the test is the aggregation.
    /// </summary>
    [SkippableFact]
    public async Task Fan_out_map_subtasks_complete_across_multiple_machines()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        string[] seeds = ["alpha", "bravo", "charlie"];
        string[] targets = ["A", "B", "A"]; // round-robin → guaranteed cross-machine
        var tasks = new List<SessionId>();

        for (var i = 0; i < seeds.Length; i++)
        {
            var task = await rig.CreateSessionAsync($"map:{seeds[i]}", ct);
            await rig.DispatchToAsync(targets[i], ct);
            var seed = seeds[i];
            Assert.True(
                await FleetRig.WaitUntilAsync(async () => await rig.HasReportAsync(task, ct), Bound),
                $"map:{seed} never mailed a report. " + await rig.DiagnoseAsync(task, ct));
            tasks.Add(task);
        }

        // Every leaf reported the correct deterministic transform of its own seed.
        for (var i = 0; i < seeds.Length; i++)
            Assert.Equal($"map:{CollabProgram.MapTransform(seeds[i])}", await rig.ResultReferenceAsync(tasks[i], ct));

        // The fan-out really spanned more than one machine.
        var machinesUsed = tasks.Select(rig.MachineOf).Distinct().ToArray();
        Assert.True(machinesUsed.Length >= 2, $"fan-out landed on only {machinesUsed.Length} machine(s)");
    }

    /// <summary>
    /// Scenario 4 — cross-machine data query. Machine A serves a seeded mock datastore;
    /// machine B opens a forward and fetches the seeded row across the relay. B reporting
    /// the exact seeded value is the proof the query reached A's store.
    /// </summary>
    [SkippableFact]
    public async Task Data_query_fetches_a_seeded_row_from_a_store_on_another_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        var serve = await rig.CreateSessionAsync("datastore-serve", ct);
        await rig.DispatchToAsync("A", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(() => rig.ServiceExistsAsync("db", ct), Bound),
            "machine A never registered the datastore service. " + await rig.DiagnoseAsync(serve, ct));

        var query = await rig.CreateSessionAsync("db-query", ct);
        await rig.DispatchToAsync("B", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.HasReportAsync(query, ct), Bound),
            "machine B never mailed a report on db-query. " + await rig.DiagnoseAsync(query, ct));

        Assert.Equal($"row:{CollabProgram.DatastoreSeedValue}", await rig.ResultReferenceAsync(query, ct));
        Assert.Equal("A", rig.MachineOf(serve));
        Assert.Equal("B", rig.MachineOf(query));
    }

}
