using System.Data.Common;
using Landbridge.Contracts;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Landbridge.ControlPlane.Tests;

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
/// (<see cref="SessionStore.HeldDispatchesOnAsync"/>), the §9.14 instance fencing that keeps
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
/// <para><b>#147 — the liveness scan could steal a task that had just moved on.</b> The scan
/// reads a task's state, decides, and then applies a <see cref="LivenessLost"/> — separate
/// round trips with nothing tying them together, while the engine accepted that command from
/// <c>blocked_on_input</c> as readily as from <c>working</c> and it named no attempt. So a
/// permission request committing in that window (which parks the task and deliberately keeps
/// its incumbent, because the asking worker is alive inside the tool call it is waiting in,
/// §11) was requeued anyway — against §9 check 7 — and the kill that follows a requeue (#84)
/// went out to that live worker; a requeue-plus-redispatch in the same window cost the
/// successor the same. The command now carries the attempt the scan judged and the engine
/// applies it only while that attempt is still working the task (§9 check 14). Covered here
/// with the window forced open at the scan's own read, in both directions, plus the refusal's
/// consequence for tracking — a refused requeue must not untrack, or the task it left alone is
/// left under no clock.</para>
///
/// <para><b>A failing dispatch pass could stop dispatch for the process's life.</b> The
/// loop's only error handling wrapped the whole <c>await foreach</c>, so one throw from one
/// pass was logged once and <c>LoopAsync</c> returned — nothing restarts it, and the plane
/// went on accepting tasks and reporting itself healthy while every submission sat in
/// <c>submitted</c>. A throw between the committed submitted→working claim and the send (the
/// token mint is a database write) additionally stranded that task working AND untracked, so
/// no clock covered it either. Containment is now per pass, and the mint/send region requeues
/// on a throw exactly as it does on a failed send. Covered here in both halves: the requeue,
/// and that the loop still serves the next wake.</para>
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
        Assert.Contains(seeded.Session, registry.SessionsOn("m1"));
        // The lease is held again, which is what the §11 answer path and the forward
        // orchestrator resolve a task's machine through.
        Assert.Equal("m1", registry.MachineFor(seeded.Session));
        Assert.True(registry.IsLeaseHeld(seeded.Session));
    }

    [SkippableFact]
    public async Task Reconnect_re_adopts_a_blocked_task_so_the_wait_ttl_sweeper_can_resolve_its_machine()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        await BlockOnInputAsync(clock, seeded);

        // §11: a blocked task's session is held on its machine, and the machine keeps its
        // lease until a Lead parks it or the machine dies — and the sweeper finds that
        // machine only through the registry. So blocked_on_input has to be re-adopted too,
        // or a task that outlives a restart while waiting on a human can be neither parked
        // nor requeued on machine death. WaitTtlSweeper carried a comment marking this hole.
        var registry = ReconnectedMachine(clock, "m1");
        Assert.Equal(1, await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default));
        Assert.Equal("m1", registry.MachineFor(seeded.Session));

        // And the sweeper can now act on it. The TTL is passed explicitly because the
        // default is infinite — a session is held until park_session, not until a timer — so
        // this exercises the opt-in auto-park an operator gets from Landbridge:WaitTtl. What the
        // re-adoption bought is the same either way: the sweeper resolved the machine.
        var waitTtl = TimeSpan.FromMinutes(30);
        clock.Advance(waitTtl + TimeSpan.FromMinutes(1));
        registry.ApplyHeartbeat("m1", Heartbeat("m1", "default")); // still live at the new now
        await NewSweeper(clock, registry, waitTtl).SweepAsync(default);

        Assert.Equal(SessionState.Parked, await StateAsync(clock, seeded.Session));
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

        Assert.Contains(mine.Session, registry.SessionsOn("m1"));
        Assert.DoesNotContain(theirs.Session, registry.SessionsOn("m1"));
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
        await RequeueAsync(clock, seeded.Session, LivenessLossReason.MachineReboot);
        Assert.Equal(SessionState.Failed, await StateAsync(clock, seeded.Session));

        var registry = ReconnectedMachine(clock, "m1");
        Assert.Equal(0, await NewDispatch(clock, registry).RehydrateMachineAsync("m1", default));
        Assert.Empty(registry.SessionsOn("m1"));
    }

    [SkippableFact]
    public async Task Reconnect_does_not_adopt_a_task_that_has_left_the_working_states()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");

        // Lead accepted while the plane was down: hidden, occupancy released. Re-adopting
        // it would put a finished session back under the liveness scan.
        await ReportResultAsync(clock, seeded);
        await using (var db = pg.NewContext())
            Assert.IsType<StoreResult.Applied>(await new SessionStore(db, clock).ApplyAsync(
                seeded.Session, new VerdictAccept(new LeadClaim(seeded.Team))));
        Assert.Equal(SessionState.Completed, await StateAsync(clock, seeded.Session));

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
        Assert.Equal(seeded.Session, tracked.Session);
        Assert.Equal(connectedAt, tracked.LastActivity);
        Assert.Equal(connectedAt, tracked.LastProgress);

        // So the scan that runs immediately after the reconnect leaves it alone.
        await NewDispatch(clock, registry).CheckLivenessAsync(default);
        Assert.Equal(SessionState.Working, await StateAsync(clock, seeded.Session));
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
        StayAliveFor(clock, registry, seeded.Session, Window * 4);
        await NewDispatch(clock, registry).CheckLivenessAsync(default);
        Assert.Equal(SessionState.Working, await StateAsync(clock, seeded.Session));

        // Half two: and silence still reclaims it. This is the half that strands without
        // the fix — no clock covered the task, so it sat in working forever.
        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(default);

        Assert.Equal(SessionState.Failed, await StateAsync(clock, seeded.Session));
        Assert.Equal(LivenessLossReason.LivenessTimeout, await LastReasonAsync(seeded.Session));
    }

    // ── #87: unregister before requeue ───────────────────────────────────────────

    [Fact]
    public void Unregister_returns_what_the_connection_held_and_takes_the_machine_out_of_dispatch()
    {
        var clock = new FakeTimeProvider();
        var registry = new RunnerConnectionRegistry(clock);
        var first = SessionId.New();
        var second = SessionId.New();
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
        Assert.Empty(registry.SessionsOn("m1"));
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
        var (registry, connection) = DeadSocketMachine(clock, "m1", seeded.Session);
        var sink = NewSink(clock, registry);

        // The endpoint's teardown order, as shipped: unregister, then requeue.
        var teardown = registry.Unregister(connection);
        await sink.HandleDisconnectAsync("m1", teardown.Held, default);

        // The requeue's pg_notify wake. The machine is gone, so the pass has nowhere to
        // put the task and it stays submitted for a live machine to claim later.
        await NewDispatch(clock, registry).RunDispatchPassAsync(default);

        Assert.Equal(SessionState.Failed, await StateAsync(clock, seeded.Session));
        Assert.Equal(
            [LivenessLossReason.MachineReboot],
            await RequeueReasonsAsync(seeded.Session));
        Assert.Equal(1, await RequeueCountAsync(seeded.Session));
    }

    [SkippableFact]
    public async Task Requeueing_before_unregistering_is_what_cost_the_second_requeue()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var (registry, connection) = DeadSocketMachine(clock, "m1", seeded.Session);
        var sink = NewSink(clock, registry);

        // #87 as it was: requeue while the dying connection is still registered and
        // flagged ready. This test exists to prove the assertion above is load-bearing —
        // it pins the defect to the ORDER and nothing else, since the only difference from
        // the previous test is which of these two lines runs first.
        await sink.HandleDisconnectAsync("m1", registry.SessionsOn("m1"), default);
        await NewDispatch(clock, registry).RunDispatchPassAsync(default);
        registry.Unregister(connection);

        // Failed is not claimable, so the old double-requeue onto a corpse socket
        // cannot happen: one loss, one fail-park.
        Assert.Equal(SessionState.Failed, await StateAsync(clock, seeded.Session));
        Assert.Equal(
            [LivenessLossReason.MachineReboot],
            await RequeueReasonsAsync(seeded.Session));
        Assert.Equal(1, await RequeueCountAsync(seeded.Session));
    }

    // ── The two fixes together ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_flapping_machine_pays_one_requeue_per_disconnect_and_re_tracks_cleanly()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var (registry, connection) = DeadSocketMachine(clock, "m1", seeded.Session);
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

        await using (var wake = pg.NewContext())
            Assert.IsType<StoreResult.Applied>(
                await new SessionStore(wake, clock).ApplyAsync(seeded.Session, new WakeParked("retry")));

        // It comes back the ordinary way instead — a fresh claim on a live socket.
        await dispatch.RunDispatchPassAsync(default);

        Assert.Equal(SessionState.Working, await StateAsync(clock, seeded.Session));
        Assert.Contains(seeded.Session, registry.SessionsOn("m1"));
        Assert.Single(reconnected.Commands.OfType<DispatchCommand>());
        Assert.Equal(1, await RequeueCountAsync(seeded.Session));
        Assert.Equal([LivenessLossReason.MachineReboot], await RequeueReasonsAsync(seeded.Session));

        // And the new dispatch is a new incumbent instance, so the predecessor's token is
        // dead (§9.14) rather than two workers racing for one task.
        await using var db = pg.NewContext();
        var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == seeded.Session.Value);
        Assert.NotEqual(seeded.Instance.Value, row.CurrentInstanceId);
        Assert.Equal(1, await db.WorkerInstances.CountAsync(w => w.SessionId == seeded.Session.Value && !w.Revoked));
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
        registry.TrackDispatch("m1", seeded.Session);

        // The wedged-but-alive shape, which is the whole of #84: landbridged keeps asserting the
        // process exists, so the aliveness clock never fires and only the progress ceiling
        // can reclaim the task — and the process it reclaims from is, by construction, still
        // running. Nothing before this told it to stop.
        StayAliveFor(clock, registry, seeded.Session, Ceiling + Window);
        await NewDispatch(clock, registry).CheckLivenessAsync(default);

        Assert.Equal(SessionState.Failed, await StateAsync(clock, seeded.Session));
        Assert.Equal(LivenessLossReason.NoProgress, await LastReasonAsync(seeded.Session));
        var kill = Assert.Single(connection.Commands.OfType<KillCommand>());
        Assert.Equal(seeded.Session, kill.Session);
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
        SessionState? whenKilled = null;
        var conn = registry.Register("m1", Set("default"), async (command, _) =>
        {
            if (command is KillCommand k)
                whenKilled = await StateAsync(clock, k.Session);
        });
        registry.ApplyHeartbeat(conn.Token, Heartbeat("m1", "default"));
        registry.TrackDispatch("m1", seeded.Session);

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(default);

        Assert.Equal(SessionState.Failed, whenKilled);
        Assert.Equal(1, await RequeueCountAsync(seeded.Session));
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
        var (registry, _) = DeadSocketMachine(clock, "m1", seeded.Session);

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry).CheckLivenessAsync(default);

        Assert.Equal(SessionState.Failed, await StateAsync(clock, seeded.Session));
        Assert.Equal(1, await RequeueCountAsync(seeded.Session));
        Assert.Equal([LivenessLossReason.LivenessTimeout], await RequeueReasonsAsync(seeded.Session));
    }

    [SkippableFact]
    public async Task The_exited_the_planes_own_kill_produces_does_not_requeue_the_successor_attempt()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        LiveConnection(clock, registry, "m1");
        registry.TrackDispatch("m1", seeded.Session);
        var dispatch = NewDispatch(clock, registry);

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await dispatch.CheckLivenessAsync(default);
        await using (var wake = pg.NewContext())
            Assert.IsType<StoreResult.Applied>(
                await new SessionStore(wake, clock).ApplyAsync(seeded.Session, new WakeParked("retry")));
        // The Lead resume, which puts the task straight back onto the one
        // machine available — the ordinary outcome, and the reason the echo is dangerous.
        await dispatch.RunDispatchPassAsync(default);
        Assert.Equal(SessionState.Working, await StateAsync(clock, seeded.Session));

        // Now the kill lands. `exited` names only the task (the wire is frozen), so nothing
        // on this event says which attempt died — and the attempt it names is now a healthy
        // one. Without the plane remembering that it ordered this death, the successor is
        // requeued: a second requeue off the §9 check 7 cap for one liveness loss, and its
        // worker left running for a task put back in the queue.
        await NewSink(clock, registry).HandleAsync(
            new ExitedEvent(seeded.Session, ExitCode: 137, clock.GetUtcNow()));

        Assert.Equal(SessionState.Working, await StateAsync(clock, seeded.Session));
        Assert.Equal(1, await RequeueCountAsync(seeded.Session));
        // One requeue, still attributed to the clock that fired. Driven by the aliveness
        // clock here rather than the progress ceiling, because the suppression is a property
        // of the kill and not of which clock ordered it.
        Assert.Equal([LivenessLossReason.LivenessTimeout], await RequeueReasonsAsync(seeded.Session));
        // And the successor keeps its tracking, so it stays under both clocks. Untracking it
        // here would strand it in working with nothing watching — the #86 symptom by a new
        // route.
        Assert.Contains(seeded.Session, registry.SessionsOn("m1"));
    }

    [SkippableFact]
    public async Task An_exit_the_plane_did_not_order_still_requeues_once_the_echo_window_lapses()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        LiveConnection(clock, registry, "m1");
        registry.TrackDispatch("m1", seeded.Session);
        var dispatch = NewDispatch(clock, registry);

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await dispatch.CheckLivenessAsync(default);
        await using (var wake = pg.NewContext())
            Assert.IsType<StoreResult.Applied>(
                await new SessionStore(wake, clock).ApplyAsync(seeded.Session, new WakeParked("retry")));
        await dispatch.RunDispatchPassAsync(default);

        // Long past the echo window: the plane can no longer tell a very late echo from the
        // genuine death of the attempt now running, and a genuine death must still be heard.
        // This is what keeps the suppression above from being a permanent blind spot, and it
        // is also the plain no-kill behaviour — an `exited` for a working task requeues it.
        clock.Advance(DispatchService.CommandedExitEchoWindow + TimeSpan.FromSeconds(1));
        await NewSink(clock, registry).HandleAsync(
            new ExitedEvent(seeded.Session, ExitCode: 1, clock.GetUtcNow()));

        Assert.Equal(SessionState.Failed, await StateAsync(clock, seeded.Session));
        Assert.Equal(
            [LivenessLossReason.LivenessTimeout, LivenessLossReason.ProcessExited],
            await RequeueReasonsAsync(seeded.Session));
    }

    // ── #147: a liveness loss applies to the attempt it judged ───────────────────

    [SkippableFact]
    public async Task A_permission_request_committing_mid_scan_is_neither_requeued_nor_killed()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        var connection = LiveConnection(clock, registry, "m1");
        registry.TrackDispatch("m1", seeded.Session);

        // The worker asks for permission in the window between the scan's read and the requeue
        // that read decided on — the harness's own MCP call, committing at the one instant that
        // used to cost it its task. This is the request kind whose process does NOT exit (§11),
        // so the task parks with the same instance still on it.
        var raced = new CommitAfterTaskRead(async () =>
        {
            await using var db = pg.NewContext();
            Assert.IsType<StoreResult.Applied>(await new SessionStore(db, clock).ApplyAsync(
                seeded.Session,
                new RequestInput(
                    new WorkerCaller(seeded.Team, seeded.Session, seeded.Instance),
                    InputRequestKind.Permission, "run `rm -rf build`?", PermissionTool: "Bash")));
        });

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry, raced).CheckLivenessAsync(default);

        Assert.True(raced.Fired, "the scan's read of the task row was never intercepted");
        await using var verify = pg.NewContext();
        var row = await verify.Sessions.AsNoTracking().SingleAsync(t => t.Id == seeded.Session.Value);
        // Everything the permission bridge needs is untouched: the task is still waiting for a
        // verdict, it owes nothing to §9 check 7, and the worker holding the tool call open is
        // still the incumbent with a live token to answer through.
        Assert.Equal(SessionState.BlockedOnInput, row.State);
        Assert.Equal(0, row.InfrastructureRequeues);
        Assert.Equal(seeded.Instance.Value, row.CurrentInstanceId);
        Assert.Equal(1, await verify.WorkerInstances.CountAsync(
            w => w.SessionId == seeded.Session.Value && !w.Revoked));
        // And no kill — the process is not wedged, it is parked inside a tool call waiting for
        // an answer, which is the whole difference the fence draws.
        Assert.Empty(connection.Commands.OfType<KillCommand>());
        // A refused requeue leaves tracking alone: the wait-TTL sweeper resolves a blocked
        // task's machine through the registry (§11), so untracking here would strand it —
        // never parked on TTL, never requeued if the machine died.
        Assert.Contains(seeded.Session, registry.SessionsOn("m1"));
    }

    [SkippableFact]
    public async Task A_redispatched_successor_is_not_requeued_for_its_predecessors_silence()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var seeded = await SeedWorkingAsync(clock, "m1");
        var registry = new RunnerConnectionRegistry(clock);
        var connection = LiveConnection(clock, registry, "m1");
        registry.TrackDispatch("m1", seeded.Session);

        // The other move available in that window: something else requeues the task first (a
        // reboot announcement, a dropped socket) and the notify it commits redispatches it. By
        // the time the scan applies, the attempt it judged is gone and a fresh one is running.
        var successor = WorkerInstanceId.New();
        var raced = new CommitAfterTaskRead(async () =>
        {
            await using (var requeue = pg.NewContext())
            {
                var store = new SessionStore(requeue, clock);
                Assert.IsType<StoreResult.Applied>(await store
                    .ApplyAsync(seeded.Session, new LivenessLost(LivenessLossReason.MachineReboot)));
                Assert.IsType<StoreResult.Applied>(await store.ApplyAsync(seeded.Session, new WakeParked()));
            }
            await using var redispatch = pg.NewContext();
            Assert.IsType<StoreResult.Applied>(await new SessionStore(redispatch, clock).DispatchNextAsync(
                new MachineSnapshot("m1", Ready: true, UnderBackPressure: false, Set("default")),
                successor));
        });

        clock.Advance(Window + TimeSpan.FromSeconds(1));
        await NewDispatch(clock, registry, raced).CheckLivenessAsync(default);

        Assert.True(raced.Fired, "the scan's read of the task row was never intercepted");
        await using var verify = pg.NewContext();
        var row = await verify.Sessions.AsNoTracking().SingleAsync(t => t.Id == seeded.Session.Value);
        // One loss, one requeue — the injected one. The scan's own is refused rather than
        // charged to the task a second time, and the successor keeps working with its token.
        Assert.Equal(SessionState.Working, row.State);
        Assert.Equal(successor.Value, row.CurrentInstanceId);
        Assert.Equal(1, row.InfrastructureRequeues);
        Assert.Equal([LivenessLossReason.MachineReboot], await RequeueReasonsAsync(seeded.Session));
        Assert.False(await verify.WorkerInstances.AsNoTracking()
            .Where(w => w.Id == successor.Value).Select(w => w.Revoked).SingleAsync());
        Assert.Empty(connection.Commands.OfType<KillCommand>());
        Assert.Contains(seeded.Session, registry.SessionsOn("m1"));
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
        var task = SessionId.New();
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
        Assert.Contains(task, registry.SessionsOn("m1"));

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
                new SystemLoad(0, 0, 0), RunningSessions: 0, ["default"], DateTimeOffset.UtcNow));

        Assert.Contains("m1", registry.ReadyMachines());
        Assert.False(registry.SnapshotFor("m1")!.UnderBackPressure);

        // And the machine is reached through the connection that is actually carrying bytes.
        // This one stays machine-keyed on purpose: a send targets the machine, and the newest
        // connection is by definition the way to reach it.
        Assert.True(await registry.SendAsync("m1", new KillCommand(SessionId.New()), default));
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
        registry.TrackDispatch("m1", seeded.Session);

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
        Assert.Equal(seeded.Session, tracked.Session);
        Assert.Equal("m1", tracked.Machine);
        // Clocks stamped at the reattach, not carried from the stale connection — the same
        // choice re-adoption makes after a plane restart, and for the same reason: the clocks
        // measure the plane's own silence.
        Assert.Equal(reconnectedAt, tracked.LastActivity);
        Assert.Equal(reconnectedAt, tracked.LastProgress);

        // And the work was never interrupted, so nothing was requeued and the incumbent
        // instance is untouched — the running worker's token stays live (§9.14). An overlap
        // that churned the incumbent would kill a healthy worker's credential for nothing.
        Assert.Equal(SessionState.Working, await StateAsync(clock, seeded.Session));
        Assert.Equal(0, await RequeueCountAsync(seeded.Session));
        await using var db = pg.NewContext();
        var row = await db.Sessions.AsNoTracking().SingleAsync(t => t.Id == seeded.Session.Value);
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
        registry.TrackDispatch("m1", seeded.Session);

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
        Assert.Equal(SessionState.Working, await StateAsync(clock, seeded.Session));
        Assert.Equal(0, await RequeueCountAsync(seeded.Session));
        Assert.Empty(await RequeueReasonsAsync(seeded.Session));
        Assert.Contains(seeded.Session, registry.SessionsOn("m1"));
        Assert.Contains("m1", registry.ReadyMachines());
        // The live connection was handed no work it did not already have — the dispatch pass
        // found nothing eligible, because the task it might have claimed is still working.
        Assert.Empty(live.Commands.OfType<DispatchCommand>());
    }

    // ── A failing pass must not take the loop, or the claimed task, with it ──────

    [SkippableFact]
    public async Task A_mint_that_throws_after_the_claim_requeues_the_task_instead_of_stranding_it_working()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var task = await SeedSubmittedAsync(clock);
        var registry = new RunnerConnectionRegistry(clock);
        var connection = LiveConnection(clock, registry, "m1");

        // The worker token is a database write like any other, issued after the claim has
        // already committed submitted→working. Failing its insert is the whole scenario:
        // nothing is wrong with the task, the machine, or the send channel.
        var db = new FailingWrite { OnAdding = nameof(CredentialRow) };
        await NewDispatch(clock, registry, db).RunDispatchPassAsync(default);

        // So it takes the remedy a failed send takes (§10 best-effort commands), where before
        // the throw unwound out of the pass and left this row working with nothing working it
        // — and untracked, so no liveness clock would ever come back for it.
        Assert.Equal(SessionState.Failed, await StateAsync(clock, task));
        Assert.Equal([LivenessLossReason.AckTimeout], await RequeueReasonsAsync(task));
        Assert.Equal(1, await RequeueCountAsync(task));
        Assert.Empty(registry.SessionsOn("m1"));
        Assert.Empty(connection.Commands.OfType<DispatchCommand>());

        // And the requeue's own effects landed with it: the instance minted for the dead
        // attempt is revoked and off the row, so nothing holds a live credential for work
        // that never started (§9.14).
        await using var verify = pg.NewContext();
        Assert.Null((await verify.Sessions.AsNoTracking().SingleAsync(t => t.Id == task.Value)).CurrentInstanceId);
        Assert.Equal(0, await verify.WorkerInstances.CountAsync(w => w.SessionId == task.Value && !w.Revoked));
    }

    [SkippableFact]
    public async Task A_pass_that_throws_does_not_end_the_dispatch_loop()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        var clock = new FakeTimeProvider();
        var task = await SeedSubmittedAsync(clock);
        var registry = new RunnerConnectionRegistry(clock);
        var dispatched = new TaskCompletionSource<DispatchCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = registry.Register("m1", Set("default"), (command, _) =>
        {
            if (command is DispatchCommand d)
                dispatched.TrySetResult(d);
            return Task.CompletedTask;
        });
        registry.ApplyHeartbeat(connection.Token, Heartbeat("m1", "default"));

        // Break the claim itself — the instance-mint effect's insert, inside
        // DispatchNextAsync's own transaction. Deliberately a failure NO requeue path can
        // catch, so it unwinds all the way out of the pass: that is the shape that used to
        // end the loop, and a database blip during the startup backlog scan is enough.
        var db = new FailingWrite { OnAdding = nameof(WorkerInstanceRow) };
        var dispatch = NewDispatch(clock, registry, db);

        await dispatch.StartAsync(default);
        try
        {
            await WaitUntil(() => db.Failures > 0, "the startup backlog scan to fail");
            // The claim rolled back with the throw, so the task owes nothing for it.
            Assert.Equal(SessionState.Submitted, await StateAsync(clock, task));
            Assert.Equal(0, await RequeueCountAsync(task));

            // The blip passes and the next notify wake arrives. A loop that ended on the
            // throw never serves it: this task — and every task submitted afterwards — stays
            // in submitted for as long as the process lives, with one log line to say so.
            db.OnAdding = null;
            dispatch.Signal();

            Assert.Equal(task, (await WithTimeout(dispatched.Task, "a later dispatch")).Session);
            Assert.Equal(SessionState.Working, await StateAsync(clock, task));
        }
        finally
        {
            await dispatch.StopAsync(default);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A database that fails one chosen write. Standing in for the transient faults a real
    /// one has — the plane's own writes are the only moving part in the two tests above, and
    /// naming the entity being inserted picks which step of a dispatch breaks without
    /// touching the code under test.
    /// </summary>
    private sealed class FailingWrite : ISaveChangesInterceptor
    {
        /// <summary>The entity type whose insert throws, or null to let every write through.
        /// Settable mid-test, so a fault can clear the way a real one does.</summary>
        public string? OnAdding { get; set; }

        /// <summary>How many writes have been failed, which is how a test observes a
        /// background loop reaching the fault at all.</summary>
        public int Failures => _failures;

        private int _failures;

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (OnAdding is { } entity && eventData.Context!.ChangeTracker.Entries()
                    .Any(e => e.State == EntityState.Added && e.Metadata.ClrType.Name == entity))
            {
                Interlocked.Increment(ref _failures);
                throw new InvalidOperationException($"the {entity} insert failed");
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Commits a write in the window a read-then-apply sequence leaves open: the callback runs
    /// once, immediately after the liveness scan's own read of the task row has executed and
    /// before the transition that read decided on is applied. That window is the whole of #147,
    /// and forcing it open here is what makes the race a deterministic test rather than one
    /// that fails on a machine under load once a month. The callback commits on its own context
    /// and connection, so what it lands is exactly what a worker's MCP call — or another requeue
    /// path — would have landed at that instant.
    /// </summary>
    private sealed class CommitAfterTaskRead(Func<Task> commit) : DbCommandInterceptor
    {
        /// <summary>Whether the window was actually hit. Asserted by the tests, so a read this
        /// no longer recognizes fails them loudly instead of passing them vacuously.</summary>
        public bool Fired { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            // The scan's decision read is the projected state+incumbent pair
            // (SessionStore.GetIncumbentDispatchAsync); the transition's own read, which comes
            // after and must not be intercepted, loads the whole row — hence the second clause.
            if (!Fired
                && command.CommandText.Contains("current_instance_id", StringComparison.Ordinal)
                && !command.CommandText.Contains("completion_criteria", StringComparison.Ordinal))
            {
                Fired = true;
                await commit();
            }
            return result;
        }
    }

    /// <summary>Polls a condition on the real clock: the dispatch loop runs on its own task,
    /// so the only way to observe its progress is to wait for it.</summary>
    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    private static async Task<T> WithTimeout<T>(Task<T> pending, string what)
    {
        Assert.True(
            await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10))) == pending,
            $"timed out waiting for {what}");
        return await pending;
    }

    private readonly record struct Seeded(SessionId Session, WorkerInstanceId Instance, TeamId Team);

    /// <summary>Create only: a submitted task waiting for a dispatch pass to claim it.</summary>
    private async Task<SessionId> SeedSubmittedAsync(TimeProvider clock)
    {
        await using var db = pg.NewContext();
        var team = TeamId.New();
        var created = (StoreResult.Applied)await new SessionStore(db, clock).CreateAsync(new CreateSession(new LeadClaim(team), team, "completion criteria", "default"));
        return created.Session.Id;
    }

    /// <summary>Create → dispatch, leaving the task <c>working</c> on the machine with a
    /// live worker instance minted for it — the shape a plane restart interrupts.</summary>
    private async Task<Seeded> SeedWorkingAsync(TimeProvider clock, string machineId)
    {
        await using var db = pg.NewContext();
        var store = new SessionStore(db, clock);
        var team = TeamId.New();
        await store.CreateAsync(new CreateSession(new LeadClaim(team), team, "completion criteria", "default"));
        var instance = WorkerInstanceId.New();
        var applied = (StoreResult.Applied)await store.DispatchNextAsync(
            new MachineSnapshot(machineId, Ready: true, UnderBackPressure: false, Set("default")), instance);
        return new Seeded(applied.Session.Id, instance, team);
    }

    /// <summary>
    /// A machine dialing back into a plane that has never heard of it: registered and
    /// ready, tracking nothing. This is exactly the registry state a restarted plane has
    /// when <c>landbridged</c> reconnects — the whole of #86.
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
        DeadSocketMachine(TimeProvider clock, string machineId, SessionId task)
    {
        var registry = new RunnerConnectionRegistry(clock);
        var connection = registry.Register(machineId, Set("default"),
            (_, _) => throw new IOException("the socket is gone"));
        registry.ApplyHeartbeat(connection.Token, Heartbeat(machineId, "default"));
        registry.TrackDispatch(machineId, task);
        return (registry, connection.Token);
    }

    private DispatchService NewDispatch(
        TimeProvider clock, RunnerConnectionRegistry registry, IInterceptor? interceptor = null) =>
        new(ScopeFactory(clock, interceptor), registry, clock, NullLogger<DispatchService>.Instance,
            listener: null, livenessWindow: Window, publicMcpUrl: null, noProgressCeiling: Ceiling);

    private RunnerEventSink NewSink(TimeProvider clock, RunnerConnectionRegistry registry) =>
        new(ScopeFactory(clock), registry, new ForwardWaiters(), new TranscriptWaiters(),
            new ProcessControlRelay(registry), NullLogger<RunnerEventSink>.Instance);

    /// <summary>
    /// A sweeper for these tests. <paramref name="waitTtl"/> is null for the machine-death
    /// paths, which fire regardless — the wait TTL now defaults to infinite (a session is
    /// held until <c>park_session</c>, not until a timer), so a test about the TTL lapsing has
    /// to ask for one explicitly, exactly as an operator does with <c>Landbridge:WaitTtl</c>.
    /// </summary>
    private WaitTtlSweeper NewSweeper(
        TimeProvider clock, RunnerConnectionRegistry registry, TimeSpan? waitTtl = null) =>
        new(ScopeFactory(clock), registry, clock, NullLogger<WaitTtlSweeper>.Instance, waitTtl);

    private static void StayAliveFor(
        FakeTimeProvider clock, RunnerConnectionRegistry registry, SessionId task, TimeSpan total)
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
        await new SessionStore(db, clock).ApplyAsync(
            seeded.Session,
            new RequestInput(
                new WorkerCaller(seeded.Team, seeded.Session, seeded.Instance), InputRequestKind.Question));
    }

    /// <summary>The worker reports a result, taking the task to verifying (§7).</summary>
    private async Task ReportResultAsync(TimeProvider clock, Seeded seeded)
    {
        await using var db = pg.NewContext();
        Assert.IsType<StoreResult.Applied>(await new SessionStore(db, clock).ApplyAsync(
            seeded.Session,
            new ReportResult(
                new WorkerCaller(seeded.Team, seeded.Session, seeded.Instance), "ref", "done")));
    }

    private async Task RequeueAsync(TimeProvider clock, SessionId task, LivenessLossReason reason)
    {
        await using var db = pg.NewContext();
        Assert.IsType<StoreResult.Applied>(
            await new SessionStore(db, clock).ApplyAsync(task, new LivenessLost(reason)));
    }

    private async Task<SessionState?> StateAsync(TimeProvider clock, SessionId id)
    {
        await using var db = pg.NewContext();
        return await new SessionStore(db, clock).GetStateAsync(id);
    }

    private async Task<LivenessLossReason?> LastReasonAsync(SessionId id)
    {
        await using var db = pg.NewContext();
        return await db.Sessions.AsNoTracking()
            .Where(t => t.Id == id.Value).Select(t => t.LastRequeueReason).SingleAsync();
    }

    private async Task<int> RequeueCountAsync(SessionId id)
    {
        await using var db = pg.NewContext();
        return await db.Sessions.AsNoTracking()
            .Where(t => t.Id == id.Value).Select(t => t.InfrastructureRequeues).SingleAsync();
    }

    /// <summary>The ordered requeue trail off the event log — the same durable read the
    /// chaos suite asserts over (#73).</summary>
    private async Task<IReadOnlyList<LivenessLossReason?>> RequeueReasonsAsync(SessionId id)
    {
        await using var db = pg.NewContext();
        return await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == id.Value && e.Kind == nameof(LivenessLost))
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.LivenessReason)
            .ToListAsync();
    }

    private IServiceScopeFactory ScopeFactory(
        TimeProvider clock, IInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LandbridgeDbContext>(o =>
        {
            o.UseNpgsql(pg.ConnectionString).UseSnakeCaseNamingConvention();
            // Registered on the provider, so every scope the service opens for itself gets
            // it — including the fresh one a failed dispatch requeues through.
            if (interceptor is not null)
                o.AddInterceptors(interceptor);
        });
        services.AddLandbridgeStore();
        services.AddScoped<TokenService>();
        services.AddSingleton(clock);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IReadOnlySet<string> Set(params string[] names) =>
        new HashSet<string>(names, StringComparer.Ordinal);

    private static MachineHeartbeat Heartbeat(string machineId, params string[] profiles) =>
        new(machineId, Ready: true, UnderBackPressure: false,
            new SystemLoad(0, 0, 0), RunningSessions: 0, profiles, DateTimeOffset.UtcNow);
}
