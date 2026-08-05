using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Docket.ControlPlane;

/// <summary>
/// Turns submitted tasks into running dispatches, spec §6/§10. On start it scans
/// the submitted backlog once, then wakes on task-event NOTIFYs (and on a nudge
/// after a machine becomes ready) to run another dispatch pass. A pass hands
/// eligible submitted tasks to ready machines via
/// <see cref="TaskStore.DispatchNextAsync"/> — whose SKIP LOCKED claim picks the
/// task, does the submitted→working transition, and mints the worker instance
/// <em>before</em> the command is sent, so a failed send requeues a now-working
/// task rather than losing it (§10 best-effort commands).
///
/// A liveness timer requeues working tasks on either of two clocks — no
/// process-aliveness signal, or no forward progress for far longer (see
/// <see cref="CheckLivenessAsync"/>) — and requeue-on-disconnect (the socket loop
/// calling the event sink) covers a vanished machine. Fine-grained ack-vs-liveness
/// split is deferred.
/// </summary>
public sealed class DispatchService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RunnerConnectionRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<DispatchService> _logger;
    private readonly TaskEventListener? _listener;
    private readonly TimeSpan _livenessWindow;
    private readonly TimeSpan _noProgressCeiling;
    private readonly string _publicMcpUrl;

    /// <summary>Aliveness clock default: docketd asserts process-alive far more often
    /// (every heartbeat, 15s by default), so silence this long means it stopped.</summary>
    public static readonly TimeSpan DefaultLivenessWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Progress clock default. Generous on purpose: a single long tool call emits
    /// nothing while it runs, and requeueing slow-but-healthy work makes it slower.
    /// This bounds how long a genuinely wedged agent can burn, not how long a task
    /// may take.
    /// </summary>
    public static readonly TimeSpan DefaultNoProgressCeiling = TimeSpan.FromMinutes(30);

    /// <summary>The default plane MCP URL a worker dials when config supplies none (§10).</summary>
    public const string DefaultPublicMcpUrl = "http://127.0.0.1:5000";

    /// <summary>
    /// The wind-down TTL on a budget-exhaustion <c>stop</c> (§9.9). An upper bound only —
    /// the runner takes the smaller of this and the profile's own configured wind-down — so
    /// it is generous enough to let a profile's graceful seam finish a turn, while still
    /// guaranteeing the hard kill lands rather than leaving an over-ceiling worker alive.
    /// </summary>
    internal static readonly TimeSpan BudgetStopTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The plane's tracing source (§1). The dispatch span opened here continues
    /// the Lead's create_task trace and its W3C id is what rides the wire to the
    /// runner. Register it with the host's TracerProvider (Docket.Mcp/Program.cs)
    /// so the span exports.
    /// </summary>
    public const string ActivitySourceName = "Docket.ControlPlane";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    // Single-slot wake signal: many nudges collapse to one pending pass.
    private readonly Channel<bool> _wake =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _notifyPump;
    private ITimer? _livenessTimer;

    public DispatchService(
        IServiceScopeFactory scopes,
        RunnerConnectionRegistry registry,
        TimeProvider clock,
        ILogger<DispatchService> logger,
        TaskEventListener? listener = null,
        TimeSpan? livenessWindow = null,
        string? publicMcpUrl = null,
        TimeSpan? noProgressCeiling = null)
    {
        _scopes = scopes;
        _registry = registry;
        _clock = clock;
        _logger = logger;
        _listener = listener;
        _livenessWindow = livenessWindow ?? DefaultLivenessWindow;
        _noProgressCeiling = noProgressCeiling ?? DefaultNoProgressCeiling;
        _publicMcpUrl = string.IsNullOrWhiteSpace(publicMcpUrl) ? DefaultPublicMcpUrl : publicMcpUrl;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => LoopAsync(ct), ct);
        if (_listener is not null)
            _notifyPump = Task.Run(() => PumpNotifyAsync(ct), ct);
        _livenessTimer = _clock.CreateTimer(
            _ => _ = CheckLivenessSafeAsync(ct), null, _livenessWindow, _livenessWindow);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        if (_livenessTimer is not null)
            await _livenessTimer.DisposeAsync();
        _wake.Writer.TryComplete();
        foreach (var task in new[] { _loop, _notifyPump })
        {
            if (task is null)
                continue;
            try { await task; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }

    /// <summary>Nudges the loop to run a dispatch pass — e.g. the socket loop calls
    /// this after a heartbeat marks a machine ready.</summary>
    public void Signal() => _wake.Writer.TryWrite(true);

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await RunDispatchPassAsync(ct); // startup backlog scan
            await foreach (var _ in _wake.Reader.ReadAllAsync(ct))
                await RunDispatchPassAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _logger.LogError(e, "dispatch loop crashed");
        }
    }

    private async Task PumpNotifyAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _listener!.ListenAsync(ct))
                Signal();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _logger.LogError(e, "task-event notify pump crashed");
        }
    }

    /// <summary>
    /// One dispatch pass: hands eligible submitted tasks to ready machines until
    /// nothing more claims. Idempotent and safe to run concurrently — the
    /// SKIP LOCKED claim in <see cref="TaskStore.DispatchNextAsync"/> guarantees
    /// no two passes claim the same row.
    /// </summary>
    public async Task RunDispatchPassAsync(CancellationToken ct)
    {
        var exhausted = new HashSet<string>(StringComparer.Ordinal);
        while (!ct.IsCancellationRequested)
        {
            var progressed = false;
            foreach (var machineId in _registry.ReadyMachines())
            {
                if (exhausted.Contains(machineId))
                    continue;

                var snapshot = _registry.SnapshotFor(machineId);
                if (snapshot is null || !snapshot.Ready)
                {
                    exhausted.Add(machineId);
                    continue;
                }

                switch (await TryDispatchOneAsync(machineId, snapshot, ct))
                {
                    case DispatchOutcome.Dispatched:
                        progressed = true;
                        break;
                    case DispatchOutcome.NothingEligible:
                    case DispatchOutcome.SendFailed:
                        exhausted.Add(machineId);
                        break;
                }
            }
            if (!progressed)
                break;
        }
    }

    private enum DispatchOutcome { Dispatched, NothingEligible, SendFailed }

    private async Task<DispatchOutcome> TryDispatchOneAsync(
        string machineId, MachineSnapshot snapshot, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<TaskStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

        var instance = WorkerInstanceId.New();
        // §6/§11 continuation targeting: hand the store the full set of connected
        // machines so its preferred-machine claim can tell "gone" (absent here) from
        // "busy elsewhere". A continuation prefers its own machine; only a Degrade
        // task whose machine is absent here is claimable by this one (cold-start).
        var result = await store.DispatchNextAsync(snapshot, instance, ct, _registry.MachineIds());
        if (result is not StoreResult.Applied applied)
            return DispatchOutcome.NothingEligible; // no eligible submitted task for this machine

        var task = applied.Task;
        var profile = task.Profile ?? MachineSnapshot.DefaultProfile;

        // §1 tracing: open the dispatch span, parented on the Lead's create_task
        // trace context stored on the row (opaque transport metadata). This span's
        // W3C id is what the send delegate stamps onto the wire envelope, so the
        // runner — and the worker it spawns — continue the same trace. A null
        // context (or no listener) yields a root span (or no span); dispatch is
        // unaffected either way. Held open across the send so Activity.Current
        // flows into the encode call on the send path.
        using var activity = StartDispatchActivity(task.Id, machineId, profile, applied.TraceContext);

        // §5, §13: mint the worker token for this instance; docketd injects it,
        // wrapped in the MCP client config the worker dials the plane with.
        var minted = await tokens.MintWorkerTokenAsync(task.Team, task.Id, instance, ct);
        var command = new DispatchCommand(
            task.Id, profile, minted.Token, McpConfigJson: BuildWorkerMcpConfig(minted.Token),
            // §11 resume: pass the prior work session's ref (present when this task
            // was worked before and parked/requeued) so docketd continues the
            // transcript. Opaque metadata surfaced by the store; docketd resumes
            // only if the resolved profile declares resume.args, else cold-starts.
            ResumeSessionRef: applied.HarnessSessionRef,
            // §9.9/§9 check 9: the per-dispatch cap committed against the Team's ceiling,
            // enforced by the harness itself (the profile's {budget} substitution). This is
            // the backstop that holds even when spend telemetry is absent — which today it
            // always is, since nothing ingests it. Null when the Team configures no cap.
            BudgetUsd: applied.BudgetCapUsd);

        _registry.TrackDispatch(machineId, task.Id);
        var sent = await _registry.SendAsync(machineId, command, ct);
        if (sent)
            return DispatchOutcome.Dispatched;

        // §10: the submitted→working transition already committed, but nothing is
        // running — requeue against the infrastructure counter.
        _registry.Untrack(task.Id);
        await store.ApplyAsync(task.Id, new LivenessLost(LivenessLossReason.AckTimeout), ct);
        _logger.LogWarning("dispatch send failed for task {Task} on {Machine}; requeued", task.Id, machineId);
        return DispatchOutcome.SendFailed;
    }

    /// <summary>
    /// Opens the dispatch span (§1), parented on the row's stored create_task
    /// trace context when present so one trace spans create_task → dispatch →
    /// runner → worker. Returns null when nothing is listening — tracing then
    /// no-ops and dispatch proceeds unchanged.
    /// </summary>
    private static Activity? StartDispatchActivity(
        TaskId task, string machineId, string profile, string? parentTraceparent)
    {
        var activity = parentTraceparent is not null
            && ActivityContext.TryParse(parentTraceparent, null, out var parent)
                ? ActivitySource.StartActivity($"dispatch {task}", ActivityKind.Producer, parent)
                : ActivitySource.StartActivity($"dispatch {task}", ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag("docket.task_id", task.ToString());
            activity.SetTag("docket.machine_id", machineId);
            activity.SetTag("docket.profile", profile);
        }
        return activity;
    }

    /// <summary>
    /// The MCP client config a worker uses to reach the plane (§13): Claude Code's
    /// <c>--mcp-config</c> HTTP shape, with the freshly-minted worker token as a
    /// bearer header. docketd writes it to <c>{work_dir}/mcp.json</c> (0600) and
    /// substitutes the path into the profile's spawn argv — the runner never
    /// interprets it, it is transport (§10). Built with the DOM so the token is
    /// escaped correctly and no serializer reflection is needed.
    /// </summary>
    private string BuildWorkerMcpConfig(string workerToken) =>
        new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["docket"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = _publicMcpUrl,
                    ["headers"] = new JsonObject
                    {
                        ["Authorization"] = $"Bearer {workerToken}",
                    },
                },
            },
        }.ToJsonString();

    private async Task CheckLivenessSafeAsync(CancellationToken ct)
    {
        try { await CheckLivenessAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _logger.LogError(e, "liveness check crashed");
        }
    }

    /// <summary>
    /// Requeues working tasks that have lost liveness (§10 per-task liveness), on
    /// two independent clocks. Tasks that are blocked_on_input/parked have liveness
    /// suspended (§11) and are left tracked; verifying/terminal tasks are simply
    /// untracked.
    ///
    /// <para><b>Aliveness</b> (<see cref="_livenessWindow"/>, default 60s): docketd
    /// has stopped even asserting the harness process exists. That means the process
    /// died without an <c>exited</c> event, or the daemon itself is wedged — either
    /// way the task is not being worked and requeues fast.</para>
    ///
    /// <para><b>Progress</b> (<see cref="_noProgressCeiling"/>, default 30min): the
    /// process is alive but the agent has produced no progress signal for a long
    /// time — the wedged-agent case the short window used to catch by accident.
    /// It has to be generous: a single long tool call (a full test suite, a large
    /// build) legitimately emits nothing for minutes, and requeueing that is worse
    /// than waiting, because the replacement attempt does the same slow thing again.</para>
    ///
    /// <para>A task that has registered a service (§8.2) is exempt from the progress
    /// ceiling: it declared, by a deliberate protocol act, that staying up is part of
    /// its job, so "no tool calls for half an hour" is its success condition rather
    /// than a symptom. It stays subject to the aliveness clock, so a service-bearing
    /// task whose process dies is still requeued promptly.</para>
    ///
    /// <para>Requeues are not unlimited (§9 check 7): whichever clock fires, the store's
    /// transition abandons the task once its infrastructure requeue cap is reached, so a
    /// task that wedges every machine it lands on stops looping instead of burning a
    /// dispatch's authorization every half hour forever. This method neither counts nor
    /// decides — the count is on the record and the decision is the engine's; it supplies
    /// the fact of which signal fired.</para>
    /// </summary>
    public async Task CheckLivenessAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        foreach (var tracked in _registry.AllTracked())
        {
            var notAlive = now - tracked.LastActivity >= _livenessWindow;
            var noProgress = now - tracked.LastProgress >= _noProgressCeiling;
            if (!notAlive && !noProgress)
                continue;

            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<TaskStore>();
            var state = await store.GetStateAsync(tracked.Task, ct);
            switch (state)
            {
                case TaskState.Working:
                    // The progress ceiling alone does not requeue a service-bearing
                    // task; the aliveness clock still does.
                    if (!notAlive && await store.HasRegisteredServiceAsync(tracked.Task, ct))
                    {
                        _logger.LogDebug(
                            "task {Task} on {Machine} has no progress for {Idle} but bears a registered service; not requeued",
                            tracked.Task, tracked.Machine, now - tracked.LastProgress);
                        break;
                    }

                    // Which clock fired IS the reason, and it is persisted with the
                    // requeue now (§6, #73) rather than surviving only in this log line:
                    // aliveness loss is a machine/daemon problem, no-progress is a wedged
                    // agent, and the remedies differ. The requeue may also be the one that
                    // reaches the task's cap (§9 check 7), in which case the store's
                    // transition takes the task terminal instead of back to submitted —
                    // either way this dispatch is over, so the untrack below is unchanged.
                    var reason = notAlive
                        ? LivenessLossReason.LivenessTimeout
                        : LivenessLossReason.NoProgress;
                    _logger.LogWarning(
                        "requeueing task {Task} on {Machine}: {Reason} (last alive {Alive} ago, last progress {Progress} ago)",
                        tracked.Task, tracked.Machine, reason,
                        now - tracked.LastActivity, now - tracked.LastProgress);
                    var requeue = await store.ApplyAsync(tracked.Task, new LivenessLost(reason), ct);
                    if (requeue is StoreResult.Applied { Task.State: TaskState.Canceled } abandoned)
                        _logger.LogError(
                            "task {Task} abandoned after {Requeues} infrastructure requeues (cap {Cap}), last reason {Reason}",
                            tracked.Task, abandoned.Task.InfrastructureRequeues,
                            abandoned.Task.InfrastructureRequeueLimit, reason);
                    _registry.Untrack(tracked.Task);
                    break;
                case null:
                case TaskState.Verifying:
                case TaskState.Completed:
                case TaskState.Rejected:
                case TaskState.Canceled:
                    _registry.Untrack(tracked.Task);
                    break;
                    // blocked_on_input / parked / submitted: leave tracked (§11).
            }
        }

        await SweepExhaustedBudgetsAsync(ct);
    }

    /// <summary>
    /// Stops the working tasks of every Team over its budget ceiling (§9.9). Refusing new
    /// dispatch bounds what a Team can <em>start</em>; without this, work already running
    /// when the ceiling was reached would run on past it — so this is the half of the
    /// ceiling that makes it a containment control rather than an admission check.
    ///
    /// <para>Rides the liveness timer because it is the same shape of job: a periodic
    /// reconciliation of running work against a rule, cheap when there is nothing to do (one
    /// indexed query returning no Teams).</para>
    ///
    /// <para>The stop is the graceful §10/§11 wind-down — a message turn where the profile
    /// has a seam, then a bounded kill — with <see cref="StopDisposition.Preserve"/>: the
    /// work is not wrong, the Team merely ran out of authorization, so the transcript is
    /// worth keeping for whoever raises the ceiling. Delivery is best-effort against a live
    /// connection (§10); a task whose machine is gone is left alone, since there is no
    /// process to stop and the exhausted ceiling already refuses its re-dispatch.</para>
    /// </summary>
    private async Task SweepExhaustedBudgetsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var budgets = scope.ServiceProvider.GetRequiredService<TeamBudgetService>();
        var exhausted = await budgets.ExhaustedTeamsAsync(ct);
        if (exhausted.Count == 0)
            return;

        var store = scope.ServiceProvider.GetRequiredService<TaskStore>();
        var candidates = await store.WorkingTasksAwaitingBudgetStopAsync(exhausted, ct);
        if (candidates.Count == 0)
            return;

        // One read per exhausted Team, not per task: the ceiling facts go into every event
        // row and a Team can hold many working tasks.
        var views = new Dictionary<TeamId, TeamBudgetView>();
        foreach (var team in exhausted)
            views[team] = await budgets.ReadAsync(team, ct);

        foreach (var candidate in candidates)
        {
            if (_registry.MachineFor(candidate.Task) is not { } machine)
                continue;

            var view = views[candidate.Team];
            var reason = $"team budget ceiling reached: {view.CommittedUsd} USD committed of " +
                         $"{view.CeilingUsd} USD authorized";
            var sent = await _registry.SendAsync(
                machine,
                new StopCommand(candidate.Task, BudgetStopTtl, StopDisposition.Preserve, reason),
                ct);
            if (!sent)
            {
                // Left for the next pass: no event row, so the candidate query returns it again.
                _logger.LogWarning(
                    "budget stop for task {Task} not delivered to machine {Machine}", candidate.Task, machine);
                continue;
            }

            _logger.LogInformation(
                "stopped task {Task} on {Machine}: {Reason}", candidate.Task, machine, reason);
            await store.RecordBudgetExhaustedStopAsync(candidate.Task, candidate.Team, reason, ct);
        }
    }
}
