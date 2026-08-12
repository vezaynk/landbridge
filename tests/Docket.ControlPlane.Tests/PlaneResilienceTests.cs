using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Docket.ControlPlane.Tests;

/// <summary>
/// The two control-plane resilience defects the §17.8 chaos suite found, at unit grain.
///
/// <para><b>#86 — a plane restart stranded in-flight tasks.</b> Dispatch→machine tracking
/// lives only in <see cref="RunnerConnectionRegistry"/>, so a restarted plane came back
/// with an empty registry while its machines were still working: the machine's
/// <c>alive</c>/progress events were dropped for a task nothing tracked, and
/// <see cref="DispatchService.CheckLivenessAsync"/> — which only walks what is tracked —
/// left the task in <c>working</c> under no clock at all, forever.
/// <see cref="DispatchService.RehydrateMachineAsync"/> re-adopts a reconnecting machine's
/// dispatches from committed state. Covered here: which tasks the query returns
/// (<see cref="TaskStore.HeldDispatchesOnAsync"/>), the §9.14 instance fencing that keeps
/// it from re-adopting the wrong dispatch, and the clock initialization that keeps
/// re-adoption from either requeueing healthy work instantly or never at all.</para>
///
/// <para><b>#87 — a disconnect could cost a task two requeues.</b> The endpoint requeued a
/// dropped machine's tasks before unregistering it, so the requeue's own <c>pg_notify</c>
/// woke a dispatch pass while the dead connection was still registered and ready; the pass
/// claimed a task onto the corpse socket and burned a second requeue as
/// <see cref="LivenessLossReason.AckTimeout"/>. The order is now unregister-then-requeue.
/// Covered here as an ordering property, in both directions: the fixed order costs one
/// requeue, and the old order is characterized so the assertion is demonstrably not
/// vacuous.</para>
///
/// <para><b>#84 — a requeue abandoned the task but not the process.</b> A liveness-loss
/// requeue revoked the attempt's authorization and freed the task but said nothing to the
/// machine, so the wedged-but-alive harness it was reclaiming kept running and kept spending
/// model tokens. The scan now follows the requeue with a <c>kill</c> where the machine is
/// still connected. Covered here as an ordering property — the kill is issued only after the
/// requeue has committed, asserted by reading committed state from inside the send itself —
/// plus the consequence-free failure path, and the reason that ordering needs a plane-side
/// memory: the runner reports the plane's own kill as an ordinary <c>exited</c> naming only
/// the task, so an echo arriving after a redispatch would requeue the successor attempt.</para>
///
/// <para><b>#94 — overlapping <c>/runner</c> connections corrupted tracking.</b> Register
/// replaced a machine's entry outright and teardown unregistered by machine id, so a
/// superseded endpoint's cleanup dropped the connection that had replaced it — requeueing a
/// running machine's tasks and leaving its live socket registered nowhere. Connections now
/// carry an identity minted at register, and teardown acts only on its own. Covered here:
/// the teardown no-op and what survives it, that a superseded socket can no longer steer
/// readiness, and the replace-then-rehydrate interaction that decides what happens to the
/// old connection's tracked dispatches.</para>
///
/// <para>A dispatch pass stands in for the <c>pg_notify</c> wake throughout — the pass IS
/// what the notify runs, and driving it directly makes the race deterministic instead of
/// leaving these tests to catch a timing window that only fires sometimes.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlaneResilienceTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(30);

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── #86: what a reconnecting machine re-adopts ───────────────────────────────

    [SkippableFact]
    public async Task Reconnect_re_adopts_a_working_task_the_machine_still_holds()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");

        // A restarted plane: a fresh registry that has never heard of this task, and a
        // machine dialing back in that is still running it.
        var registry = ReconnectedMachine(clock, "m1");
        var adopted = await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default);

        Assert.Equal(1, adopted);
        Assert.Contains(seeded.Task, registry.TasksOn("m1"));
        // The lease is held again, which is what the §11 answer path and the forward
        // orchestrator resolve a task's machine through.
        Assert.Equal("m1", registry.MachineFor(seeded.Task));
        Assert.True(registry.IsLeaseHeld(seeded.Task));
    }

    [SkippableFact]
    public async Task Reconnect_re_adopts_a_blocked_task_so_the_wait_ttl_sweeper_can_resolve_its_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        await BlockOnInputAsync(clock, seeded);

        // §11: a blocked task's harness process is expected to be gone, but the machine
        // still holds its lease until the sweeper parks it or the machine dies — and the
        // sweeper finds that machine only through the registry. So blocked_on_input has to
        // be re-adopted too, or a task that outlives a restart while waiting on a human is
        // never parked on TTL and never requeued on machine death. WaitTtlSweeper carried a
        // comment marking exactly this hole.
        var registry = ReconnectedMachine(clock, "m1");
        Assert.Equal(1, await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default));
        Assert.Equal("m1", registry.MachineFor(seeded.Task));

        // And the sweeper now does its job: the wait TTL lapses on a live machine, so the
        // task parks rather than sitting blocked forever.
        clock.Advance(WaitTtlSweeper.DefaultWaitTtl + TimeSpan.FromMinutes(1));
        registry.ApplyHeartbeat("m1", Heartbeat("m1", "default")); // still live at the new now
        await NewSweeper(clock, registry).SweepAsync(default);

        Assert.Equal(TaskState.Parked, await StateAsync(clock, seeded.Task));
    }

    [SkippableFact]
    public async Task Reconnect_does_not_adopt_a_task_whose_incumbent_instance_runs_on_another_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var mine = await SeedWorkingAsync(clock, "m1");
        var theirs = await SeedWorkingAsync(clock, "m2");

        // §9.14 fencing: the instance row records the machine its dispatch was minted for,
        // so re-adoption is per-machine. If m1 could adopt m2's task, m1's clocks would
        // govern work it is not running and the sweeper would resolve the wrong machine for
        // a park record.
        var registry = ReconnectedMachine(clock, "m1");
        Assert.Equal(1, await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default));

        Assert.Contains(mine.Task, registry.TasksOn("m1"));
        Assert.DoesNotContain(theirs.Task, registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task Reconnect_does_not_adopt_a_task_whose_instance_a_requeue_already_revoked()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");

        // The task was already freed — requeued back to submitted, its incumbent instance
        // revoked and cleared off the row. Re-adopting it would resurrect a dispatch that
        // no longer exists, and (with #87's ordering) would double-count a flapping
        // machine's disconnect. The query keys on the live current instance precisely so
        // this case falls out.
        await RequeueAsync(clock, seeded.Task, LivenessLossReason.MachineReboot);
        Assert.Equal(TaskState.Submitted, await StateAsync(clock, seeded.Task));

        var registry = ReconnectedMachine(clock, "m1");
        Assert.Equal(0, await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default));
        Assert.Empty(registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task Reconnect_does_not_adopt_a_task_that_has_left_the_working_states()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");

        // Reported result while the plane was down: the dispatch is over, the worker is
        // finished, and the task is waiting on a Lead — not on a clock. Re-adopting it
        // would put a verifying task back under the liveness scan.
        await ReportResultAsync(clock, seeded);
        Assert.Equal(TaskState.Verifying, await StateAsync(clock, seeded.Task));

        var registry = ReconnectedMachine(clock, "m1");
        Assert.Equal(0, await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default));
    }

    // ── #86: the clocks a re-adopted task comes back under ───────────────────────

    [SkippableFact]
    public async Task Re_adopted_clocks_start_at_connect_time_not_at_the_stale_dispatch_time()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");

        // The plane was down far longer than the aliveness window — the interesting case,
        // because the plane heard nothing for all of it. Carrying a pre-restart timestamp
        // into the re-adopted clocks would requeue this task on the very first scan,
        // punishing healthy work for the plane's own downtime; leaving the clocks unset
        // would restore tracking without restoring liveness. Both clocks start now.
        clock.Advance(Window * 10);
        var registry = ReconnectedMachine(clock, "m1");
        var connectedAt = clock.GetUtcNow();
        await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default);

        var tracked = Assert.Single(registry.AllTracked());
        Assert.Equal(seeded.Task, tracked.Task);
        Assert.Equal(connectedAt, tracked.LastActivity);
        Assert.Equal(connectedAt, tracked.LastProgress);

        // So the scan that runs immediately after the reconnect leaves it alone.
        await NewDispatch(clock, registry).CheckLivenessAsync(default);
        Assert.Equal(TaskState.Working, await StateAsync(clock, seeded.Task));
    }

    [SkippableFact]
    public async Task A_re_adopted_task_is_back_under_both_liveness_clocks()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = ReconnectedMachine(clock, "m1");
        await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default);

        // Half one: the machine's alive events land again. Before re-adoption they were
        // dropped for an untracked task, so this walk would have left the clocks frozen.
        StayAliveFor(clock, registry, seeded.Task, Window * 4);
        await NewDispatch(clock, registry).CheckLivenessAsync(default);
        Assert.Equal(TaskState.Working, await StateAsync(clock, seeded.Task));

        // Half two: and silence still reclaims it. This is the half that strands without
        // the fix — no clock covered the task, so it sat in working forever.
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(default);

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, seeded.Task));
        Assert.Equal(LivenessLossReason.LivenessTimeout, await LastReasonAsync(seeded.Task));
    }

    // ── #87: unregister before requeue ───────────────────────────────────────────

    [Fact]
    public void Unregister_returns_what_the_connection_held_and_takes_the_machine_out_of_dispatch()
    {
        var clock = new FakeTimeProvider();
        var registry = new RunnerConnectionRegistry(clock);
        var first = TaskId.New();
        var second = TaskId.New();
        var connection = registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(connection.Token, Heartbeat("m1", "default"));
        registry.TrackDispatch("m1", first);
        registry.TrackDispatch("m1", second);

        var teardown = registry.Unregister(connection.Token);

        // Both facts in one step: the caller learns what to requeue, and the machine is
        // already undispatchable — which is what makes requeueing second safe.
        Assert.True(teardown.Unregistered);
        Assert.Equal(2, teardown.Held.Count);
        Assert.Contains(first, teardown.Held);
        Assert.Contains(second, teardown.Held);
        Assert.Empty(registry.ReadyMachines());
        Assert.Null(registry.SnapshotFor("m1"));
        Assert.Empty(registry.TasksOn("m1"));
        // Asking a second time yields nothing, so a double teardown cannot double-requeue.
        var again = registry.Unregister(connection.Token);
        Assert.False(again.Unregistered);
        Assert.Empty(again.Held);
    }

    [SkippableFact]
    public async Task A_disconnect_costs_the_task_exactly_one_requeue()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var (registry, connection) = DeadSocketMachine(clock, "m1", seeded.Task);
        var sink = NewSink(clock, registry);

        // The endpoint's teardown order, as shipped: unregister, then requeue.
        var teardown = registry.Unregister(connection);
        await sink.HandleDisconnectAsync("m1", teardown.Held, default);

        // The requeue's pg_notify wake. The machine is gone, so the pass has nowhere to
        // put the task and it stays submitted for a live machine to claim later.
        await NewDispatch(clock, registry).RunDispatchPassAsync(default);

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, seeded.Task));
        Assert.Equal(
            [LivenessLossReason.MachineReboot],
            await RequeueReasonsAsync(seeded.Task));
        Assert.Equal(1, await RequeueCountAsync(seeded.Task));
    }

    [SkippableFact]
    public async Task Requeueing_before_unregistering_is_what_cost_the_second_requeue()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var (registry, connection) = DeadSocketMachine(clock, "m1", seeded.Task);
        var sink = NewSink(clock, registry);

        // #87 as it was: requeue while the dying connection is still registered and
        // flagged ready. This test exists to prove the assertion above is load-bearing —
        // it pins the defect to the ORDER and nothing else, since the only difference from
        // the previous test is which of these two lines runs first.
        await sink.HandleDisconnectAsync("m1", registry.TasksOn("m1"), default);
        await NewDispatch(clock, registry).RunDispatchPassAsync(default);
        registry.Unregister(connection);

        // The pass claimed the just-requeued task onto the corpse socket, the send failed,
        // and the task paid a second requeue against its §9 check 7 cap.
        Assert.Equal(
            [LivenessLossReason.MachineReboot, LivenessLossReason.AckTimeout],
            await RequeueReasonsAsync(seeded.Task));
        Assert.Equal(2, await RequeueCountAsync(seeded.Task));
    }

    // ── The two fixes together ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_flapping_machine_pays_one_requeue_per_disconnect_and_re_tracks_cleanly()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var (registry, connection) = DeadSocketMachine(clock, "m1", seeded.Task);
        var sink = NewSink(clock, registry);

        // Drop: unregister-then-requeue (#87).
        await sink.HandleDisconnectAsync("m1", registry.Unregister(connection).Held, default);

        // Fast reconnect, before anything else could redispatch. Re-adoption (#86) must
        // find nothing to adopt: the disconnect already revoked this task's instance and
        // cleared it off the row, so adopting it here would resurrect a dead dispatch and
        // hand the machine a task the plane has already put back in the queue.
        var reconnected = LiveConnection(clock, registry, "m1");
        var dispatch = NewDispatch(clock, registry);
        Assert.Equal(0, await dispatch.RehydrateMachineAsync("m1", default));

        // It comes back the ordinary way instead — a fresh claim on a live socket.
        await dispatch.RunDispatchPassAsync(default);

        Assert.Equal(TaskState.Working, await StateAsync(clock, seeded.Task));
        Assert.Contains(seeded.Task, registry.TasksOn("m1"));
        Assert.Single(reconnected.Commands.OfType<DispatchCommand>());
        Assert.Equal(1, await RequeueCountAsync(seeded.Task));
        Assert.Equal([LivenessLossReason.MachineReboot], await RequeueReasonsAsync(seeded.Task));

        // And the new dispatch is a new incumbent instance, so the predecessor's token is
        // dead (§9.14) rather than two workers racing for one task.
        await using var db = pg.NewContext();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Value);
        Assert.NotEqual(seeded.Instance.Value, row.CurrentInstanceId);
        Assert.Equal(1, await db.WorkerInstances.CountAsync(w => w.TaskId == seeded.Task.Value && !w.Revoked));
    }

    // ── #84: a requeue takes the process down too ────────────────────────────────

    [SkippableFact]
    public async Task A_wedged_dispatch_is_killed_on_the_machine_that_is_still_holding_it()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        var connection = LiveConnection(clock, registry, "m1");
        registry.TrackDispatch("m1", seeded.Task);

        // The wedged-but-alive shape, which is the whole of #84: docketd keeps asserting the
        // process exists, so the aliveness clock never fires and only the progress ceiling
        // can reclaim the task — and the process it reclaims from is, by construction, still
        // running. Nothing before this told it to stop.
        StayAliveFor(clock, registry, seeded.Task, Ceiling + Window);
        await NewDispatch(clock, registry).CheckLivenessAsync(default);

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, seeded.Task));
        Assert.Equal(LivenessLossReason.NoProgress, await LastReasonAsync(seeded.Task));
        var kill = Assert.Single(connection.Commands.OfType<KillCommand>());
        Assert.Equal(seeded.Task, kill.Task);
    }

    [SkippableFact]
    public async Task The_kill_is_issued_only_once_the_requeue_has_committed()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);

        // The ordering, read from inside the send: what does committed state say at the
        // instant the kill goes out? `submitted` means the requeue had already landed, which
        // is the property — the plane's decision does not wait on the machine cooperating
        // (§10 best-effort commands), and the kill's own `exited` cannot arrive while the
        // task still looks working and requeue it as ProcessExited, burying the clock that
        // actually fired under the symptom (#73).
        TaskState? whenKilled = null;
        var conn = registry.Register("m1", Set("default"), async (command, _) =>
        {
            if (command is KillCommand k)
                whenKilled = await StateAsync(clock, k.Task);
        });
        registry.ApplyHeartbeat(conn.Token, Heartbeat("m1", "default"));
        registry.TrackDispatch("m1", seeded.Task);

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(default);

        Assert.Equal(TaskState.Submitted, whenKilled);
        Assert.Equal(1, await RequeueCountAsync(seeded.Task));
    }

    [SkippableFact]
    public async Task A_kill_that_cannot_be_delivered_changes_nothing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        // A machine whose socket has died but whose registration has not been dropped yet:
        // the write throws, so there is no channel to kill over. The requeue has already
        // committed by then, so the failure costs the task nothing — exactly the behaviour
        // before #84, which is the point: the kill is an improvement where it lands and
        // never a dependency.
        var (registry, _) = DeadSocketMachine(clock, "m1", seeded.Task);

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(default);

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, seeded.Task));
        Assert.Equal(1, await RequeueCountAsync(seeded.Task));
        Assert.Equal([LivenessLossReason.LivenessTimeout], await RequeueReasonsAsync(seeded.Task));
    }

    [SkippableFact]
    public async Task The_exited_the_planes_own_kill_produces_does_not_requeue_the_successor_attempt()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        LiveConnection(clock, registry, "m1");
        registry.TrackDispatch("m1", seeded.Task);
        var dispatch = NewDispatch(clock, registry);

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await dispatch.CheckLivenessAsync(default);
        // The requeue's own notify wake, which puts the task straight back onto the one
        // machine available — the ordinary outcome, and the reason the echo is dangerous.
        await dispatch.RunDispatchPassAsync(default);
        Assert.Equal(TaskState.Working, await StateAsync(clock, seeded.Task));

        // Now the kill lands. `exited` names only the task (the wire is frozen), so nothing
        // on this event says which attempt died — and the attempt it names is now a healthy
        // one. Without the plane remembering that it ordered this death, the successor is
        // requeued: a second requeue off the §9 check 7 cap for one liveness loss, and its
        // worker left running for a task put back in the queue.
        await NewSink(clock, registry).HandleAsync(
            new ExitedEvent(seeded.Task, ExitCode: 137, clock.GetUtcNow()));

        Assert.Equal(TaskState.Working, await StateAsync(clock, seeded.Task));
        Assert.Equal(1, await RequeueCountAsync(seeded.Task));
        // One requeue, still attributed to the clock that fired. Driven by the aliveness
        // clock here rather than the progress ceiling, because the suppression is a property
        // of the kill and not of which clock ordered it.
        Assert.Equal([LivenessLossReason.LivenessTimeout], await RequeueReasonsAsync(seeded.Task));
        // And the successor keeps its tracking, so it stays under both clocks. Untracking it
        // here would strand it in working with nothing watching — the #86 symptom by a new
        // route.
        Assert.Contains(seeded.Task, registry.TasksOn("m1"));
    }

    [SkippableFact]
    public async Task An_exit_the_plane_did_not_order_still_requeues_once_the_echo_window_lapses()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        LiveConnection(clock, registry, "m1");
        registry.TrackDispatch("m1", seeded.Task);
        var dispatch = NewDispatch(clock, registry);

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await dispatch.CheckLivenessAsync(default);
        await dispatch.RunDispatchPassAsync(default);

        // Long past the echo window: the plane can no longer tell a very late echo from the
        // genuine death of the attempt now running, and a genuine death must still be heard.
        // This is what keeps the suppression above from being a permanent blind spot, and it
        // is also the plain no-kill behaviour — an `exited` for a working task requeues it.
        clock.Advance(DispatchService.CommandedExitEchoWindow + TimeSpan.FromSeconds(1));
        await NewSink(clock, registry).HandleAsync(
            new ExitedEvent(seeded.Task, ExitCode: 1, clock.GetUtcNow()));

        Assert.Equal(TaskState.Submitted, await StateAsync(clock, seeded.Task));
        Assert.Equal(
            [LivenessLossReason.LivenessTimeout, LivenessLossReason.ProcessExited],
            await RequeueReasonsAsync(seeded.Task));
    }

    // ── #94: one machine, two connections ────────────────────────────────────────

    [Fact]
    public void A_second_connection_supersedes_the_first_and_says_so()
    {
        var clock = new FakeTimeProvider();
        var registry = new RunnerConnectionRegistry(clock);

        var first = registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        Assert.False(first.SupersededLiveConnection);

        // The §17.8 closed-laptop shape: the machine dials in while the plane still believes
        // its previous socket is live. The flag is what the endpoint logs — a machine holding
        // two accepted connections is an operator fact, even though the registry copes.
        var second = registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        Assert.True(second.SupersededLiveConnection);
        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.Token.Generation, second.Token.Generation);
    }

    [Fact]
    public void A_superseded_connections_teardown_leaves_the_live_connection_registered()
    {
        var clock = new FakeTimeProvider();
        var registry = new RunnerConnectionRegistry(clock);
        var task = TaskId.New();
        var stale = registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(stale.Token, Heartbeat("m1", "default"));
        registry.TrackDispatch("m1", task);

        var live = LiveConnection(clock, registry, "m1");
        registry.TrackDispatch("m1", task);

        // The older endpoint's `finally`, running whenever its half-open socket finally
        // errors out. Unregistering by machine id — as it did — would remove the entry that
        // now belongs to the LIVE connection: its tasks requeued out from under a machine
        // still working them, and a live socket registered nowhere, invisible to dispatch
        // until it happened to reconnect. It must therefore be a no-op, and hand back nothing
        // to requeue.
        var teardown = registry.Unregister(stale.Token);
        Assert.False(teardown.Unregistered);
        Assert.Empty(teardown.Held);

        Assert.NotNull(registry.SnapshotFor("m1"));
        Assert.Contains("m1", registry.ReadyMachines());
        Assert.Contains(task, registry.TasksOn("m1"));

        // The live connection's own teardown still works normally, and still returns what it
        // held — #87's ordering is untouched by any of this.
        var real = registry.Unregister(live.Token);
        Assert.True(real.Unregistered);
        Assert.Equal([task], real.Held);
    }

    [Fact]
    public async Task A_superseded_connection_can_no_longer_steer_the_machine_or_receive_its_commands()
    {
        var clock = new FakeTimeProvider();
        var registry = new RunnerConnectionRegistry(clock);
        var staleReceived = new List<RunnerCommand>();
        var stale = registry.Register("m1", Set("default"),
            (cmd, _) => { staleReceived.Add(cmd); return Task.CompletedTask; });
        registry.ApplyHeartbeat(stale.Token, Heartbeat("m1", "default"));

        var live = LiveConnection(clock, registry, "m1");

        // A heartbeat still arriving on the stale socket — buffered, or a genuinely
        // overlapping pair — reported back-pressure. Applied by machine id, as it was, this
        // would mark the LIVE connection unready and quietly stop the machine taking work.
        registry.ApplyHeartbeat(
            stale.Token,
            new MachineHeartbeat("m1", Ready: false, UnderBackPressure: true,
                new SystemLoad(0, 0, 0), RunningTasks: 0, ["default"], DateTimeOffset.UtcNow));

        Assert.Contains("m1", registry.ReadyMachines());
        Assert.False(registry.SnapshotFor("m1")!.UnderBackPressure);

        // And the machine is reached through the connection that is actually carrying bytes.
        // This one stays machine-keyed on purpose: a send targets the machine, and the newest
        // connection is by definition the way to reach it.
        Assert.True(await registry.SendAsync("m1", new KillCommand(TaskId.New()), default));
        Assert.Empty(staleReceived);
        Assert.Single(live.Commands);
    }

    [SkippableFact]
    public async Task A_replacing_connection_re_derives_the_machines_dispatches_from_committed_state()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        var stale = registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(stale.Token, Heartbeat("m1", "default"));
        registry.TrackDispatch("m1", seeded.Task);

        // The reattach. Registering drops the stale connection's tracked dispatches, and
        // rehydration (#86) re-derives them — instance-fenced, so it re-adopts exactly what
        // this machine's live incumbents say it holds.
        clock.Advance(Window * 3);
        LiveConnection(clock, registry, "m1");
        var reconnectedAt = clock.GetUtcNow();
        Assert.Equal(1, await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default));

        // Exactly once, on exactly one connection: dispatches are keyed by task, so the two
        // steps cannot compound into a double-tracked task, and the entry that survives is
        // the one rehydration wrote.
        var tracked = Assert.Single(registry.AllTracked());
        Assert.Equal(seeded.Task, tracked.Task);
        Assert.Equal("m1", tracked.Machine);
        // Clocks stamped at the reattach, not carried from the stale connection — the same
        // choice re-adoption makes after a plane restart, and for the same reason: the clocks
        // measure the plane's own silence.
        Assert.Equal(reconnectedAt, tracked.LastActivity);
        Assert.Equal(reconnectedAt, tracked.LastProgress);

        // And the work was never interrupted, so nothing was requeued and the incumbent
        // instance is untouched — the running worker's token stays live (§9.14). An overlap
        // that churned the incumbent would kill a healthy worker's credential for nothing.
        Assert.Equal(TaskState.Working, await StateAsync(clock, seeded.Task));
        Assert.Equal(0, await RequeueCountAsync(seeded.Task));
        await using var db = pg.NewContext();
        var row = await db.Tasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Value);
        Assert.Equal(seeded.Instance.Value, row.CurrentInstanceId);
    }

    [SkippableFact]
    public async Task A_reattached_machine_keeps_working_when_its_half_open_socket_finally_tears_down()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        var stale = registry.Register("m1", Set("default"), (_, _) => Task.CompletedTask);
        registry.ApplyHeartbeat(stale.Token, Heartbeat("m1", "default"));
        registry.TrackDispatch("m1", seeded.Task);

        var live = LiveConnection(clock, registry, "m1");
        var dispatch = NewDispatch(clock, registry);
        Assert.Equal(1, await dispatch.RehydrateMachineAsync("m1", default));

        // The half-open socket errors out at last and its endpoint runs the full teardown it
        // always ran: unregister, then requeue whatever came back (#87). The whole scenario
        // §17.8 calls "close a laptop and reattach", end to end at unit grain.
        var sink = NewSink(clock, registry);
        var teardown = registry.Unregister(stale.Token);
        await sink.HandleDisconnectAsync("m1", teardown.Held, default);
        await dispatch.RunDispatchPassAsync(default);

        // Nothing moved: the task is still working on a machine that never stopped working
        // it, at no cost against its requeue cap, and the machine is still dispatchable.
        Assert.Equal(TaskState.Working, await StateAsync(clock, seeded.Task));
        Assert.Equal(0, await RequeueCountAsync(seeded.Task));
        Assert.Empty(await RequeueReasonsAsync(seeded.Task));
        Assert.Contains(seeded.Task, registry.TasksOn("m1"));
        Assert.Contains("m1", registry.ReadyMachines());
        // The live connection was handed no work it did not already have — the dispatch pass
        // found nothing eligible, because the task it might have claimed is still working.
        Assert.Empty(live.Commands.OfType<DispatchCommand>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private readonly record struct Seeded(TaskId Task, WorkerInstanceId Instance, TeamId Team);

    /// <summary>Create → dispatch, leaving the task <c>working</c> on the machine with a
    /// live worker instance minted for it — the shape a plane restart interrupts.</summary>
    private async Task<Seeded> SeedWorkingAsync(TimeProvider clock, string machineId)
    {
        await using var db = pg.NewContext();
        var store = new TaskStore(db, clock);
        var team = TeamId.New();
        await store.CreateAsync(new CreateTask(
            new LeadClaim(team), team, "completion criteria", CompletionMode.Lead, null));
        var instance = WorkerInstanceId.New();
        var applied = (StoreResult.Applied)await store.DispatchNextAsync(
            new MachineSnapshot(machineId, Ready: true, UnderBackPressure: false, Set("default")), instance);
        return new Seeded(applied.Task.Id, instance, team);
    }

    /// <summary>
    /// A machine dialing back into a plane that has never heard of it: registered and
    /// ready, tracking nothing. This is exactly the registry state a restarted plane has
    /// when <c>docketd</c> reconnects — the whole of #86.
    /// </summary>
    private static RunnerConnectionRegistry ReconnectedMachine(TimeProvider clock, string machineId)
    {
        var registry = new RunnerConnectionRegistry(clock);
        LiveConnection(clock, registry, machineId);
        return registry;
    }

    /// <summary>One registered connection: the token its endpoint would hold, and the
    /// commands the plane sends down it.</summary>
    private readonly record struct Wired(
        RunnerConnectionRegistry.ConnectionToken Token, List<RunnerCommand> Commands);

    /// <summary>Registers a working socket on an existing registry, returning its identity
    /// and the commands it receives.</summary>
    private static Wired LiveConnection(
        TimeProvider clock, RunnerConnectionRegistry registry, string machineId)
    {
        var captured = new List<RunnerCommand>();
        var connection = registry.Register(
            machineId, Set("default"), (cmd, _) => { captured.Add(cmd); return Task.CompletedTask; });
        registry.ApplyHeartbeat(connection.Token, Heartbeat(machineId, "default"));
        return new Wired(connection.Token, captured);
    }

    /// <summary>
    /// A machine whose socket is dead but whose registration has not been dropped yet —
    /// the SIGKILLed daemon a moment after the kill. The send delegate throws, which
    /// <see cref="RunnerConnectionRegistry.SendAsync"/> turns into the false that makes a
    /// dispatch pass requeue as <see cref="LivenessLossReason.AckTimeout"/>.
    /// </summary>
    private static (RunnerConnectionRegistry Registry, RunnerConnectionRegistry.ConnectionToken Connection)
        DeadSocketMachine(TimeProvider clock, string machineId, TaskId task)
    {
        var registry = new RunnerConnectionRegistry(clock);
        var connection = registry.Register(machineId, Set("default"),
            (_, _) => throw new IOException("the socket is gone"));
        registry.ApplyHeartbeat(connection.Token, Heartbeat(machineId, "default"));
        registry.TrackDispatch(machineId, task);
        return (registry, connection.Token);
    }

    private DispatchService NewDispatch(TimeProvider clock, RunnerConnectionRegistry registry) =>
        new(ScopeFactory(clock), registry, clock, NullLogger<DispatchService>.Instance,
            listener: null, livenessWindow: Window, publicMcpUrl: null, noProgressCeiling: Ceiling);

    private RunnerEventSink NewSink(TimeProvider clock, RunnerConnectionRegistry registry) =>
        new(ScopeFactory(clock), registry, new ForwardWaiters(), new TranscriptWaiters(),
            new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);

    private WaitTtlSweeper NewSweeper(TimeProvider clock, RunnerConnectionRegistry registry) =>
        new(ScopeFactory(clock), registry, clock, NullLogger<WaitTtlSweeper>.Instance);

    private static void StayAliveFor(
        FakeTimeProvider clock, RunnerConnectionRegistry registry, TaskId task, TimeSpan total)
    {
        var beat = TimeSpan.FromSeconds(15);
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += beat)
        {
            clock.Advance(beat);
            registry.RecordAlive(task);
        }
    }

    /// <summary>Takes a working task into blocked_on_input as its own incumbent worker.</summary>
    private async Task BlockOnInputAsync(TimeProvider clock, Seeded seeded)
    {
        await using var db = pg.NewContext();
        await new TaskStore(db, clock).ApplyAsync(
            seeded.Task,
            new RequestInput(
                new WorkerCaller(seeded.Team, seeded.Task, seeded.Instance), InputRequestKind.Question));
    }

    /// <summary>The worker reports a result, taking the task to verifying (§7).</summary>
    private async Task ReportResultAsync(TimeProvider clock, Seeded seeded)
    {
        await using var db = pg.NewContext();
        Assert.IsType<StoreResult.Applied>(await new TaskStore(db, clock).ApplyAsync(
            seeded.Task,
            new ReportResult(
                new WorkerCaller(seeded.Team, seeded.Task, seeded.Instance), "ref", "done")));
    }

    private async Task RequeueAsync(TimeProvider clock, TaskId task, LivenessLossReason reason)
    {
        await using var db = pg.NewContext();
        Assert.IsType<StoreResult.Applied>(
            await new TaskStore(db, clock).ApplyAsync(task, new LivenessLost(reason)));
    }

    private async Task<TaskState?> StateAsync(TimeProvider clock, TaskId id)
    {
        await using var db = pg.NewContext();
        return await new TaskStore(db, clock).GetStateAsync(id);
    }

    private async Task<LivenessLossReason?> LastReasonAsync(TaskId id)
    {
        await using var db = pg.NewContext();
        return await db.Tasks.AsNoTracking()
            .Where(t => t.Id == id.Value).Select(t => t.LastRequeueReason).SingleAsync();
    }

    private async Task<int> RequeueCountAsync(TaskId id)
    {
        await using var db = pg.NewContext();
        return await db.Tasks.AsNoTracking()
            .Where(t => t.Id == id.Value).Select(t => t.InfrastructureRequeues).SingleAsync();
    }

    /// <summary>The ordered requeue trail off the event log — the same durable read the
    /// chaos suite asserts over (#73).</summary>
    private async Task<IReadOnlyList<LivenessLossReason?>> RequeueReasonsAsync(TaskId id)
    {
        await using var db = pg.NewContext();
        return await db.TaskEvents.AsNoTracking()
            .Where(e => e.TaskId == id.Value && e.Kind == nameof(LivenessLost))
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.LivenessReason)
            .ToListAsync();
    }

    private IServiceScopeFactory ScopeFactory(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<DocketDbContext>(o =>
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention());
        services.AddDocketStore();
        services.AddScoped<TokenService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static MachineHeartbeat Heartbeat(string machineId, params string[] profiles) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningTasks: 0, profiles, DateTimeOffset.UtcNow);
}
