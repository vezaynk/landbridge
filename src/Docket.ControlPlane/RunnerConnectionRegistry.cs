using System.Collections.Concurrent;
using Docket.Contracts;
using Docket.Core;

namespace Docket.ControlPlane;

/// <summary>
/// The in-memory registry of live runner connections, spec §10. Single-node v1:
/// docketd only dials outbound, so a connection is a WebSocket the control plane
/// accepted and the send delegate that writes command frames back down it. The
/// registry holds, per machine, that delegate, the profiles it declares (learned
/// from its heartbeat), its readiness, its last heartbeat, and the set of tasks
/// dispatched to it — each with the two liveness clocks §10 needs: when the machine
/// last said the task's process was alive, and when the task last made progress.
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
    /// <see cref="TaskStore.HeldDispatchesOnAsync"/>): it re-adopts exactly the tasks whose
    /// live incumbent instance was minted for this machine. Carrying the old map over would
    /// instead preserve entries that fence rejects — a task requeued out from under this
    /// machine while the stale socket still listed it — leaving a dead dispatch tracked and
    /// under a liveness clock. There is no double-tracking either way: dispatches are keyed
    /// by task, so re-adoption overwrites rather than accumulates.</para>
    /// </summary>
    public Registration Register(
        string machineId, IReadOnlySet<string> profiles, Func<RunnerCommand, CancellationToken, Task> send)
    {
        var token = new ConnectionToken(machineId, Interlocked.Increment(ref _generations));
        var superseded = _connections.ContainsKey(machineId);
        _connections[machineId] = new RunnerConnection(send, token.Generation)
        {
            Profiles = profiles,
            LastHeartbeat = clock.GetUtcNow(),
        };
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
    /// <see cref="TasksOn"/> would get an empty list and requeue nothing — stranding
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
        lock (conn.Gate)
            return new UnregisterOutcome(true, conn.Dispatched.Keys.ToArray());
    }

    /// <summary>
    /// Folds a heartbeat into the state of the connection <paramref name="token"/> names;
    /// see <see cref="ApplyHeartbeat(string, MachineHeartbeat)"/> for what it folds.
    ///
    /// <para>This is the overload the runner endpoint uses, and the reason is #94: a
    /// superseded connection must not steer the live one. Readiness, declared profiles and
    /// the heartbeat timestamp are all tracking state, and a heartbeat that was in flight
    /// (or buffered) when a machine's new connection replaced the old one would otherwise
    /// be applied to the successor — marking a ready machine back-pressured, or refreshing
    /// a liveness timestamp on behalf of a socket that is no longer carrying anything. A
    /// stale token is ignored outright.</para>
    /// </summary>
    public void ApplyHeartbeat(ConnectionToken token, MachineHeartbeat heartbeat)
    {
        if (Current(token) is { } conn)
            Fold(conn, heartbeat);
    }

    /// <summary>
    /// Folds a heartbeat into whichever connection a machine currently holds: readiness
    /// (ready unless under back-pressure), declared profiles, and the heartbeat timestamp
    /// (§10). Keyed by the <b>authenticated</b> machine id (the caller's token identity),
    /// never the heartbeat's self-reported <see cref="MachineHeartbeat.MachineId"/> — a
    /// machine must not be able to steer another's connection state (§13).
    ///
    /// <para>For callers holding no <see cref="ConnectionToken"/>, which in practice means
    /// tests driving a single connection per machine. Anything reading heartbeats off a
    /// socket has a token and should present it —
    /// <see cref="ApplyHeartbeat(ConnectionToken, MachineHeartbeat)"/> — so a superseded
    /// connection cannot write over its successor's state (#94).</para>
    /// </summary>
    public void ApplyHeartbeat(string machineId, MachineHeartbeat heartbeat)
    {
        if (_connections.TryGetValue(machineId, out var conn))
            Fold(conn, heartbeat);
    }

    private void Fold(RunnerConnection conn, MachineHeartbeat heartbeat)
    {
        lock (conn.Gate)
        {
            conn.Ready = heartbeat.Ready && !heartbeat.UnderBackPressure;
            conn.UnderBackPressure = heartbeat.UnderBackPressure;
            conn.Profiles = new HashSet<string>(heartbeat.Profiles, StringComparer.Ordinal);
            // §10/§12: stored verbatim and never interpreted — the plane renders what the
            // machine reports and forms no opinion about service health. Null (a runner
            // predating the field) leaves the previous list rather than blanking it.
            conn.Services = heartbeat.Services ?? conn.Services;
            conn.Processes = heartbeat.Processes ?? conn.Processes;
            conn.LastHeartbeat = clock.GetUtcNow();
        }
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
    public void TrackDispatch(string machineId, TaskId task)
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
    public void RecordProgress(TaskId task) => Refresh(task, progress: true);

    /// <summary>
    /// Refreshes only the aliveness clock, from an <c>alive</c> event: docketd
    /// asserting the harness process still exists, which says nothing about whether
    /// the agent is getting anywhere. Deliberately does <b>not</b> touch the
    /// progress clock — if it did, a wedged-but-running agent would be immortal,
    /// which is the whole reason the two clocks are separate (§10).
    /// </summary>
    public void RecordAlive(TaskId task) => Refresh(task, progress: false);

    private void Refresh(TaskId task, bool progress)
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
    public void Untrack(TaskId task)
    {
        foreach (var conn in _connections.Values)
            lock (conn.Gate)
                conn.Dispatched.Remove(task);
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
    public string? MachineFor(TaskId task)
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
    /// A machine's most recent heartbeat, or null when it has no live connection.
    /// A blocked task's own per-task activity is frozen — its worker has exited
    /// (§11) — so the wait-TTL sweeper judges the machine's liveness by this
    /// machine-level heartbeat, not the task's activity, and requeues a blocked
    /// task whose machine has gone silent past the liveness window (§6:
    /// blocked_on_input → submitted on machine liveness loss).
    /// </summary>
    public DateTimeOffset? LastHeartbeatFor(string machineId)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return null;
        lock (conn.Gate)
            return conn.LastHeartbeat;
    }

    /// <summary>
    /// The services a machine last reported (§10, §12). Empty for a machine that
    /// declares none or predates the heartbeat field — the plane does not distinguish
    /// those, because neither gives it anything to render.
    /// </summary>
    public IReadOnlyList<ServiceStatus> ServicesOn(string machineId)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return [];
        lock (conn.Gate)
            return conn.Services;
    }

    /// <summary>The agent-started processes a machine last reported (§10, §12).</summary>
    public IReadOnlyList<ProcessStatus> ProcessesOn(string machineId)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return [];
        lock (conn.Gate)
            return conn.Processes;
    }

    /// <summary>The tasks currently tracked as dispatched to a machine.</summary>
    public IReadOnlyList<TaskId> TasksOn(string machineId)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return [];
        lock (conn.Gate)
            return conn.Dispatched.Keys.ToArray();
    }

    /// <summary>Picks a ready machine declaring <paramref name="requiredProfile"/>, or null.</summary>
    public string? TryPickMachine(string requiredProfile)
    {
        foreach (var (id, conn) in _connections)
        {
            lock (conn.Gate)
                if (conn.Ready && conn.Profiles.Contains(requiredProfile))
                    return id;
        }
        return null;
    }

    /// <summary>The machine-eligibility snapshot dispatch runs against, or null if gone.</summary>
    public MachineSnapshot? SnapshotFor(string machineId)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return null;
        lock (conn.Gate)
            return new MachineSnapshot(
                machineId, conn.Ready, conn.UnderBackPressure,
                new HashSet<string>(conn.Profiles, StringComparer.Ordinal));
    }

    /// <summary>The machine ids currently ready to accept dispatch.</summary>
    public IReadOnlyList<string> ReadyMachines()
    {
        var ready = new List<string>();
        foreach (var (id, conn) in _connections)
        {
            lock (conn.Gate)
                if (conn.Ready)
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
    public bool IsLeaseHeld(TaskId task)
    {
        foreach (var tracked in AllTracked())
            if (tracked.Task == task)
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
        string machineId, TaskId task, TimeSpan expectExitWithin, CancellationToken ct)
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
    public bool ConsumeCommandedExit(TaskId task)
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
    public readonly record struct UnregisterOutcome(bool Unregistered, IReadOnlyList<TaskId> Held);

    /// <summary>
    /// A tracked dispatch and its two liveness clocks (§10).
    /// <see cref="LastActivity"/> moves on any inbound signal including <c>alive</c>
    /// — "docketd still says this process exists". <see cref="LastProgress"/> moves
    /// only on a real progress signal — "the agent is getting somewhere". They are
    /// separate because a dead process and a wedged one need different detection
    /// times, and one number cannot carry both.
    /// </summary>
    public readonly record struct TrackedTask(
        TaskId Task, string Machine, DateTimeOffset LastActivity, DateTimeOffset LastProgress);

    /// <summary>The two clocks kept per dispatched task; see <see cref="TrackedTask"/>.</summary>
    private readonly record struct TaskActivity(DateTimeOffset LastActivity, DateTimeOffset LastProgress);

    private sealed class RunnerConnection(Func<RunnerCommand, CancellationToken, Task> send, long generation)
    {
        public object Gate { get; } = new();
        public Func<RunnerCommand, CancellationToken, Task> Send { get; } = send;

        /// <summary>This connection's half of its <see cref="ConnectionToken"/>.</summary>
        public long Generation { get; } = generation;

        /// <summary>Kills the plane ordered on this connection, each with the instant its
        /// echo stops being expected; see <see cref="SendKillAsync"/>.</summary>
        public Dictionary<TaskId, DateTimeOffset> CommandedExits { get; } = new();

        public IReadOnlySet<string> Profiles { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public bool Ready { get; set; }
        public bool UnderBackPressure { get; set; }
        public DateTimeOffset LastHeartbeat { get; set; }
        public IReadOnlyList<ServiceStatus> Services { get; set; } = [];
        public IReadOnlyList<ProcessStatus> Processes { get; set; } = [];
        public Dictionary<TaskId, TaskActivity> Dispatched { get; } = new();
    }
}
