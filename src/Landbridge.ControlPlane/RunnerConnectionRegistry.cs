using System.Collections.Concurrent;
using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// The in-memory registry of live runner connections, spec §10. Single-node v1:
/// landbridged only dials outbound, so a connection is a WebSocket the control plane
/// accepted and the send delegate that writes command frames back down it. Per
/// machine it holds that delegate, generation, tracked dispatches, and the two
/// liveness clocks §10 needs on those tasks.
///
/// Ready / profiles / processes / last-spoke live on <c>machines</c> and
/// <c>machine_processes</c> (heartbeat upsert). A non-guid test id ("m1") has
/// no row; <see cref="ApplyHeartbeat"/> keeps a small overlay so those tests
/// still drive dispatch without enrolling.

///
/// It is transport-agnostic — nothing here knows about WebSockets — so it is
/// driven directly in tests. All state is process-local and evaporates on restart,
/// but it is no longer lost: a reconnecting machine's dispatches are re-adopted from
/// committed state by <see cref="DispatchService.RehydrateMachineAsync"/> (§10, #86),
/// which is what lets a task survive a plane restart instead of stranding in
/// <c>working</c> with no clock over it.
///
/// <para><b>A machine is not a connection</b> (#94). One machine can briefly hold two
/// accepted <c>/runner</c> connections — a half-open socket the plane has not noticed
/// (a laptop closed and reattached, §17.8) plus the fresh one — so every operation here
/// is deliberately keyed one way or the other:</para>
/// <list type="bullet">
/// <item><b>Machine-keyed</b> — dispatch, tracking, sends, and every view. These target
/// the machine, and the newest connection is by definition the way to reach it, so
/// resolving through the current entry is the correct behaviour.</item>
/// <item><b>Connection-keyed</b>, on the <see cref="ConnectionToken"/> minted by
/// <see cref="Register"/> — teardown (<see cref="Unregister"/>) and folding in what that
/// socket reports about itself (<see cref="ApplyHeartbeat(ConnectionToken, MachineHeartbeat)"/>).
/// Both were machine-keyed before, which is precisely how a superseded endpoint's cleanup
/// unregistered the live connection that had replaced it — requeueing a running machine's
/// tasks and leaving its socket registered nowhere.</item>
/// </list>
/// </summary>
public sealed class RunnerConnectionRegistry(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, RunnerConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Overlay> _overlay = new(StringComparer.Ordinal);


    /// <summary>Mints <see cref="ConnectionToken.Generation"/>. Process-wide rather than
    /// per-machine so a token can never be mistaken for another machine's connection.</summary>
    private long _generations;

    /// <summary>
    /// Registers a dialed-in machine. Starts not-ready with no profiles; its first
    /// heartbeat supplies both (§10).
    ///
    /// <para>Latest connection wins: a machine that dials in while an earlier connection
    /// is still registered replaces it, because the new socket is the only one known to
    /// carry bytes. The returned <see cref="Registration"/> names this connection
    /// (<see cref="ConnectionToken"/>), which its endpoint must present at teardown so a
    /// superseded connection's cleanup cannot unregister its successor (#94).</para>
    ///
    /// <para><b>The replaced connection's tracked dispatches are dropped, not carried
    /// over</b>, and the new connection re-derives them from committed state through
    /// <see cref="DispatchService.RehydrateMachineAsync"/>. Committed state is the
    /// authority and is instance-fenced (§9.14,
    /// <see cref="SessionStore.HeldDispatchesOnAsync"/>): it re-adopts exactly the tasks whose
    /// live incumbent instance was minted for this machine. Carrying the old map over would
    /// instead preserve entries that fence rejects — a task requeued out from under this
    /// machine while the stale socket still listed it — leaving a dead dispatch tracked and
    /// under a liveness clock. There is no double-tracking either way: dispatches are keyed
    /// by task, so re-adoption overwrites rather than accumulates.</para>
    ///
    /// <para><paramref name="close"/> is how the plane hangs up on this connection from
    /// somewhere other than its own endpoint — <see cref="DisconnectAsync"/>, which
    /// machine revocation needs (§13). It is optional because a caller driving the
    /// registry with no socket underneath it (every test, the in-process rigs) has
    /// nothing to close, and such a connection simply disconnects without a hang-up.</para>
    /// </summary>
    public Registration Register(
        string machineId, IReadOnlySet<string> profiles, Func<RunnerCommand, CancellationToken, Task> send,
        Func<CancellationToken, Task>? close = null)
    {
        var token = new ConnectionToken(machineId, Interlocked.Increment(ref _generations));
        var superseded = _connections.ContainsKey(machineId);
        _overlay.TryRemove(machineId, out _);
        _connections[machineId] = new RunnerConnection(send, token.Generation) { Close = close };
        return new Registration(token, superseded);

    }

    /// <summary>
    /// Drops the connection <paramref name="token"/> names (its socket closed) and returns
    /// the tasks it still held, so the caller can requeue them <em>after</em> the connection
    /// is gone (#87).
    ///
    /// <para>Returning them is what makes that order possible: removal and the read are
    /// one step, so there is no window in which the connection is unreachable but its
    /// tasks are still unknown. A caller that unregistered first and then asked
    /// <see cref="SessionsOn"/> would get an empty list and requeue nothing — stranding
    /// exactly the tasks the disconnect was supposed to free.</para>
    ///
    /// <para>Once the connection is out of the dictionary the machine is invisible to
    /// <see cref="ReadyMachines"/>, <see cref="SnapshotFor"/> and <see cref="SendAsync"/>,
    /// so the requeue's own <c>pg_notify</c> cannot wake a dispatch pass that claims a
    /// task straight back onto the dead socket.</para>
    ///
    /// <para><b>A superseded connection tears down to nothing</b> (#94):
    /// <see cref="UnregisterOutcome.Unregistered"/> is false and the held set is empty, so
    /// the caller requeues nothing. That is the whole point of the token — this connection's
    /// tasks, if it had any, now belong to the connection that replaced it and are still
    /// being worked, so requeueing them here would abandon live work and leave the live
    /// socket registered nowhere. Also the answer for a machine that was already gone, and
    /// for a second teardown of the same connection.</para>
    /// </summary>
    public UnregisterOutcome Unregister(ConnectionToken token)
    {
        if (Current(token) is not { } conn)
            return new UnregisterOutcome(false, []);
        // Compare-and-remove: only drop the entry while it is still this generation, so a
        // connection registered between the lookup above and here survives.
        if (!_connections.TryRemove(new KeyValuePair<string, RunnerConnection>(token.MachineId, conn)))
            return new UnregisterOutcome(false, []);
        _overlay.TryRemove(token.MachineId, out _);
        lock (conn.Gate)
            return new UnregisterOutcome(true, conn.Dispatched.Keys.ToArray());

    }

    /// <summary>
    /// Hangs up on whichever connection a machine currently holds: drops the registry
    /// entry and closes the socket underneath it, returning the tasks it held so the
    /// caller can requeue them exactly as a dropped connection's teardown does.
    ///
    /// <para>Machine-keyed, not connection-keyed, and that is the point: the caller is
    /// machine revocation (§13, <see cref="Auth.MachineRevocationService"/>), which is
    /// about the box rather than about one socket, and the box is reachable on whatever
    /// connection it holds right now. Removing the entry <em>before</em> closing keeps
    /// the #87 order — the requeue that follows commits a <c>pg_notify</c>, and a
    /// still-registered ready machine would take one of those tasks straight back onto
    /// the socket being torn down.</para>
    ///
    /// <para>The endpoint serving that socket will run its own teardown when the close
    /// lands; <see cref="Unregister"/> then finds the entry already gone and reports
    /// nothing held, so the requeue happens once, here.</para>
    ///
    /// <para>Returns <see cref="UnregisterOutcome.Unregistered"/> false with an empty
    /// held set for a machine holding no connection — a machine that is enrolled but
    /// offline is the ordinary case for a revoke, not an error.</para>
    /// </summary>
    public async Task<UnregisterOutcome> DisconnectAsync(string machineId, CancellationToken ct = default)
    {
        if (!_connections.TryRemove(machineId, out var conn))
            return new UnregisterOutcome(false, []);
        _overlay.TryRemove(machineId, out _);


        SessionId[] held;
        lock (conn.Gate)
            held = conn.Dispatched.Keys.ToArray();

        if (conn.Close is { } close)
        {
            // Best-effort, like every other write down a runner socket (§10): a
            // connection we are hanging up on may already be dead, and a revoke must
            // not fail because the box it un-trusts stopped answering.
            try
            {
                await close(ct);
            }
            catch
            {
                // Ignored: the entry is already out of the registry, which is the part
                // that decides what the plane will send or dispatch here.
            }
        }

        return new UnregisterOutcome(true, held);
    }

    /// <summary>
    /// Accepts a heartbeat on the connection <paramref name="token"/> names.
    /// Guid machines persist facts in <c>machines</c> / <c>machine_processes</c>
    /// via <see cref="HubOutbox.WriteHeartbeatAsync"/>; this only checks the
    /// token is still live. Non-guid test ids fold into an overlay.
    /// A stale token is ignored (#94).
    /// </summary>
    public bool ApplyHeartbeat(ConnectionToken token, MachineHeartbeat heartbeat)
    {
        if (Current(token) is not { } _)
            return false;
        RememberOverlay(token.MachineId, heartbeat);
        return true;
    }

    /// <summary>
    /// Same as the token overload for tests driving a single connection per machine.
    /// </summary>
    public void ApplyHeartbeat(string machineId, MachineHeartbeat heartbeat)
    {
        if (!_connections.ContainsKey(machineId))
            return;
        RememberOverlay(machineId, heartbeat);
    }

    private void RememberOverlay(string machineId, MachineHeartbeat heartbeat)
    {
        if (Guid.TryParse(machineId, out _))
            return;
        _overlay[machineId] = new Overlay(
            heartbeat.Ready && !heartbeat.UnderBackPressure,
            heartbeat.UnderBackPressure,
            new HashSet<string>(heartbeat.Profiles, StringComparer.Ordinal),
            clock.GetUtcNow(),
            heartbeat.Processes ?? (_overlay.TryGetValue(machineId, out var prev) ? prev.Processes : []));
    }


    /// <summary>The connection <paramref name="token"/> names, or null once it has been
    /// superseded or torn down.</summary>
    private RunnerConnection? Current(ConnectionToken token) =>
        _connections.TryGetValue(token.MachineId, out var conn) && conn.Generation == token.Generation
            ? conn
            : null;

    /// <summary>
    /// Records that a task was dispatched to a machine, stamping both clocks now. Also
    /// the re-adoption path on reconnect (§10, #86): stamping <em>now</em> rather than
    /// carrying a pre-restart timestamp is deliberate there — the clocks measure the
    /// plane's own silence, so a rehydrated task gets a full window to prove itself
    /// instead of being requeued the instant its machine comes back. No-ops when the
    /// machine has no live connection.
    /// </summary>
    public void TrackDispatch(string machineId, SessionId task)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return;
        var now = clock.GetUtcNow();
        lock (conn.Gate)
            conn.Dispatched[task] = new TaskActivity(now, now);
    }

    /// <summary>
    /// Refreshes both clocks from a **progress** signal — <c>started</c>,
    /// <c>session-started</c>, <c>tool-call</c>, <c>subagent-spawned</c> (§10
    /// per-task liveness). Progress implies aliveness, so this is the strictly
    /// stronger of the two records.
    /// </summary>
    public void RecordProgress(SessionId task) => Refresh(task, progress: true);

    /// <summary>
    /// Refreshes only the aliveness clock, from an <c>alive</c> event: landbridged
    /// asserting the harness process still exists, which says nothing about whether
    /// the agent is getting anywhere. Deliberately does <b>not</b> touch the
    /// progress clock — if it did, a wedged-but-running agent would be immortal,
    /// which is the whole reason the two clocks are separate (§10).
    /// </summary>
    public void RecordAlive(SessionId task) => Refresh(task, progress: false);

    private void Refresh(SessionId task, bool progress)
    {
        var now = clock.GetUtcNow();
        foreach (var conn in _connections.Values)
        {
            lock (conn.Gate)
            {
                if (conn.Dispatched.TryGetValue(task, out var current))
                {
                    conn.Dispatched[task] = current with
                    {
                        LastActivity = now,
                        LastProgress = progress ? now : current.LastProgress,
                    };
                    return;
                }
            }
        }
    }

    /// <summary>Stops tracking a task on every machine (exit, requeue, reboot).</summary>
    public void Untrack(SessionId task)
    {
        foreach (var conn in _connections.Values)
            lock (conn.Gate)
                conn.Dispatched.Remove(task);
    }

    /// <summary>
    /// The asking ACP process has exited, but the task stays tracked so the
    /// wait-TTL sweeper and a later answer can still resolve its machine. A
    /// subsequent <see cref="HasLiveProcess"/> is false: answering then
    /// redispatches rather than sending <c>PromptCommand</c> into a dead session.
    /// </summary>
    public void MarkProcessGone(SessionId task)
    {
        foreach (var conn in _connections.Values)
        {
            lock (conn.Gate)
            {
                if (conn.Dispatched.TryGetValue(task, out var current))
                {
                    conn.Dispatched[task] = current with { ProcessGone = true };
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Whether this task is tracked on a machine <em>and</em> that machine still
    /// holds a live ACP process for it. <see cref="MachineFor"/> is not this:
    /// a blocked task stays tracked after its process exits so the sweeper can
    /// find it. An in-place answer is only honest when the process is still up.
    /// </summary>
    public bool HasLiveProcess(SessionId task)
    {
        foreach (var conn in _connections.Values)
        {
            lock (conn.Gate)
            {
                if (conn.Dispatched.TryGetValue(task, out var activity) && !activity.ProcessGone)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The machine a task is currently tracked as dispatched to, or null when it
    /// is tracked nowhere. Two callers: the wait-TTL sweeper (§11) uses it to find
    /// a blocked task's machine — to judge that machine's liveness and record it
    /// in the park record — and the forward orchestrator (§8.3) resolves the
    /// producer and consumer machines of a forward from their tasks. A task is
    /// tracked on at most one machine (dispatch is single, §9 check 5). Null means the
    /// plane does not currently hold the assignment — the machine is disconnected, or it
    /// has not reconnected yet since a plane restart (a reconnect re-adopts what it holds,
    /// <see cref="DispatchService.RehydrateMachineAsync"/>); callers treat that
    /// conservatively.
    /// </summary>
    public string? MachineFor(SessionId task)
    {
        foreach (var (id, conn) in _connections)
        {
            lock (conn.Gate)
                if (conn.Dispatched.ContainsKey(task))
                    return id;
        }
        return null;
    }

    /// <summary>
    /// A machine's most recent overlay heartbeat, or null. Guid machines use
    /// <c>machines.last_spoke_at</c>; this is the non-guid test path.
    /// </summary>
    public DateTimeOffset? LastHeartbeatFor(string machineId) =>
        _overlay.TryGetValue(machineId, out var o) ? o.LastHeartbeat : null;

    /// <summary>Agent-started processes last reported on a non-guid test id.</summary>
    public IReadOnlyList<ProcessStatus> ProcessesOn(string machineId) =>
        _overlay.TryGetValue(machineId, out var o) ? o.Processes : [];


    /// <summary>The tasks currently tracked as dispatched to a machine.</summary>
    public IReadOnlyList<SessionId> SessionsOn(string machineId)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return [];
        lock (conn.Gate)
            return conn.Dispatched.Keys.ToArray();
    }

    /// <summary>Picks a ready overlay machine declaring <paramref name="requiredProfile"/>, or null.
    /// Guid machines go through <see cref="MachineLive.ReadyAsync"/>.</summary>
    public string? TryPickMachine(string requiredProfile)
    {
        foreach (var (id, facts) in _overlay)
        {
            if (_connections.ContainsKey(id) && facts.Ready && facts.Profiles.Contains(requiredProfile))
                return id;
        }
        return null;
    }

    /// <summary>
    /// Socket presence, plus overlay facts for non-guid ids. Guid machines that
    /// have not yet spoken are connected and not ready; live facts are the row.
    /// Null if the socket is gone.
    /// </summary>
    public MachineSnapshot? SnapshotFor(string machineId)
    {
        if (!_connections.ContainsKey(machineId))
            return null;
        if (_overlay.TryGetValue(machineId, out var o))
            return new MachineSnapshot(machineId, o.Ready, o.UnderBackPressure, o.Profiles);
        return new MachineSnapshot(
            machineId, Ready: false, UnderBackPressure: false,
            new HashSet<string>(StringComparer.Ordinal));
    }


    /// <summary>
    /// The fleet's declared profiles grouped as routing targets — what a Lead's
    /// <c>list_profiles</c> reads (§7 exact-match routing, §10 as-built refinement).
    ///
    /// <para><b>Derived from the dispatch inputs themselves, not from a parallel view.</b>
    /// It walks <see cref="MachineIds"/> and reads each machine through
    /// <see cref="SnapshotFor"/> — the very <see cref="MachineSnapshot"/> a dispatch pass
    /// hands the store and the engine matches on — plus
    /// <see cref="LastHeartbeatFor"/>. So the profiles listed are exactly the profiles a
    /// task can match, and <see cref="ProfileRoutingEntry.Dispatchable"/> agrees with
    /// <see cref="TryPickMachine"/> by construction for overlay ids. Guid machines go
    /// through <see cref="MachineLive.RoutingAsync"/> so the table is the same input
    /// dispatch uses.
    /// </para>
    /// <para>Full enumeration rather than <see cref="ReadyMachines"/>, for the same reason
    /// the §12 view enumerates: a machine that is connected and saturated still declares
    /// its profiles, and a Lead needs the profile to appear — as present but not
    /// dispatchable — rather than to vanish and read as "no machine declares this".</para>
    ///
    /// <para>Not a snapshot of one instant, and it does not need to be: membership comes
    /// from the concurrent dictionary and each machine is then read under its own
    /// <c>Gate</c>, so a machine that raced away mid-walk is skipped and one that arrives
    /// mid-walk may or may not appear. Routing is a live question whose answer can change
    /// the moment after it is given, so a consistent-at-an-instant read would be no more
    /// true — and this is how <see cref="DashboardQueries.GetMachinesAsync"/> reads the
    /// registry too.</para>
    /// </summary>
    public ProfileRoutingView ProfileRouting()
    {
        var byProfile = new Dictionary<string, List<ProfileMachineView>>(StringComparer.Ordinal);
        var connected = 0;

        foreach (var machineId in MachineIds())
        {
            if (SnapshotFor(machineId) is not { } snapshot)
                continue; // raced away between enumeration and read
            connected++;
            var machine = new ProfileMachineView(
                machineId, snapshot.Ready, snapshot.UnderBackPressure, LastHeartbeatFor(machineId));
            foreach (var profile in snapshot.DeclaredProfiles)
            {
                if (!byProfile.TryGetValue(profile, out var machines))
                    byProfile[profile] = machines = [];
                machines.Add(machine);
            }
        }

        var profiles = byProfile
            .Select(p => new ProfileRoutingEntry(
                p.Key,
                p.Value.Any(m => m.Ready),
                p.Value.OrderBy(m => m.MachineId, StringComparer.Ordinal).ToList()))
            .OrderBy(p => p.Profile, StringComparer.Ordinal)
            .ToList();

        return new ProfileRoutingView(profiles, connected);
    }

    /// <summary>
    /// Overlay-ready machine ids (non-guid tests). Production dispatch uses
    /// <see cref="MachineLive.ReadyAsync"/>.
    /// </summary>
    public IReadOnlyList<string> ReadyMachines()
    {
        var ready = new List<string>();
        foreach (var (id, facts) in _overlay)
        {
            if (facts.Ready && _connections.ContainsKey(id))
                ready.Add(id);
        }
        return ready;
    }


    /// <summary>
    /// Every currently-registered machine id, regardless of readiness or whether it
    /// holds a tracked task — the full-enumeration snapshot the §12 Machine Group
    /// view needs so a connected, back-pressured, zero-task machine is still visible
    /// (neither <see cref="ReadyMachines"/> nor <see cref="AllTracked"/> would name
    /// it). Membership lives in the concurrent dictionary, so the key snapshot is
    /// consistent without taking any connection's <c>Gate</c> — that lock guards a
    /// connection's mutable fields, which this read does not touch.
    /// </summary>
    public IReadOnlyList<string> MachineIds() => _connections.Keys.ToArray();

    /// <summary>
    /// Whether the dispatch lease for <paramref name="task"/> is still held: the
    /// task is tracked on some machine AND that machine's connection is still
    /// registered (§10). The blocked-input answer path reads the held-lease machine
    /// itself via <see cref="MachineFor"/> (it becomes the redispatch park record's
    /// preferred machine); this boolean predicate is the same fact for callers that
    /// only need the yes/no — the lease is what a blocked task's recovery affinity
    /// hangs on (§11).
    /// </summary>
    public bool IsLeaseHeld(SessionId task)
    {
        foreach (var tracked in AllTracked())
            if (tracked.Session == task)
                return SnapshotFor(tracked.Machine) is not null;
        return false;
    }

    /// <summary>Every tracked task with its machine and both clocks, for the liveness scan.</summary>
    public IReadOnlyList<TrackedTask> AllTracked()
    {
        var all = new List<TrackedTask>();
        foreach (var (id, conn) in _connections)
        {
            lock (conn.Gate)
                foreach (var (task, activity) in conn.Dispatched)
                    all.Add(new TrackedTask(task, id, activity.LastActivity, activity.LastProgress));
        }
        return all;
    }

    /// <summary>
    /// Sends a command down a machine's connection. Best-effort against a live
    /// connection (§10): returns false if the machine is gone or the write
    /// fails, never throws and never queues.
    /// </summary>
    public async Task<bool> SendAsync(string machineId, RunnerCommand command, CancellationToken ct)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return false;
        try
        {
            await conn.Send(command, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends <c>kill</c> for a dispatch the plane has just abandoned, and records that the
    /// machine's resulting <c>exited</c> is this kill echoing back rather than news about
    /// the task (§10, #84). Returns false when the machine has no live channel or the write
    /// failed — in which case nothing was sent and nothing is expected.
    ///
    /// <para><b>Why the expectation exists.</b> The runner reports every process death as
    /// <c>exited</c>, including one the plane ordered, and the event names only the task —
    /// there is no attempt or instance on it (§10 the-runner-is-transport; the wire is
    /// frozen). By the time the echo arrives the plane may already have redispatched that
    /// task, quite possibly to this same machine, so an unqualified <c>exited</c> would be
    /// read as the <em>successor</em> attempt dying and requeue it: a second requeue against
    /// the §9 check 7 cap for one liveness loss, and a live worker left running for a task
    /// the plane has just put back in the queue. Recording the kill closes that, because the
    /// plane knows something the event cannot carry — that it caused this death and has
    /// already accounted for it.</para>
    ///
    /// <para>Recorded <em>before</em> the write, so the echo cannot beat its own
    /// expectation; withdrawn again if the write fails, so a failed kill leaves nothing
    /// behind to swallow a later genuine exit. The expectation lives on this connection, so
    /// it evaporates with the socket — a machine that drops before echoing requeues
    /// everything it held anyway.</para>
    /// </summary>
    public async Task<bool> SendKillAsync(
        string machineId, SessionId task, TimeSpan expectExitWithin, CancellationToken ct)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return false;
        var until = clock.GetUtcNow() + expectExitWithin;
        lock (conn.Gate)
            conn.CommandedExits[task] = until;

        if (await SendAsync(machineId, new KillCommand(task), ct))
            return true;

        lock (conn.Gate)
            conn.CommandedExits.Remove(task);
        return false;
    }

    /// <summary>
    /// Whether this <c>exited</c> is the echo of a kill the plane itself ordered for
    /// <paramref name="task"/> — see <see cref="SendKillAsync"/>. Consumes the expectation,
    /// so the next <c>exited</c> for the task is news again.
    ///
    /// <para>An expectation that has outlived its window is dropped and reported as not
    /// matching: past that point the plane cannot tell a very late echo from the genuine
    /// exit of a successor attempt, and treating a genuine exit as an echo would leave the
    /// task in <c>working</c> until a liveness clock reclaims it. The window is chosen so
    /// that swallowing is the safer error inside it and the wrong one outside
    /// (<see cref="DispatchService.CommandedExitEchoWindow"/>).</para>
    /// </summary>
    public bool ConsumeCommandedExit(SessionId task)
    {
        var now = clock.GetUtcNow();
        foreach (var conn in _connections.Values)
        {
            lock (conn.Gate)
            {
                if (!conn.CommandedExits.TryGetValue(task, out var until))
                    continue;
                conn.CommandedExits.Remove(task);
                return now <= until;
            }
        }
        return false;
    }

    /// <summary>
    /// The same question as <see cref="ConsumeCommandedExit"/> without consuming the answer:
    /// is a kill the plane ordered for <paramref name="task"/> still outstanding?
    ///
    /// <para>Exists because a killed ACP session produces <em>two</em> signals, not one — the
    /// turn ends (<c>session/cancel</c> answered) and then the process exits — and the
    /// expectation has to survive the first to still be there for the second. So
    /// <c>turn-ended</c> peeks and <c>exited</c> consumes; the ordering is guaranteed by
    /// landbridged, which cancels before it kills. Reading it the other way round would leave the
    /// genuine exit looking like news and requeue a task twice for one death.</para>
    ///
    /// <para>Do not reach for the agent's own <c>stopReason</c> instead. Measured 2026-08-16:
    /// <c>grok agent stdio</c> answers <c>cancelled</c> on turns the plane never touched, so
    /// that word identifies who reported the stop and not who ordered it — and a worker that
    /// wedges itself would be read as a kill the plane is already handling.</para>
    /// </summary>
    public bool HasCommandedExit(SessionId task)
    {
        var now = clock.GetUtcNow();
        foreach (var conn in _connections.Values)
        {
            lock (conn.Gate)
            {
                if (conn.CommandedExits.TryGetValue(task, out var until))
                    return now <= until;
            }
        }
        return false;
    }

    /// <summary>
    /// The identity of one accepted <c>/runner</c> connection (#94): the machine that
    /// authenticated, plus the generation this registry minted for the connection itself.
    /// Held by the endpoint serving that socket and presented at teardown, so cleanup can
    /// tell "my connection" from "the connection that replaced mine".
    /// </summary>
    public readonly record struct ConnectionToken(string MachineId, long Generation);

    /// <summary>
    /// What <see cref="Register"/> hands back: this connection's
    /// <see cref="ConnectionToken"/>, and whether registering it displaced a connection the
    /// machine still had — the observable signal that a machine is holding overlapping
    /// <c>/runner</c> connections (#94), which is worth a log line even though the registry
    /// handles it.
    /// </summary>
    public readonly record struct Registration(ConnectionToken Token, bool SupersededLiveConnection);

    /// <summary>
    /// What <see cref="Unregister"/> hands back: whether this connection was still the
    /// machine's registered one — so the registry actually changed and the caller owns the
    /// requeue — and the tasks it held. Always <c>false</c> and empty for a superseded
    /// connection (#94).
    /// </summary>
    public readonly record struct UnregisterOutcome(bool Unregistered, IReadOnlyList<SessionId> Held);

    /// <summary>
    /// A tracked dispatch and its two liveness clocks (§10).
    /// <see cref="LastActivity"/> moves on any inbound signal including <c>alive</c>
    /// — "landbridged still says this process exists". <see cref="LastProgress"/> moves
    /// only on a real progress signal — "the agent is getting somewhere". They are
    /// separate because a dead process and a wedged one need different detection
    /// times, and one number cannot carry both.
    /// </summary>
    public readonly record struct TrackedTask(
        SessionId Session, string Machine, DateTimeOffset LastActivity, DateTimeOffset LastProgress);

    /// <summary>The two clocks kept per dispatched task; see <see cref="TrackedTask"/>.
    /// <see cref="ProcessGone"/> is set when the harness exits but the task stays
    /// tracked (blocked_on_input): the lease is still this machine's, the process
    /// is not.</summary>
    private readonly record struct TaskActivity(
        DateTimeOffset LastActivity, DateTimeOffset LastProgress, bool ProcessGone = false);

    private sealed class RunnerConnection(Func<RunnerCommand, CancellationToken, Task> send, long generation)
    {
        public object Gate { get; } = new();
        public Func<RunnerCommand, CancellationToken, Task> Send { get; } = send;

        /// <summary>How to hang up on this connection from outside its endpoint
        /// (<see cref="DisconnectAsync"/>); null for a connection with no socket under it.</summary>
        public Func<CancellationToken, Task>? Close { get; init; }

        /// <summary>This connection's half of its <see cref="ConnectionToken"/>.</summary>
        public long Generation { get; } = generation;

        /// <summary>Kills the plane ordered on this connection, each with the instant its
        /// echo stops being expected; see <see cref="SendKillAsync"/>.</summary>
        public Dictionary<SessionId, DateTimeOffset> CommandedExits { get; } = new();
        public Dictionary<SessionId, TaskActivity> Dispatched { get; } = new();
    }

    private sealed record Overlay(
        bool Ready,
        bool UnderBackPressure,
        HashSet<string> Profiles,
        DateTimeOffset LastHeartbeat,
        IReadOnlyList<ProcessStatus> Processes);
}

