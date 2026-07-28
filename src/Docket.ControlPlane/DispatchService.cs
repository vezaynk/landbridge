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
/// A liveness timer requeues working tasks that go quiet past the window, and
/// requeue-on-disconnect (the socket loop calling the event sink) covers a
/// vanished machine. Fine-grained ack-vs-liveness split is deferred.
/// </summary>
public sealed class DispatchService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RunnerConnectionRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<DispatchService> _logger;
    private readonly TaskEventListener? _listener;
    private readonly TimeSpan _livenessWindow;
    private readonly string _publicMcpUrl;

    /// <summary>The default plane MCP URL a worker dials when config supplies none (§10).</summary>
    public const string DefaultPublicMcpUrl = "http://127.0.0.1:5000";

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
        string? publicMcpUrl = null)
    {
        _scopes = scopes;
        _registry = registry;
        _clock = clock;
        _logger = logger;
        _listener = listener;
        _livenessWindow = livenessWindow ?? TimeSpan.FromSeconds(60);
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
        var result = await store.DispatchNextAsync(snapshot, instance, ct);
        if (result is not StoreResult.Applied applied)
            return DispatchOutcome.NothingEligible; // no eligible submitted task for this machine

        var task = applied.Task;
        var profile = task.Profile ?? MachineSnapshot.DefaultProfile;

        // §5, §13: mint the worker token for this instance; docketd injects it,
        // wrapped in the MCP client config the worker dials the plane with.
        var minted = await tokens.MintWorkerTokenAsync(task.Team, task.Id, instance, ct);
        var command = new DispatchCommand(
            task.Id, profile, minted.Token, McpConfigJson: BuildWorkerMcpConfig(minted.Token));

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
    /// Requeues working tasks with no activity within the liveness window (§10
    /// per-task liveness). Tasks that are blocked_on_input/parked have liveness
    /// suspended (§11) and are left tracked; verifying/terminal tasks are simply
    /// untracked.
    /// </summary>
    public async Task CheckLivenessAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        foreach (var tracked in _registry.AllTracked())
        {
            if (now - tracked.LastActivity < _livenessWindow)
                continue;

            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<TaskStore>();
            var state = await store.GetStateAsync(tracked.Task, ct);
            switch (state)
            {
                case TaskState.Working:
                    await store.ApplyAsync(tracked.Task, new LivenessLost(LivenessLossReason.LivenessTimeout), ct);
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
    }
}
