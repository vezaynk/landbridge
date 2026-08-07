using Docket.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Docket.ControlPlane;

/// <summary>
/// The §11 wait-TTL sweeper: the control-plane driver that ends a task's stay in
/// <see cref="TaskState.BlockedOnInput"/> so no task waits on a human forever.
/// A headless worker that asks a question ends its turn and its process exits —
/// per-task liveness is suspended while blocked (§11), and <see cref="DispatchService"/>
/// deliberately leaves blocked tasks alone — so without this sweeper a task whose
/// Lead never answers holds its lease indefinitely. This closes exactly the two
/// §6 rows the control plane owns for a waiting task:
///
///   blocked_on_input → parked    (<see cref="WaitTtlExpired"/>): the wait TTL
///     elapsed; the worker-instance token is revoked and a park record is written
///     so the answer, when it lands, wakes the task and redispatch resumes it —
///     "what lets a Team survive its Lead's session ending" (§11).
///   blocked_on_input → submitted (<see cref="LivenessLost"/>): the dispatched
///     machine went silent while waiting; the task requeues against the
///     infrastructure counter (§6), exactly as the reboot/disconnect path does.
///
/// Both outcomes are expressed as commands through <see cref="TaskStore"/> — the
/// engine owns every transition (§15), never raw SQL. Machine death is checked
/// first: a task on a dead machine requeues rather than parking onto a machine
/// that could never resume it. The sweep is a periodic <see cref="TimeProvider"/>
/// timer with a fresh scoped <see cref="DocketDbContext"/> per pass, mirroring
/// <see cref="DispatchService"/>.
///
/// Race safety: a Lead answering (<see cref="AnswerInput"/>) at the same instant
/// must win cleanly. Because both outcomes go through the store's optimistic
/// concurrency (xmin) and the engine's source-state check, a sweep that loses the
/// race sees the task already <see cref="TaskState.Submitted"/> — the answer
/// requeued it for redispatch — (or a concurrency conflict) and no-ops; the answer
/// stands and the task is redispatched with its transcript resumed.
/// </summary>
public sealed class WaitTtlSweeper : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RunnerConnectionRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<WaitTtlSweeper> _logger;
    private readonly TimeSpan _waitTtl;
    private readonly TimeSpan _machineLivenessWindow;
    private readonly TimeSpan _sweepInterval;

    /// <summary>
    /// How long a task may sit in blocked_on_input before it parks (§11 names the
    /// wait TTL but not a value). 30 minutes by default — long enough for a human
    /// to answer in-session, short enough that a Lead's session ending frees the
    /// lease promptly. Override with <c>Docket:WaitTtl</c>.
    /// </summary>
    public static readonly TimeSpan DefaultWaitTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How stale a machine's heartbeat may get before a task blocked on it is
    /// treated as having lost its machine and requeued. 90 seconds — six missed
    /// 15-second heartbeats (the runner default) and comfortably above the
    /// per-task liveness window, so a briefly-quiet machine is not mistaken for a
    /// dead one. Override with <c>Docket:MachineLivenessTtl</c>.
    /// </summary>
    public static readonly TimeSpan DefaultMachineLivenessWindow = TimeSpan.FromSeconds(90);

    /// <summary>How often a sweep runs. Override with <c>Docket:WaitTtlSweepInterval</c>.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(60);

    private CancellationTokenSource? _cts;
    private ITimer? _timer;

    public WaitTtlSweeper(
        IServiceScopeFactory scopes,
        RunnerConnectionRegistry registry,
        TimeProvider clock,
        ILogger<WaitTtlSweeper> logger,
        TimeSpan? waitTtl = null,
        TimeSpan? machineLivenessWindow = null,
        TimeSpan? sweepInterval = null)
    {
        _scopes = scopes;
        _registry = registry;
        _clock = clock;
        _logger = logger;
        _waitTtl = waitTtl ?? DefaultWaitTtl;
        _machineLivenessWindow = machineLivenessWindow ?? DefaultMachineLivenessWindow;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _timer = _clock.CreateTimer(
            _ => _ = SweepSafeAsync(ct), null, _sweepInterval, _sweepInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        if (_timer is not null)
            await _timer.DisposeAsync();
        _cts.Dispose();
    }

    private async Task SweepSafeAsync(CancellationToken ct)
    {
        try { await SweepAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _logger.LogError(e, "wait-TTL sweep crashed");
        }
    }

    /// <summary>
    /// One sweep pass. Public so it can be driven deterministically in tests with
    /// a <c>FakeTimeProvider</c>; the hosted timer calls it on a period. Reads the
    /// blocked backlog once, then for each task decides machine-death-vs-park and
    /// applies the transition through the store. Idempotent: a task the pass
    /// already moved is no longer blocked, so a later pass skips it.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // Read the blocked backlog once. The view rows are detached (AsNoTracking),
        // so they outlive this scope; each transition below gets its own scoped
        // DbContext, mirroring DispatchService.CheckLivenessAsync.
        IReadOnlyList<BlockedTaskView> blocked;
        using (var readScope = _scopes.CreateScope())
            blocked = await readScope.ServiceProvider.GetRequiredService<TaskStore>().ListBlockedAsync(ct);

        foreach (var b in blocked)
        {
            if (ct.IsCancellationRequested)
                return;

            var task = new TaskId(b.TaskId);
            var machine = _registry.MachineFor(task);
            if (machine is null)
                // Untracked: the plane does not currently hold this task's machine
                // assignment — the machine is disconnected, or has not reconnected yet
                // since a plane restart (a reconnect re-adopts blocked tasks too,
                // DispatchService.RehydrateMachineAsync, #86). Nothing to judge liveness
                // against and no machine to record for affinity, so leave it for the
                // sweep that follows re-adoption, or for the disconnect path to requeue.
                continue;

            var lastHeartbeat = _registry.LastHeartbeatFor(machine);
            var machineLive = lastHeartbeat is { } beat && now - beat <= _machineLivenessWindow;

            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<TaskStore>();

            if (!machineLive)
            {
                // §6: blocked_on_input → submitted, machine liveness lost while
                // waiting. Same infrastructure-loss command the reboot/disconnect
                // path uses — the machine going silent is the machine going away.
                if (await TryApplyAsync(store, task, new LivenessLost(LivenessLossReason.MachineReboot), ct))
                    _logger.LogInformation(
                        "wait-TTL sweep requeued task {Task}: machine {Machine} silent past liveness window",
                        task, machine);
                continue;
            }

            if (b.BlockedAt is { } since && now - since >= _waitTtl)
            {
                // §6/§11: blocked_on_input → parked, wait TTL expired. The plane
                // knows the machine (live), the attempt (row), and the harness
                // session ref stamped from the work session's SessionStartedEvent —
                // so the park carries it and redispatch resumes the transcript. The
                // working directory still originates runner-side and is null here; a
                // resume with a session ref but no directory continues the session
                // from the workspace (§11).
                var park = new ParkRecord(machine, Directory: null, HarnessSessionRef: b.HarnessSessionRef, b.Attempt);
                if (await TryApplyAsync(store, task, new WaitTtlExpired(park), ct))
                    _logger.LogInformation(
                        "wait-TTL sweep parked task {Task} on machine {Machine}", task, machine);
            }
        }
    }

    /// <summary>
    /// Applies a sweeper transition through the store and, on success, untracks
    /// the task so the old dispatch is not left dangling on the machine (a later
    /// wake/redispatch re-tracks it). Returns whether it applied. A
    /// non-<see cref="StoreResult.Applied"/> outcome means someone moved the task
    /// first — a Lead answered, or a concurrent writer won the xmin race — so the
    /// sweep no-ops for that task and its command was rejected without any write.
    /// </summary>
    private async Task<bool> TryApplyAsync(
        TaskStore store, TaskId task, TaskCommand command, CancellationToken ct)
    {
        var result = await store.ApplyAsync(task, command, ct);
        if (result is StoreResult.Applied)
        {
            _registry.Untrack(task);
            return true;
        }
        _logger.LogDebug(
            "wait-TTL sweep no-op on task {Task}: {Command} not applied ({Result}) — answered or moved concurrently",
            task, command.GetType().Name, result.GetType().Name);
        return false;
    }
}
