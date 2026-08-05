using Docket.ControlPlane.Tests;
using Docket.Core;

namespace Docket.Chaos.Tests;

/// <summary>
/// Spec §17.8 — the chaos test. §17 is the build order, and its step 8 names ten
/// scenarios; this is the first slice, covering the ones that turn on a process dying
/// while work is in flight. Scenario-by-scenario coverage:
///
/// <list type="table">
/// <item><term>Kill a runner mid-task with siblings running</term><description>
///   <see cref="Sigkilled_docketd_requeues_every_task_it_held_and_the_restart_reaps_strays"/> —
///   two tasks in flight on one machine, both requeued by the one death.</description></item>
/// <item><term>SIGKILL docketd and restart it</term><description>same test: the restarted
///   daemon sweeps the previous generation's strays before accepting dispatch, then the
///   requeued work is redispatched and a fresh task completes end to end.</description></item>
/// <item><term>Replay a stale worker-instance token</term><description>
///   <see cref="A_requeued_dispatchs_worker_token_is_refused_when_replayed"/>.</description></item>
/// <item><term>Partition a machine</term><description>partially —
///   <see cref="A_wedged_worker_is_reclaimed_when_the_no_progress_ceiling_lapses"/> covers the
///   liveness half (the clocks that reclaim a task whose worker has stopped getting
///   anywhere). A true network partition, with the machine's socket held open, is not
///   covered here.</description></item>
/// </list>
///
/// <para>Not in this slice, and still open from §17.8: cancel with each disposition,
/// fail verification three times, sever a forward mid-transfer, evict a Lead
/// mid-decomposition, close a laptop and reattach, park a task and answer it after the
/// machine is gone.</para>
///
/// <para><b>Requeue counts are asserted tolerantly on purpose.</b> Infrastructure
/// requeues are capped (#73, default 5 per task) and a task that reaches its cap is
/// abandoned as <c>canceled</c>, so these scenarios assert "at least one requeue" and
/// "eventually leaves the state it was stuck in" — never an exact count, and never
/// retry-forever behaviour. Where a scenario could drift into the cap it says so and
/// asserts it has not; the cap itself is covered by
/// <c>Docket.Core.Tests.RequeueCapTests</c>.</para>
///
/// <para><b>Requeue reasons are read from committed state</b>
/// (<c>tasks.last_requeue_reason</c> and <c>task_events.liveness_reason</c>, both added
/// by #73), not from the plane's log. That matters most for the wedged-worker scenario,
/// where every clock bumps the same counter and only the reason distinguishes them.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChaosScenarioTests(PostgresFixture pg) : IAsyncLifetime
{
    /// <summary>
    /// Whole-scenario ceiling. Every wait inside is separately bounded and dumps
    /// diagnostics; this is the backstop that keeps a wedged rig from holding CI for the
    /// job timeout.
    /// </summary>
    private static readonly TimeSpan ScenarioBudget = TimeSpan.FromMinutes(3);

    /// <summary>How long a state transition driven by the plane's own loops may take.</summary>
    private static readonly TimeSpan TransitionBudget = TimeSpan.FromSeconds(45);

    public async Task InitializeAsync()
    {
        if (pg.Available)
            await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The rig itself, proven before any chaos is applied: a task created over real MCP
    /// is dispatched by the real plane to the real docketd, worked by the real scripted
    /// worker, and accepted by the Lead. If this fails, the chaos scenarios below are
    /// failing for a reason that has nothing to do with chaos — which is exactly what a
    /// baseline is for.
    /// </summary>
    [SkippableFact]
    public async Task A_task_completes_through_the_real_multi_process_fleet()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        Skip.If(OperatingSystem.IsWindows(), WindowsSkip);

        using var cts = new CancellationTokenSource(ScenarioBudget);
        var ct = cts.Token;
        await using var fleet = new ChaosFleet(pg, new ChaosFleetOptions());
        await fleet.StartAsync(ct);

        var task = await fleet.CreateTaskAsync("chaos baseline", profile: null, ct);
        await AssertReachesAsync(fleet, task, TaskState.Verifying, "the scripted worker never reported", ct);
        await fleet.AcceptAsync(task, ct);
        await AssertReachesAsync(fleet, task, TaskState.Completed, "the Lead's accept never committed", ct);
    }

    /// <summary>
    /// §17.8: "Kill a runner mid-task with siblings running" and "SIGKILL docketd and
    /// restart it".
    ///
    /// <para>Two tasks are working on the one machine and a tagged stray tree is planted
    /// beside them. docketd is then SIGKILLed — no handler, no flush, no child cleanup.
    /// What must hold:</para>
    /// <list type="number">
    /// <item>The plane notices the dropped socket and requeues BOTH tasks — the sibling
    /// blast radius is the whole machine, not just one task — each against the
    /// infrastructure counter, each with its worker instance revoked.</item>
    /// <item>A restarted docketd reaps the previous generation's strays BEFORE announcing
    /// itself, so it cannot inherit a port-holding orphan (§10 restart-equals-reboot,
    /// the guarantee that has to survive a hard crash precisely because a SIGKILLed
    /// daemon cleans up nothing).</item>
    /// <item>Nothing is lost: both requeued tasks are redispatched, each on exactly one
    /// live instance — a requeue must not leave two workers racing for one task.</item>
    /// <item>The fleet is genuinely healthy again — a fresh task runs the whole loop to
    /// completed.</item>
    /// </list>
    ///
    /// <para>The two in-flight tasks use the wedge profile deliberately: a task must
    /// still be <c>working</c> at the moment of the kill for the scenario to mean
    /// anything, and the reporting worker finishes in well under a second. The kill's
    /// blast radius is the assertion; what the workers were doing is not.</para>
    /// </summary>
    [SkippableFact]
    public async Task Sigkilled_docketd_requeues_every_task_it_held_and_the_restart_reaps_strays()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        Skip.If(OperatingSystem.IsWindows(), WindowsSkip);

        using var cts = new CancellationTokenSource(ScenarioBudget);
        var ct = cts.Token;
        // A long no-progress ceiling keeps the liveness sweeper out of this scenario:
        // the requeue asserted below must be the one the DISCONNECT caused.
        await using var fleet = new ChaosFleet(pg, new ChaosFleetOptions
        {
            NoProgressCeiling = TimeSpan.FromMinutes(10),
        });
        await fleet.StartAsync(ct);

        var first = await fleet.CreateTaskAsync("chaos sibling A", ChaosProfiles.Wedge, ct);
        var second = await fleet.CreateTaskAsync("chaos sibling B", ChaosProfiles.Wedge, ct);
        var siblings = new[] { first, second };
        foreach (var task in siblings)
            await AssertReachesAsync(fleet, task, TaskState.Working, "a sibling never reached working", ct);

        var beforeKill = new Dictionary<TaskId, TaskFacts>();
        foreach (var task in siblings)
            beforeKill[task] = (await fleet.FactsAsync(task, ct))!.Value;

        // Planted only now: had it existed before the FIRST docketd start, that start's
        // own sweep would have reaped it and there would be nothing left to prove.
        var stray = await fleet.PlantStrayAsync(ct);

        await fleet.SigkillDocketdAsync();

        // ── 1. Both siblings requeued by the one death.
        foreach (var task in siblings)
        {
            await AssertReachesAsync(fleet, task, TaskState.Submitted,
                "a sibling was not requeued after docketd was SIGKILLed", ct, siblings);
            var facts = (await fleet.FactsAsync(task, ct))!.Value;
            Assert.True(facts.InfrastructureRequeues > beforeKill[task].InfrastructureRequeues,
                $"task {task} did not count an infrastructure requeue " +
                $"({beforeKill[task].InfrastructureRequeues} → {facts.InfrastructureRequeues})\n" +
                await fleet.DiagnoseAsync(siblings, ct));
            Assert.Null(facts.CurrentInstanceId);
            Assert.Equal(0, facts.LiveInstanceCount);

            // The requeue must be attributed to the machine going away, not to a liveness
            // clock lapsing — those are different scenarios, and before the reason was
            // persisted (#73) the only way to keep them apart here was to push the
            // no-progress ceiling far out and trust it. Now it is assertable.
            //
            // Asserted over the whole requeue trail rather than as the LAST reason,
            // because the two siblings do not end up with the same trail. The disconnect
            // requeues both with MachineReboot, and that commit's notify wakes the
            // dispatch loop while the dying connection is still registered
            // (RunnerEndpoint requeues in its `finally` BEFORE unregistering). A pass
            // claims ONE task, cannot write to the dead socket, and requeues it again as
            // AckTimeout; by the next pass the connection is gone, so the other sibling
            // keeps the bare MachineReboot trail. Which of the two pays the extra requeue
            // varies per run. Both reasons are the same underlying fact — the machine is
            // gone — so what is worth asserting is that the trail says machine-gone and
            // that NEITHER liveness clock fired.
            var reasons = await fleet.RequeueReasonsAsync(task, ct);
            Assert.Contains(LivenessLossReason.MachineReboot, reasons);
            Assert.DoesNotContain(LivenessLossReason.NoProgress, reasons);
            Assert.DoesNotContain(LivenessLossReason.LivenessTimeout, reasons);
        }

        // ── 2. The restart sweep runs before the daemon accepts anything.
        var up = await fleet.StartDocketdAsync(ct);
        Assert.True(ParseStraysReaped(up) >= 1,
            $"the restarted docketd reaped nothing; the planted stray tree was still tagged " +
            $"with machine {fleet.MachineId}\n{up}\n" + await fleet.DiagnoseAsync(siblings, ct));
        Assert.True(
            await ChaosFleet.WaitUntilAsync(() => Task.FromResult(!stray.AnyAlive), TransitionBudget, ct),
            "the planted stray tree survived the restart sweep\n" + await fleet.DiagnoseAsync(siblings, ct));

        // ── 3. The requeued work is picked back up, once each.
        foreach (var task in siblings)
        {
            await AssertReachesAsync(fleet, task, TaskState.Working,
                "a requeued sibling was never redispatched after docketd came back", ct, siblings);
            var facts = (await fleet.FactsAsync(task, ct))!.Value;
            Assert.NotEqual(beforeKill[task].CurrentInstanceId, facts.CurrentInstanceId);
            Assert.Equal(1, facts.LiveInstanceCount);
        }

        // ── 4. The whole loop still works, not just the requeue path.
        var after = await fleet.CreateTaskAsync("chaos post-restart", profile: null, ct);
        await AssertReachesAsync(fleet, after, TaskState.Verifying,
            "a task created after the restart never reached verifying", ct, siblings.Append(after));
        await fleet.AcceptAsync(after, ct);
        await AssertReachesAsync(fleet, after, TaskState.Completed,
            "a task created after the restart never completed", ct, siblings.Append(after));
    }

    /// <summary>
    /// §17.8: "Replay a stale worker-instance token."
    ///
    /// <para>The token replayed here is the real thing: read out of the <c>mcp.json</c>
    /// docketd generated for the live dispatch (§13), which is the very credential the
    /// running worker is authenticating with. The task is then requeued out from under
    /// it by killing docketd, and the token is presented again over the real MCP
    /// surface.</para>
    ///
    /// <para>What §9.14 promises is that an orphaned harness "holds a token that is
    /// already dead". Note WHERE that bites: a worker token carries no state of its own,
    /// so revoking the instance on requeue kills the credential itself and the replay is
    /// refused at authentication — the request never reaches a tool, and never reaches
    /// the engine's <c>IncumbentInstanceOnly</c> check. So this asserts the observable
    /// production behaviour (the call is refused) rather than a specific rule name; the
    /// engine-level fence is separately unit-tested. Either an auth failure at connect
    /// or an error from the call satisfies it — which of the two surfaces first is an MCP
    /// client-library detail, not a control-plane guarantee.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_requeued_dispatchs_worker_token_is_refused_when_replayed()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        Skip.If(OperatingSystem.IsWindows(), WindowsSkip);

        using var cts = new CancellationTokenSource(ScenarioBudget);
        var ct = cts.Token;
        await using var fleet = new ChaosFleet(pg, new ChaosFleetOptions
        {
            NoProgressCeiling = TimeSpan.FromMinutes(10),
        });
        await fleet.StartAsync(ct);

        var task = await fleet.CreateTaskAsync("chaos stale token", ChaosProfiles.Wedge, ct);
        await AssertReachesAsync(fleet, task, TaskState.Working, "the task never reached working", ct);

        string? stale = null;
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                async () => (stale = await fleet.InjectedWorkerTokenAsync(task, ct)) is not null,
                TransitionBudget, ct),
            "docketd never wrote the worker's mcp.json\n" + await fleet.DiagnoseAsync([task], ct));

        // Sanity: this credential is live right now. Without this the test could pass
        // against a token that was never valid in the first place.
        await using (var worker = await fleet.ConnectMcpAsync(stale!, ct))
        {
            var assignment = await worker.CallToolAsync(
                "get_task", new Dictionary<string, object?>(), cancellationToken: ct);
            Assert.NotEqual(true, assignment.IsError);
        }

        await fleet.SigkillDocketdAsync();
        await AssertReachesAsync(fleet, task, TaskState.Submitted,
            "the task was not requeued, so its instance was never revoked", ct);

        var refused = await IsRefusedAsync(fleet, stale!, ct);
        Assert.True(refused,
            "the predecessor instance's worker token was still accepted after its task was requeued\n" +
            await fleet.DiagnoseAsync([task], ct));
    }

    /// <summary>
    /// §17.8: "Partition a machine" — the liveness half.
    ///
    /// <para>A worker that is running but getting nowhere is the case the §10 two clocks
    /// exist to separate, and it is the one a single number cannot express: docketd keeps
    /// reporting <c>alive</c> for the process every heartbeat, so the aliveness clock
    /// stays fresh forever and only the no-progress ceiling can reclaim the task. This
    /// wedges a real worker (a process that emits nothing and registers no service —
    /// note a registered service would exempt it from the no-progress clock by design,
    /// since babysitting a service legitimately looks like no progress) and asserts the
    /// task is reclaimed once the ceiling lapses.</para>
    ///
    /// <para>Tolerant by construction (#73): it asserts the task leaves <c>working</c>
    /// with at least one infrastructure requeue counted and that every requeue so far was
    /// attributed to no-progress — not how many times it is retried afterwards. The wedge
    /// is deterministic, so redispatch wedges again and the task would eventually reach
    /// its cap and be abandoned; that end state belongs to the cap's own tests, so this
    /// one asserts it stops short of it.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_wedged_worker_is_reclaimed_when_the_no_progress_ceiling_lapses()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        Skip.If(OperatingSystem.IsWindows(), WindowsSkip);

        using var cts = new CancellationTokenSource(ScenarioBudget);
        var ct = cts.Token;
        await using var fleet = new ChaosFleet(pg, new ChaosFleetOptions
        {
            // Heartbeat ≪ aliveness window, so the wedged worker keeps looking alive and
            // the ONLY clock that can fire is the no-progress ceiling.
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            PerTaskLivenessWindow = TimeSpan.FromSeconds(5),
            NoProgressCeiling = TimeSpan.FromSeconds(6),
        });
        await fleet.StartAsync(ct);

        var task = await fleet.CreateTaskAsync("chaos wedged worker", ChaosProfiles.Wedge, ct);
        await AssertReachesAsync(fleet, task, TaskState.Working, "the wedged worker never started", ct);
        var working = (await fleet.FactsAsync(task, ct))!.Value;

        // The sweep period is the aliveness window, so the reclaim lands within roughly
        // one ceiling plus one period; the budget is far wider than that.
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                async () =>
                {
                    var facts = await fleet.FactsAsync(task, ct);
                    return facts is { } f && f.InfrastructureRequeues > working.InfrastructureRequeues;
                },
                TransitionBudget, ct),
            "the wedged worker's task was never reclaimed by the no-progress ceiling\n" +
            await fleet.DiagnoseAsync([task], ct));

        // Which clock fired is the whole point, and both clocks bump the same counter —
        // so without this the test would equally pass if docketd had simply stopped
        // reporting the worker alive, which is a different scenario with a different
        // detection time and a different remedy (a machine problem, not a wedged agent).
        // Since #73 the reason is committed on both surfaces, so this reads durable state
        // rather than scraping a log line.
        var reasons = await fleet.RequeueReasonsAsync(task, ct);
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.Equal(LivenessLossReason.NoProgress, reason));
        var reclaimed = (await fleet.FactsAsync(task, ct))!.Value;
        Assert.Equal(LivenessLossReason.NoProgress, reclaimed.LastRequeueReason);

        // The wedge is deterministic, so it re-wedges on every redispatch and would walk
        // the task to its requeue cap and abandon it (§9 check 7) if this ran long
        // enough. Asserting we are still short of the cap is what keeps this a test about
        // the no-progress clock rather than an accidental test of the cap — which has its
        // own coverage in Docket.Core.Tests.RequeueCapTests.
        Assert.True(reclaimed.InfrastructureRequeues < reclaimed.InfrastructureRequeueLimit,
            $"the wedge reached its requeue cap ({reclaimed.InfrastructureRequeues}/" +
            $"{reclaimed.InfrastructureRequeueLimit}) before this scenario could assert on it\n" +
            await fleet.DiagnoseAsync([task], ct));
    }

    // ── Shared assertions ───────────────────────────────────────────────────────

    private const string WindowsSkip =
        "the §10 restart sweep is a documented deferral on Windows (NullProcessInventory), " +
        "so a stray-reaping scenario there would assert nothing";

    /// <summary>
    /// Waits for a committed state with a hard deadline, and on expiry fails with the
    /// full diagnostics dump — the timeline, every process's output tail, and each
    /// task's row, instances and event log. A chaos suite whose failures are not
    /// self-explaining costs more than it buys.
    /// </summary>
    private static async Task AssertReachesAsync(
        ChaosFleet fleet, TaskId task, TaskState expected, string because,
        CancellationToken ct, IEnumerable<TaskId>? context = null)
    {
        if (await fleet.WaitForStateAsync(task, expected, TransitionBudget, ct))
            return;
        var actual = await fleet.StateAsync(task, ct);
        Assert.Fail(
            $"{because}: task {task} is {actual?.ToString() ?? "(gone)"}, expected {expected} " +
            $"within {TransitionBudget}\n" + await fleet.DiagnoseAsync(context ?? [task], ct));
    }

    /// <summary>
    /// Whether <paramref name="bearer"/> is refused by the plane. A dead credential can
    /// surface either as a failure to establish the MCP session or as an error on the
    /// call, depending on when the client library first sees the 401; both are the same
    /// control-plane fact.
    /// </summary>
    private static async Task<bool> IsRefusedAsync(ChaosFleet fleet, string bearer, CancellationToken ct)
    {
        try
        {
            await using var client = await fleet.ConnectMcpAsync(bearer, ct);
            var result = await client.CallToolAsync(
                "get_task", new Dictionary<string, object?>(), cancellationToken: ct);
            return result.IsError == true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the scenario budget expired — not a refusal
        }
        catch
        {
            return true; // connect or call rejected outright
        }
    }

    /// <summary>
    /// The count out of docketd's announcement line, which §10 prints only after the
    /// restart sweep: <c>docketd up: machine=… profiles=[…] strays_reaped=N control=…</c>.
    /// Asserted as a lower bound, never an equality — the reaper counts pids it resolved,
    /// so a stray that dies on its own between the scan and the kill still counts, and a
    /// tree kill can take the grandchild before the loop reaches it.
    /// </summary>
    private static int ParseStraysReaped(string upLine)
    {
        const string key = "strays_reaped=";
        var at = upLine.IndexOf(key, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no strays_reaped in docketd's announcement: {upLine}");
        var rest = upLine[(at + key.Length)..];
        var end = rest.IndexOf(' ');
        return int.Parse(end >= 0 ? rest[..end] : rest);
    }
}
