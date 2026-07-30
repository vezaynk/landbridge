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
/// dispatched to it (each with its last activity time, for the liveness window).
///
/// It is transport-agnostic — nothing here knows about WebSockets — so it is
/// driven directly in tests. All state is process-local and evaporates on
/// restart; machine-assignment persistence across a control-plane restart is a
/// documented follow-up (§10 is single control plane for v1).
/// </summary>
public sealed class RunnerConnectionRegistry(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, RunnerConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>Registers a dialed-in machine. Starts not-ready with no profiles;
    /// its first heartbeat supplies both (§10).</summary>
    public void Register(string machineId, IReadOnlySet<string> profiles, Func<RunnerCommand, CancellationToken, Task> send)
    {
        _connections[machineId] = new RunnerConnection(send)
        {
            Profiles = profiles,
            LastHeartbeat = clock.GetUtcNow(),
        };
    }

    /// <summary>Drops a machine's connection (socket closed). Its tracked tasks are
    /// returned by <see cref="TasksOn"/> first so the caller can requeue them.</summary>
    public void Unregister(string machineId) => _connections.TryRemove(machineId, out _);

    /// <summary>
    /// Folds a heartbeat into connection state: readiness (ready unless under
    /// back-pressure), declared profiles, and the heartbeat timestamp (§10). Keyed
    /// by the <b>authenticated</b> machine id (the caller's token identity), never
    /// the heartbeat's self-reported <see cref="MachineHeartbeat.MachineId"/> — a
    /// machine must not be able to steer another's connection state (§13).
    /// </summary>
    public void ApplyHeartbeat(string machineId, MachineHeartbeat heartbeat)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return;
        lock (conn.Gate)
        {
            conn.Ready = heartbeat.Ready && !heartbeat.UnderBackPressure;
            conn.UnderBackPressure = heartbeat.UnderBackPressure;
            conn.Profiles = new HashSet<string>(heartbeat.Profiles, StringComparer.Ordinal);
            conn.LastHeartbeat = clock.GetUtcNow();
        }
    }

    /// <summary>Records that a task was dispatched to a machine, stamping activity now.</summary>
    public void TrackDispatch(string machineId, TaskId task)
    {
        if (!_connections.TryGetValue(machineId, out var conn))
            return;
        lock (conn.Gate)
            conn.Dispatched[task] = clock.GetUtcNow();
    }

    /// <summary>Refreshes a tracked task's last-activity time from an inbound liveness
    /// signal (started/alive/tool-call) (§10 per-task liveness).</summary>
    public void RecordActivity(TaskId task)
    {
        foreach (var conn in _connections.Values)
        {
            lock (conn.Gate)
            {
                if (conn.Dispatched.ContainsKey(task))
                {
                    conn.Dispatched[task] = clock.GetUtcNow();
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
    /// tracked on at most one machine (dispatch is single, §9 check 5). Null
    /// means the plane no longer holds the assignment (e.g. after a control-plane
    /// restart drops this in-memory registry — machine-assignment persistence is
    /// a documented §10 follow-up); callers treat that conservatively.
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
    /// registered (§10). This is the control-plane fact behind
    /// <c>AnswerInput.LeaseStillHeld</c> — answering a blocked task must not
    /// resume it onto a machine that is gone; the engine parks it instead (§6).
    /// </summary>
    public bool IsLeaseHeld(TaskId task)
    {
        foreach (var tracked in AllTracked())
            if (tracked.Task == task)
                return SnapshotFor(tracked.Machine) is not null;
        return false;
    }

    /// <summary>Every tracked (task, machine, last-activity) triple, for the liveness scan.</summary>
    public IReadOnlyList<TrackedTask> AllTracked()
    {
        var all = new List<TrackedTask>();
        foreach (var (id, conn) in _connections)
        {
            lock (conn.Gate)
                foreach (var (task, at) in conn.Dispatched)
                    all.Add(new TrackedTask(task, id, at));
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

    /// <summary>A tracked dispatch: the task, the machine holding it, and its last activity.</summary>
    public readonly record struct TrackedTask(TaskId Task, string Machine, DateTimeOffset LastActivity);

    private sealed class RunnerConnection(Func<RunnerCommand, CancellationToken, Task> send)
    {
        public object Gate { get; } = new();
        public Func<RunnerCommand, CancellationToken, Task> Send { get; } = send;
        public IReadOnlySet<string> Profiles { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public bool Ready { get; set; }
        public bool UnderBackPressure { get; set; }
        public DateTimeOffset LastHeartbeat { get; set; }
        public Dictionary<TaskId, DateTimeOffset> Dispatched { get; } = new();
    }
}
