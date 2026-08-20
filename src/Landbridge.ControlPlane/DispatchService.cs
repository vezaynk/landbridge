using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Landbridge.Contracts;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Landbridge.ControlPlane;

/// <summary>
/// Turns submitted tasks into running dispatches, spec §6/§10. On start it scans
/// the submitted backlog once, then wakes on task-event NOTIFYs (and on a nudge
/// after a machine becomes ready) to run another dispatch pass. A pass hands
/// eligible submitted tasks to ready machines via
/// <see cref="SessionStore.DispatchNextAsync"/> — whose SKIP LOCKED claim picks the
/// task, does the submitted→working transition, and mints the worker instance
/// <em>before</em> the command is sent, so a failed send — or a throw anywhere
/// between that commit and the send — requeues a now-working task rather than
/// losing it (§10 best-effort commands).
///
/// A liveness timer requeues working tasks on either of two clocks — no
/// process-aliveness signal, or no forward progress for far longer (see
/// <see cref="CheckLivenessAsync"/>) — and requeue-on-disconnect (the socket loop
/// calling the event sink) covers a vanished machine. Fine-grained ack-vs-liveness
/// split is deferred.
///
/// <para>Both clocks read the in-memory registry, so the other half of that story is
/// <see cref="RehydrateMachineAsync"/>: a reconnecting machine's in-flight dispatches are
/// re-adopted from committed state, which is what puts a task that outlived a plane
/// restart back under a clock instead of leaving it stranded in <c>working</c> (§10, #86).</para>
/// </summary>
public sealed class DispatchService : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RunnerConnectionRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<DispatchService> _logger;
    private readonly SessionEventListener? _listener;
    private readonly TimeSpan _livenessWindow;
    private readonly TimeSpan _noProgressCeiling;
    private readonly string _publicMcpUrl;

    /// <summary>Aliveness clock default: landbridged asserts process-alive far more often
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
    public const string DefaultPublicMcpUrl = "http://127.0.0.1:5050";

    /// <summary>
    /// How long the plane keeps expecting the <c>exited</c> its own liveness-loss
    /// <c>kill</c> will produce (§10, #84). The echo is one socket round trip plus a process
    /// death away, so this is generous by orders of magnitude; what bounds it at the top is
    /// that a stale expectation swallows one genuine exit of a <em>successor</em> attempt,
    /// which then waits for <see cref="DefaultLivenessWindow"/> instead. Both errors are
    /// self-healing, so the window sits well clear of the echo and well inside the aliveness
    /// clock that backstops the other side.
    /// </summary>
    public static readonly TimeSpan CommandedExitEchoWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The plane's tracing source (§1). The dispatch span opened here continues
    /// the Lead's create_session trace and its W3C id is what rides the wire to the
    /// runner. Register it with the host's TracerProvider (Landbridge.Mcp/Program.cs)
    /// so the span exports.
    /// </summary>
    public const string ActivitySourceName = "Landbridge.ControlPlane";

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
        SessionEventListener? listener = null,
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
            await RunPassContainedAsync(ct); // startup backlog scan
            await foreach (var _ in _wake.Reader.ReadAllAsync(ct))
                await RunPassContainedAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _logger.LogError(e, "dispatch loop crashed");
        }
    }

    /// <summary>
    /// One pass, with its failure contained to itself — because a pass that throws must
    /// never be able to end the loop. This loop is the only thing that turns submitted
    /// tasks into running work, it is started once per process, and nothing restarts it:
    /// with the containment one level up (where the outer catch below sat alone), a single
    /// throw from a single pass — a token mint against a momentarily unreachable database
    /// is enough — stopped dispatch for the rest of the process's life, while the plane
    /// went on accepting tasks and reporting itself healthy. Every submission after that
    /// simply sat in <c>submitted</c>.
    ///
    /// <para>A bad pass is therefore logged and left behind; the next wake runs another
    /// one, and passes are idempotent by construction (the SKIP LOCKED claim), so nothing
    /// is owed to the one that failed. Shutdown still ends the loop, which is what the
    /// cancellation rethrow preserves.</para>
    /// </summary>
    private async Task RunPassContainedAsync(CancellationToken ct)
    {
        try
        {
            await RunDispatchPassAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "dispatch pass failed; the loop continues");
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
    /// SKIP LOCKED claim in <see cref="SessionStore.DispatchNextAsync"/> guarantees
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

    /// <summary>
    /// Re-adopts the dispatches a reconnecting machine already holds (§10, #86), called
    /// by the runner endpoint once the connection is registered and before its receive
    /// loop starts. Returns how many tasks were re-tracked.
    ///
    /// <para><b>Why it exists.</b> Dispatch→machine tracking lives only in
    /// <see cref="RunnerConnectionRegistry"/>, i.e. in this process's memory. A plane
    /// restart therefore comes back with an empty registry while the machines it dispatched
    /// to are still running that work — and nothing re-derived it: landbridged announces
    /// <c>rebooted</c> once per <em>process</em> start, so a socket reconnect re-announces
    /// nothing; <see cref="RunnerConnectionRegistry.RecordAlive"/> silently drops signals
    /// for a task it is not tracking; and <see cref="CheckLivenessAsync"/> only iterates
    /// what is tracked. A task left <c>working</c> across the restart was covered by no
    /// clock at all — stranded, with its own liveness signals discarded. Re-adopting on
    /// connect fixes it plane-side, keeping the runner dumb (§10 the-runner-is-transport):
    /// no new wire member, nothing for landbridged to remember.</para>
    ///
    /// <para><b>Instance fencing</b> (§9.14) is the store query's, not this method's — see
    /// <see cref="SessionStore.HeldDispatchesOnAsync"/>: only a task whose live incumbent
    /// instance was minted for <paramref name="machineId"/> is re-adopted, so a requeued
    /// task is never re-adopted and a stale instance's events are still refused.</para>
    ///
    /// <para><b>Clock initialization.</b> <see cref="RunnerConnectionRegistry.TrackDispatch"/>
    /// stamps both clocks at connect time. That is the only defensible choice of the three
    /// available: a pre-restart timestamp would make the aliveness clock fire on the first
    /// scan and requeue healthy work for the plane's own downtime, and leaving the clocks
    /// unset would restore tracking without restoring liveness. Starting at now costs at
    /// most one window of delayed detection for a machine that died during the restart,
    /// which the aliveness clock then catches normally.</para>
    /// </summary>
    public async Task<int> RehydrateMachineAsync(string machineId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SessionStore>();
        var held = await store.HeldDispatchesOnAsync(machineId, ct);
        foreach (var task in held)
            _registry.TrackDispatch(machineId, task);
        if (held.Count > 0)
            _logger.LogInformation(
                "re-adopted {Count} in-flight task(s) on reconnecting machine {Machine}: {Tasks}",
                held.Count, machineId, string.Join(",", held));
        return held.Count;
    }

    private enum DispatchOutcome { Dispatched, NothingEligible, SendFailed }

    private async Task<DispatchOutcome> TryDispatchOneAsync(
        string machineId, MachineSnapshot snapshot, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SessionStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

        var instance = WorkerInstanceId.New();
        // §6/§11 continuation targeting: hand the store the full set of connected
        // machines so its preferred-machine claim can tell "gone" (absent here) from
        // "busy elsewhere". A continuation prefers its own machine; only a Degrade
        // task whose machine is absent here is claimable by this one (cold-start).
        var result = await store.DispatchNextAsync(snapshot, instance, ct, _registry.MachineIds());
        if (result is not StoreResult.Applied applied)
            return DispatchOutcome.NothingEligible; // no eligible submitted task for this machine

        var task = applied.Session;
        var profile = task.Profile
            ?? throw new InvalidOperationException($"session {task.Id} has no profile");

        // §1 tracing: open the dispatch span, parented on the Lead's create_session
        // trace context stored on the row (opaque transport metadata). This span's
        // W3C id is what the send delegate stamps onto the wire envelope, so the
        // runner — and the worker it spawns — continue the same trace. A null
        // context (or no listener) yields a root span (or no span); dispatch is
        // unaffected either way. Held open across the send so Activity.Current
        // flows into the encode call on the send path.
        using var activity = StartDispatchActivity(task.Id, machineId, profile, applied.TraceContext);

        try
        {
            // §5, §13: mint the worker token for this instance; landbridged injects it,
            // wrapped in the MCP client config the worker dials the plane with.
            var minted = await tokens.MintWorkerTokenAsync(task.Team, task.Id, instance, ct);
            var command = new DispatchCommand(
                task.Id, profile, minted.Token, McpConfigJson: BuildWorkerMcpConfig(minted.Token),
                // #112 G2: the plane URL as a spawn substitution so a profile can write
                // a harness-native MCP file without parsing Claude's mcp.json. The runner
                // already consumes SpawnSubstitutions; this is the first producer.
                SpawnSubstitutions: new Dictionary<string, string> { ["mcp_url"] = _publicMcpUrl },
                // §11 resume: pass the prior work session's ref (present when this task
                // was worked before and parked/requeued) so landbridged continues the
                // transcript. Opaque metadata surfaced by the store; landbridged resumes
                // only if the resolved profile declares resume.args, else cold-starts.
                ResumeSessionRef: applied.Session.PendingSpawn == PendingSpawn.Load
                    ? applied.HarnessSessionRef
                    : null,
                // §7/§11 directory inheritance: whose work dir this dispatch runs in. A property
                // of continuation itself, NOT of transcript resume (#122) — a continuation works
                // where its predecessor worked whether or not it inherits the session, so this
                // rides every continuation dispatch including a degrade cold-start, where
                // ResumeSessionRef above is deliberately null. Transcript resume additionally
                // depends on it, since a harness session resumes only from the directory that
                // created it. Null for a same-task park-resume, whose dir the runner already picks.
                WorkDirSession: applied.WorkDirSession);

            _registry.TrackDispatch(machineId, task.Id);
            if (await _registry.SendAsync(machineId, command, ct))
                return DispatchOutcome.Dispatched;

            _logger.LogWarning("dispatch send failed for task {Task} on {Machine}; requeued", task.Id, machineId);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A throw between the committed submitted→working transition and the send leaves
            // exactly the wreckage a failed send leaves — a working row that nothing is
            // working on — so it takes the same remedy rather than escaping. The mint above
            // can fail for reasons that have nothing to do with this task — it is a database
            // write, and a transient fault there is one the next attempt would sail through
            // — which is the whole of what makes this reachable in a healthy system.
            //
            // Uncaught, such a throw left the row stranded working AND untracked, the worst
            // of the shapes available: nothing tracks it, so the liveness scan never walks
            // it and no clock covers it, and only a machine reconnect's rehydration would
            // ever put it back under one — the #86 symptom by a new route. Shutdown
            // cancellation is left to propagate instead: nothing ran, and the restarted
            // plane re-adopts the instance on reconnect and lets the aliveness clock
            // reclaim it.
            _logger.LogError(
                e, "dispatch of task {Task} to {Machine} failed after the claim; requeued",
                task.Id, machineId);
        }

        // §10: the submitted→working transition already committed, but nothing is
        // running — requeue against the infrastructure counter.
        _registry.Untrack(task.Id);
        await RequeueUndispatchedAsync(task.Id, ct);
        return DispatchOutcome.SendFailed;
    }

    /// <summary>
    /// Frees a task that was claimed but never started (§10 best-effort commands). The
    /// submitted→working transition has committed, so only a transition of its own puts the
    /// task back in the queue — which also revokes the instance minted for the dead attempt
    /// (§9.14) and counts against the infrastructure requeue cap (§9 check 7).
    ///
    /// <para>On a scope of its own, deliberately. The caller's store shares one DbContext
    /// with the token mint, and a mint that threw part-way through its <c>SaveChanges</c>
    /// leaves its credential row tracked as Added on that context — so requeueing through
    /// the same store would re-attempt the very write that just failed and lose the requeue
    /// to the same exception. A fresh scope owes nothing to whatever broke.</para>
    /// </summary>
    private async Task RequeueUndispatchedAsync(SessionId task, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SessionStore>();
        await store.ApplyAsync(task, new LivenessLost(LivenessLossReason.AckTimeout), ct);
    }

    /// <summary>
    /// Opens the dispatch span (§1), parented on the row's stored create_session
    /// trace context when present so one trace spans create_session → dispatch →
    /// runner → worker. Returns null when nothing is listening — tracing then
    /// no-ops and dispatch proceeds unchanged.
    /// </summary>
    private static Activity? StartDispatchActivity(
        SessionId task, string machineId, string profile, string? parentTraceparent)
    {
        var activity = parentTraceparent is not null
            && ActivityContext.TryParse(parentTraceparent, null, out var parent)
                ? ActivitySource.StartActivity($"dispatch {task}", ActivityKind.Producer, parent)
                : ActivitySource.StartActivity($"dispatch {task}", ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag("landbridge.session_id", task.ToString());
            activity.SetTag("landbridge.machine_id", machineId);
            activity.SetTag("landbridge.profile", profile);
        }
        return activity;
    }

    /// <summary>
    /// One of the two carriers a worker's identity reaches the harness by (§13): Claude
    /// Code's <c>--mcp-config</c> HTTP shape, with the freshly-minted worker token as a
    /// bearer header. landbridged writes it to <c>{work_dir}/mcp.json</c> (0600) and
    /// substitutes the path into the profile's spawn argv — the runner never
    /// interprets it, it is transport (§10). Built with the DOM so the token is
    /// escaped correctly and no serializer reflection is needed.
    ///
    /// <para>The other carrier is <c>LANDBRIDGE_WORKER_TOKEN</c>, stamped on every spawn.
    /// The plane still always <em>sends</em> this JSON — it cannot see the profile —
    /// but landbridged writes the file only when spawn/resume argv names
    /// <c>{mcp_config}</c> (#112 G11). A Grok/Codex/OpenCode profile that never
    /// references it must not leave a live bearer on disk.</para>
    /// </summary>
    private string BuildWorkerMcpConfig(string workerToken) =>
        new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["landbridge"] = new JsonObject
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
    /// suspended (§11) and are left tracked; terminal tasks are simply
    /// untracked. A report keeps the process and is skipped as Lead-owed
    /// (<see cref="SessionStore.IsAwaitingLeadAsync"/>).
    ///
    /// <para><b>Aliveness</b> (<see cref="_livenessWindow"/>, default 60s): landbridged
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
    ///
    /// <para><b>The requeue is fenced on the attempt it judged</b> (§9 check 14,
    /// <see cref="LivenessLost.Instance"/>). A scan reads the row, decides, and then applies —
    /// separate round trips — and the task can move in between: a permission request parks it
    /// while deliberately keeping its live incumbent (§11), and a requeue from elsewhere plus a
    /// redispatch replaces the attempt outright. Unfenced, the requeue landed on whatever the
    /// row held when it arrived, so a task that had just blocked was requeued against §9 check
    /// 7 and the worker still alive inside the tool call it was waiting in was killed. Now such
    /// a loss is refused — and refused without untracking, so nothing is left uncovered.</para>
    ///
    /// <para><b>A requeue takes the process down too</b>, where the plane has a channel to
    /// take it down with (<see cref="KillAbandonedDispatchAsync"/>, #84). Requeueing revoked
    /// the attempt's authorization and freed the task, but on its own it said nothing to the
    /// machine — so the wedged-but-alive harness this reclaims kept running and kept
    /// spending model tokens until its <c>landbridged</c> restarted and the stray sweep reaped
    /// it. It is a <c>kill</c> rather than the graceful <c>stop</c> of §11: stop's value is
    /// its injected wind-down turn, and a harness that has just failed a liveness clock has
    /// demonstrably stopped reading its input.</para>
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
            var store = scope.ServiceProvider.GetRequiredService<SessionStore>();
            // State AND incumbent, in one read: this decision is about one attempt of one
            // state, and the requeue below is fenced on exactly the pair read here (§9 check
            // 14) so it cannot land on a task that moved between this line and that one.
            switch (await store.GetIncumbentDispatchAsync(tracked.Session, ct))
            {
                case { State: SessionState.Working } dispatch:
                    // The progress ceiling alone does not requeue a service-bearing
                    // task; the aliveness clock still does.
                    if (!notAlive && await store.HasRegisteredServiceAsync(tracked.Session, ct))
                    {
                        _logger.LogDebug(
                            "task {Task} on {Machine} has no progress for {Idle} but bears a registered service; not requeued",
                            tracked.Session, tracked.Machine, now - tracked.LastProgress);
                        break;
                    }

                    if (await store.IsAwaitingLeadAsync(tracked.Session, ct))
                    {
                        _logger.LogDebug(
                            "task {Task} on {Machine} is awaiting a Lead follow-up; not requeued",
                            tracked.Session, tracked.Machine);
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
                        tracked.Session, tracked.Machine, reason,
                        now - tracked.LastActivity, now - tracked.LastProgress);
                    var requeue = await store.ApplyAsync(
                        tracked.Session, new LivenessLost(reason, dispatch.Instance), ct);
                    if (requeue is not StoreResult.Applied applied)
                    {
                        // The fence refused, or the row lost the xmin race: the attempt this
                        // scan judged is not the one on the task any more. Whatever moved it
                        // owns it now, so nothing is requeued, nothing is killed — and the
                        // task stays TRACKED, because the two things it can have moved to
                        // both need that. A permission request keeps its live worker and its
                        // machine assignment, which is what the wait-TTL sweeper resolves a
                        // blocked task's machine through (§11), and a successor dispatch is
                        // under these same clocks. Untracking either would strand it with
                        // nothing watching, the #86 symptom by a new route.
                        _logger.LogInformation(
                            "liveness requeue of task {Task} on {Machine} did not apply ({Result}); " +
                            "the dispatch it judged has moved on",
                            tracked.Session, tracked.Machine, requeue.GetType().Name);
                        break;
                    }
                    if (applied.Session.State == SessionState.Failed)
                        _logger.LogWarning(
                            "task {Task} failed ({Reason}) after {Requeues} infrastructure losses; waiting for the Lead",
                            tracked.Session, reason, applied.Session.InfrastructureRequeues);
                    _registry.Untrack(tracked.Session);
                    // Only once the plane has actually given up on this dispatch: a refused
                    // transition means it has not, and killing a process the plane still
                    // considers live would destroy work nothing requeued.
                    await KillAbandonedDispatchAsync(tracked.Session, tracked.Machine, ct);
                    break;
                case null:
                case { State: SessionState.Completed or SessionState.Rejected
                    or SessionState.Canceled or SessionState.Failed }:
                    _registry.Untrack(tracked.Session);
                    break;
                    // blocked_on_input / parked / submitted: leave tracked (§11).
            }
        }
    }

    /// <summary>
    /// Kills the harness of a dispatch the liveness scan has just requeued, where the
    /// machine is still connected (§10, #84). Best-effort and consequence-free on failure:
    /// the requeue has already committed, so a machine that cannot be reached simply keeps
    /// whatever it is running until its own daemon restarts and the §10 stray sweep reaps
    /// it — exactly the behaviour before this existed.
    ///
    /// <para><b>The kill goes out after the requeue commits, never before.</b> Ordering it
    /// the other way loses either way round: the kill's <c>exited</c> can land while the task
    /// is still <c>working</c> and requeue it as
    /// <see cref="LivenessLossReason.ProcessExited"/>, so the reason on the trail becomes the
    /// symptom instead of the clock that fired (#73 persisted that distinction precisely
    /// because the remedies differ) — and if the plane's own <c>LivenessLost</c> then lands
    /// after a redispatch, the task pays two requeues against its cap for one loss. Committing
    /// first makes the plane's decision independent of whether the machine cooperates, which
    /// is what §10 means by best-effort commands: the task is free the moment the transition
    /// commits, and everything after is housekeeping that can fail.</para>
    ///
    /// <para><b>Nor can the kill reach the successor attempt.</b> The requeue's own
    /// <c>pg_notify</c> may redispatch this task immediately, and <c>kill</c> names only a
    /// task — so a kill overtaken by that redispatch would take down the replacement. It is
    /// not: a redispatch onto a <em>different</em> machine cannot be touched by a kill sent to
    /// this one (the runner kills out of its own supervised set), and onto this same machine
    /// both commands travel the one socket under the endpoint's send lock, in the order they
    /// were written. This write is issued in the same continuation as the commit, whereas the
    /// redispatch cannot even have started — it needs the notify to arrive, a pass to claim
    /// the row and a token to be minted — so the kill is already queued ahead of the
    /// successor's <c>DispatchCommand</c>, and the runner therefore executes it while the
    /// successor does not yet exist.</para>
    ///
    /// <para>Uniform across both clocks. An aliveness-loss requeue on a machine whose socket
    /// is up is the case where <c>landbridged</c> is reachable but has stopped reporting this
    /// task — its supervisor may well still be holding a live process — so it has the same
    /// stray to reap as the wedged-agent case. At the requeue cap the task is abandoned
    /// rather than requeued and this matters most: nothing will ever come back for that
    /// process.</para>
    ///
    /// <para>Deliberately not extended to the other requeue paths, which have no channel or
    /// nothing to kill: a disconnect requeue has lost the socket (and the machine's own
    /// dead-man switch and restart sweep cover it, §10); a <c>rebooted</c> requeue is on a
    /// live socket, but the daemon announcing it has already swept the previous generation's
    /// processes; a send-failure requeue never started anything; and a blocked task's harness
    /// has already exited by definition (§11).</para>
    /// </summary>
    private async Task KillAbandonedDispatchAsync(SessionId task, string machineId, CancellationToken ct)
    {
        if (await _registry.SendKillAsync(machineId, task, CommandedExitEchoWindow, ct))
        {
            _logger.LogInformation(
                "killed the requeued dispatch of task {Task} on {Machine}", task, machineId);
            return;
        }
        _logger.LogInformation(
            "no live channel to kill the requeued dispatch of task {Task} on {Machine}; " +
            "the machine's own restart sweep reaps it", task, machineId);
    }
}
