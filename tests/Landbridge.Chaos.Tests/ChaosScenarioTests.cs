using Landbridge.ControlPlane.Tests;
using Landbridge.Core;

namespace Landbridge.Chaos.Tests;

/// <summary>
/// Spec §17.8 — the chaos test. §17 is the build order, and its step 8 names ten
/// scenarios; this is the first slice, covering the ones that turn on a process dying
/// while work is in flight. Scenario-by-scenario coverage:
///
/// <list type="table">
/// <item><term>Kill a runner mid-task with siblings running</term><description>
///   <see cref="Sigkilled_landbridged_requeues_every_task_it_held_and_the_restart_reaps_strays"/> —
///   two tasks in flight on one machine, both requeued by the one death.</description></item>
/// <item><term>SIGKILL landbridged and restart it</term><description>same test: the restarted
///   daemon sweeps the previous generation's strays before accepting dispatch, then the
///   requeued work is redispatched and a fresh task completes end to end.</description></item>
/// <item><term>Replay a stale worker-instance token</term><description>
///   <see cref="A_requeued_dispatchs_worker_token_is_refused_when_replayed"/>.</description></item>
/// <item><term>Partition a machine</term><description>partially —
///   <see cref="A_wedged_worker_is_reclaimed_when_the_no_progress_ceiling_lapses"/> covers the
///   liveness half (the clocks that reclaim a task whose worker has stopped getting
///   anywhere), and
///   <see cref="A_reclaimed_wedged_workers_process_is_killed_rather_than_left_running"/> the
///   half after it: the reclaimed worker's process is taken down rather than left burning
///   (#84). A true network partition, with the machine's socket held open, is not
///   covered here.</description></item>
/// <item><term>Restart the plane mid-task</term><description>
///   <see cref="A_task_in_flight_survives_a_plane_restart_and_stays_under_its_liveness_clocks"/> —
///   the control plane is SIGKILLed under live work and replaced; the reconnecting machine's
///   in-flight task is re-adopted and its liveness clocks resume. This one was unwritable as
///   a passing test until #86 was fixed: dispatch tracking was memory-only and never
///   rehydrated, so the task was stranded in <c>working</c> forever.</description></item>
/// <item><term>Close a laptop and reattach</term><description>
///   <see cref="A_superseded_connection_tearing_down_costs_the_reattached_machine_nothing"/> —
///   one machine holds two <c>/runner</c> connections, and the older one's teardown must cost
///   the reattached machine nothing (#94). The half-open socket is approximated by a second
///   real connection that never sends; see that test for why that is the faithful part.</description></item>
/// </list>
///
/// <para>Not in this slice, and still open from §17.8: cancel with each disposition,
/// fail verification three times, sever a forward mid-transfer, evict a Lead
/// mid-decomposition, park a task and answer it after the machine is gone.</para>
///
/// <para><b>Requeue counts are asserted tolerantly except where a count is the point.</b>
/// Infrastructure requeues are capped (#73, default 5 per task) and a task that reaches its
/// cap is abandoned as <c>canceled</c>, so a scenario about some other mechanism asserts "at
/// least one requeue" and "eventually leaves the state it was stuck in" rather than an exact
/// count, and never asserts retry-forever behaviour. Where a scenario could drift into the
/// cap it says so and asserts it has not; the cap itself is covered by
/// <c>Landbridge.Core.Tests.RequeueCapTests</c>. The two disconnect/restart scenarios are the
/// exception and assert the trail exactly, because "one requeue per disconnect" is the
/// property under test (#87) — a doubled requeue is invisible to a tolerant assertion and
/// costs a flapping machine its cap in three disconnects instead of five.</para>
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
    /// is dispatched by the real plane to the real landbridged, worked by the real scripted
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

        var task = await fleet.CreateSessionAsync("chaos baseline", profile: null, ct);
        await AssertReportedAsync(fleet, task, "the scripted worker never reported", ct);
        await fleet.AcceptAsync(task, ct);
        await AssertReachesAsync(fleet, task, SessionState.Completed, "the Lead's accept never committed", ct);
    }

    /// <summary>
    /// §17.8: "Kill a runner mid-task with siblings running" and "SIGKILL landbridged and
    /// restart it".
    ///
    /// <para>Two tasks are working on the one machine and a tagged stray tree is planted
    /// beside them. landbridged is then SIGKILLed — no handler, no flush, no child cleanup.
    /// What must hold:</para>
    /// <list type="number">
    /// <item>The plane notices the dropped socket and requeues BOTH tasks — the sibling
    /// blast radius is the whole machine, not just one task — each against the
    /// infrastructure counter, each with its worker instance revoked.</item>
    /// <item>A restarted landbridged reaps the previous generation's strays BEFORE announcing
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
    public async Task Sigkilled_landbridged_requeues_every_task_it_held_and_the_restart_reaps_strays()
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

        var first = await fleet.CreateSessionAsync("chaos sibling A", ChaosProfiles.Wedge, ct);
        var second = await fleet.CreateSessionAsync("chaos sibling B", ChaosProfiles.Wedge, ct);
        var siblings = new[] { first, second };
        foreach (var task in siblings)
            await AssertReachesAsync(fleet, task, SessionState.Working, "a sibling never reached working", ct);

        // Both workers must really be running before the kill. `working` is committed before
        // the DispatchCommand is sent, so a task can be working with its command still in
        // flight and no process behind it — and such a task reports no aliveness, so the
        // aliveness clock would add a LivenessTimeout to the requeue trail asserted exactly
        // below. Waiting for each worker's own start marker removes that window and makes
        // "two tasks in flight" true rather than assumed.
        foreach (var task in siblings)
            Assert.True(
                await ChaosFleet.WaitUntilAsync(
                    () => Task.FromResult(fleet.WorkerStarted(task)), TransitionBudget, ct),
                $"sibling {task} never wrote its start marker, so it was not actually in " +
                $"flight when landbridged was killed\n" + await fleet.DiagnoseAsync(siblings, ct));

        var beforeKill = new Dictionary<SessionId, TaskFacts>();
        foreach (var task in siblings)
            beforeKill[task] = (await fleet.FactsAsync(task, ct))!.Value;

        // Planted only now: had it existed before the FIRST landbridged start, that start's
        // own sweep would have reaped it and there would be nothing left to prove.
        var stray = await fleet.PlantStrayAsync(ct);

        await fleet.SigkillLandbridgedAsync();

        // ── 1. Both siblings failed by the one death. The plane does not requeue.
        foreach (var task in siblings)
        {
            await AssertReachesAsync(fleet, task, SessionState.Failed,
                "a sibling was not failed after landbridged was SIGKILLed", ct, siblings);
            var facts = (await fleet.FactsAsync(task, ct))!.Value;
            Assert.True(facts.InfrastructureRequeues == beforeKill[task].InfrastructureRequeues + 1,
                $"task {task} counted {facts.InfrastructureRequeues - beforeKill[task].InfrastructureRequeues} " +
                $"infrastructure requeue(s) for one disconnect, expected exactly 1 " +
                $"({beforeKill[task].InfrastructureRequeues} → {facts.InfrastructureRequeues})\n" +
                await fleet.DiagnoseAsync(siblings, ct));
            // Sampled the instant the task is observed submitted, deliberately, and NOT
            // behind a settle-wait. This pair is what caught #87 in CI on master (run
            // 31200118872): the requeue's notify woke a dispatch pass while the dead
            // connection was still registered, the pass claimed the task and minted a fresh
            // instance, and this Assert.Null sampled that transient and failed holding a live
            // guid. Unregistering first removes the mechanism rather than the symptom — the
            // SIGKILL leaves this single-machine fleet with nothing registered, so
            // ReadyMachines is empty and no pass can claim the task until step 2 restarts
            // landbridged. There is no longer a window to sample, which is why this is now stable.
            //
            // Adding a settle-wait here would make the assertion vacuous: it would let a
            // returning race finish its second requeue and then observe a null instance
            // again, hiding exactly the transient this is positioned to catch. The exact
            // trail below would still fail, but this is the cheaper, earlier signal.
            Assert.Null(facts.CurrentInstanceId);
            Assert.Equal(0, facts.LiveInstanceCount);

            // The requeue must be attributed to the machine going away, not to a liveness
            // clock lapsing — those are different scenarios, and before the reason was
            // persisted (#73) the only way to keep them apart here was to push the
            // no-progress ceiling far out and trust it. Now it is assertable.
            //
            // The whole trail, exactly: ONE requeue per task per disconnect, and it says
            // machine-gone. This assertion used to tolerate a trailing AckTimeout because
            // the endpoint requeued in its `finally` BEFORE unregistering, so the requeue's
            // own notify woke a dispatch pass while the dying connection was still
            // registered and ready — a pass claimed ONE of the two siblings onto the dead
            // socket and burned a second requeue (which sibling varied per run, #87). The
            // endpoint now unregisters first, so there is no window in which the corpse is
            // dispatchable and the trail is deterministic for both siblings. Exact rather
            // than tolerant on purpose: at #85's cap of 5 a doubled requeue means a
            // flapping machine abandons a task in as few as three disconnects, and only an
            // exact count can catch that coming back.
            Assert.Equal(
                [LivenessLossReason.MachineReboot],
                await fleet.RequeueReasonsAsync(task, ct));
        }

        // ── 2. The restart sweep runs before the daemon accepts anything.
        var up = await fleet.StartLandbridgedAsync(ct);
        Assert.True(ParseStraysReaped(up) >= 1,
            $"the restarted landbridged reaped nothing; the planted stray tree was still tagged " +
            $"with machine {fleet.MachineId}\n{up}\n" + await fleet.DiagnoseAsync(siblings, ct));
        Assert.True(
            await ChaosFleet.WaitUntilAsync(() => Task.FromResult(!stray.AnyAlive), TransitionBudget, ct),
            "the planted stray tree survived the restart sweep\n" + await fleet.DiagnoseAsync(siblings, ct));

        // ── 3. The Lead resumes each failed attempt; dispatch places them once each.
        foreach (var task in siblings)
        {
            await fleet.ResumeFailedAsync(task, ct);
            await AssertReachesAsync(fleet, task, SessionState.Working,
                "a failed sibling was never redispatched after landbridged came back", ct, siblings);
            Assert.True(
                await ChaosFleet.WaitUntilAsync(
                    async () =>
                    {
                        var seated = await fleet.FactsAsync(task, ct);
                        return seated is { } s
                            && s.CurrentInstanceId is not null
                            && s.LiveInstanceCount == 1
                            && s.CurrentInstanceId != beforeKill[task].CurrentInstanceId;
                    },
                    TransitionBudget, ct),
                "a failed sibling was marked working before dispatch seated a live instance\n"
                + await fleet.DiagnoseAsync(siblings, ct));
        }

        // ── 4. The whole loop still works, not just the requeue path.
        var after = await fleet.CreateSessionAsync("chaos post-restart", profile: null, ct);
        await AssertReportedAsync(fleet, after,
            "a task created after the restart never reported", ct, siblings.Append(after));
        await fleet.AcceptAsync(after, ct);
        await AssertReachesAsync(fleet, after, SessionState.Completed,
            "a task created after the restart never completed", ct, siblings.Append(after));
    }

    /// <summary>
    /// §17.8: "Replay a stale worker-instance token."
    ///
    /// <para>The token replayed here is the real thing: read out of the <c>mcp.json</c>
    /// landbridged generated for the live dispatch (§13), which is the very credential the
    /// running worker is authenticating with. The task is then requeued out from under
    /// it by killing landbridged, and the token is presented again over the real MCP
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
        // Neither §10 clock may reclaim this task: the scenario is about replaying a dead
        // instance's token, and the only reclaim it wants is the sigkill's. The ceiling
        // mutes the progress clock. The aliveness clock needs saying too — it is the one
        // that failed here in CI, requeueing as LivenessTimeout 7.65s after dispatch,
        // before the wedge worker existed to be alive. The default 5s cannot cover a cold
        // spawn on a loaded runner; widening it globally is wrong, because other scenarios
        // rely on that sweep to rescue a stalled dispatch inside TransitionBudget.
        await using var fleet = new ChaosFleet(pg, new ChaosFleetOptions
        {
            NoProgressCeiling = TimeSpan.FromMinutes(10),
            PerTaskLivenessWindow = TimeSpan.FromSeconds(30),
        });
        await fleet.StartAsync(ct);

        var task = await fleet.CreateSessionAsync("chaos stale token", ChaosProfiles.Wedge, ct);
        await AssertReachesAsync(fleet, task, SessionState.Working, "the task never reached working", ct);

        string? stale = null;
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                async () => (stale = await fleet.InjectedWorkerTokenAsync(task, ct)) is not null,
                TransitionBudget, ct),
            "landbridged never wrote the worker's mcp.json\n" + await fleet.DiagnoseAsync([task], ct));

        // Sanity: this credential is live right now. Without this the test could pass
        // against a token that was never valid in the first place.
        await using (var worker = await fleet.ConnectMcpAsync(stale!, ct))
        {
            var assignment = await worker.CallToolAsync(
                "get_session", new Dictionary<string, object?>(), cancellationToken: ct);
            Assert.NotEqual(true, assignment.IsError);
        }

        await fleet.SigkillLandbridgedAsync();
        await AssertReachesAsync(fleet, task, SessionState.Failed,
            "the task was not failed, so its instance was never revoked", ct);

        var refused = await IsRefusedAsync(fleet, stale!, ct);
        Assert.True(refused,
            "the predecessor instance's worker token was still accepted after its task was requeued\n" +
            await fleet.DiagnoseAsync([task], ct));
    }

    /// <summary>
    /// §17.8: "Partition a machine" — the liveness half.
    ///
    /// <para>A worker that is running but getting nowhere is the case the §10 two clocks
    /// exist to separate, and it is the one a single number cannot express: landbridged keeps
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
            // the ONLY clock that can fire is the no-progress ceiling. The window is
            // deliberately WIDE (15 heartbeats), not merely wider than the ceiling: a loaded
            // CI runner can stall landbridged's heartbeat pump for several seconds, and a 5s
            // window let the aliveness clock win that race once (reason LivenessTimeout
            // instead of NoProgress, dispatch run 31240309899). The ceiling still fires
            // first by construction; detection just waits for the next sweep.
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            PerTaskLivenessWindow = TimeSpan.FromSeconds(15),
            NoProgressCeiling = TimeSpan.FromSeconds(6),
        });
        await fleet.StartAsync(ct);

        var task = await fleet.CreateSessionAsync("chaos wedged worker", ChaosProfiles.Wedge, ct);
        await AssertReachesAsync(fleet, task, SessionState.Working, "the wedged worker never started", ct);
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
        // so without this the test would equally pass if landbridged had simply stopped
        // reporting the worker alive, which is a different scenario with a different
        // detection time and a different remedy (a machine problem, not a wedged agent).
        // Since #73 the reason is committed on both surfaces, so this reads durable state
        // rather than scraping a log line.
        var reasons = await fleet.RequeueReasonsAsync(task, ct);
        Assert.Contains(LivenessLossReason.NoProgress, reasons);
        var reclaimed = (await fleet.FactsAsync(task, ct))!.Value;
        Assert.Equal(LivenessLossReason.NoProgress, reclaimed.LastRequeueReason);

        // The wedge is deterministic, so it re-wedges on every redispatch and would walk
        // the task to its requeue cap and abandon it (§9 check 7) if this ran long
        // enough. Asserting we are still short of the cap is what keeps this a test about
        // the no-progress clock rather than an accidental test of the cap — which has its
        // own coverage in Landbridge.Core.Tests.RequeueCapTests.
        Assert.True(reclaimed.InfrastructureRequeues < reclaimed.InfrastructureRequeueLimit,
            $"the wedge reached its requeue cap ({reclaimed.InfrastructureRequeues}/" +
            $"{reclaimed.InfrastructureRequeueLimit}) before this scenario could assert on it\n" +
            await fleet.DiagnoseAsync([task], ct));
    }

    /// <summary>
    /// §17.8: "restart the plane mid-task" — the scenario that was unwritable as a passing
    /// test until #86 was fixed, and is now its regression test.
    ///
    /// <para>A task is left <c>working</c> and the PLANE is SIGKILLed. Nothing plane-side
    /// runs on the way out — no requeue, no unregister — so the replacement process comes
    /// up with an empty <c>RunnerConnectionRegistry</c> over a database that still says the
    /// task is working on this machine. landbridged survives (it holds each worker's stdin, so
    /// the dead-man's switch does not trip and the worker keeps running) and reconnects on
    /// its own backoff, re-announcing nothing: <c>rebooted</c> is emitted once per daemon
    /// PROCESS start, not per socket.</para>
    ///
    /// <para>That combination was a permanent strand. The reconnecting machine was
    /// registered with no scan for what it held, so the task was tracked nowhere: its
    /// <c>alive</c> events were dropped on the floor (<c>Refresh</c> returns for an
    /// untracked task) and the liveness scan never saw it (it walks <c>AllTracked</c>).
    /// No clock covered the task and no requeue could ever fire — it sat in <c>working</c>
    /// forever.</para>
    ///
    /// <para>What is asserted, in the order the fix produces it:</para>
    /// <list type="number">
    /// <item>The restarted plane re-adopts the in-flight task from committed state, which
    /// its own log states — the direct observation, and what bounds landbridged's reconnect.</item>
    /// <item>The task is genuinely back under both clocks: it is reclaimed, and the reason
    /// is <c>NoProgress</c>. That single reason carries three facts at once. It fires at
    /// all, so tracking resumed. It is not <c>LivenessTimeout</c>, so the re-adopted clocks
    /// started at reconnect rather than carrying a pre-restart timestamp that would have
    /// requeued healthy work for the plane's own downtime — and so the machine's
    /// <c>alive</c> events are landing again, since only they hold that clock off. And it
    /// is not <c>MachineReboot</c>, so this is the liveness path reclaiming a tracked task,
    /// not a disconnect sweeping an untracked one.</item>
    /// <item>Nothing was stranded and nothing was lost: the reclaimed task is requeued
    /// exactly once and redispatched onto the reconnected machine.</item>
    /// </list>
    ///
    /// <para>The wedge profile is what makes this deterministic: a worker that stays alive
    /// and makes no progress leaves <c>NoProgress</c> as the only clock that can fire, and
    /// leaves the task reliably <c>working</c> at the moment the plane is killed. The
    /// ceiling is shrunk to seconds but kept comfortably longer than the restart itself, so
    /// the requeue being asserted cannot be one the doomed plane had already started.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_task_in_flight_survives_a_plane_restart_and_stays_under_its_liveness_clocks()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        Skip.If(OperatingSystem.IsWindows(), WindowsSkip);

        using var cts = new CancellationTokenSource(ScenarioBudget);
        var ct = cts.Token;
        await using var fleet = new ChaosFleet(pg, new ChaosFleetOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            PerTaskLivenessWindow = TimeSpan.FromSeconds(5),
            // Long enough that the pre-restart plane cannot reach it before the kill, short
            // enough to assert on: the post-restart reclaim lands within one ceiling plus
            // one sweep period of the reconnect.
            NoProgressCeiling = TimeSpan.FromSeconds(20),
        });
        await fleet.StartAsync(ct);

        var task = await fleet.CreateSessionAsync("chaos plane restart", ChaosProfiles.Wedge, ct);
        await AssertReachesAsync(fleet, task, SessionState.Working, "the task never reached working", ct);

        // MID-TASK means the worker is really running, and `working` alone does not say
        // that: the store commits submitted→working before the DispatchCommand is sent, so
        // the row can say working while the command is still in flight. Killing the plane in
        // that window destroys the dispatch instead of interrupting a task — the worker
        // never spawns, so nothing reports it alive and the aliveness clock reclaims it as
        // LivenessTimeout. That is correct behaviour for a lost dispatch and it is not this
        // scenario, so wait for the worker's own start marker before killing anything.
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                () => Task.FromResult(fleet.WorkerStarted(task)), TransitionBudget, ct),
            "the wedged worker never wrote its start marker, so it never ran and there was " +
            "no in-flight task for the restart to interrupt\n" + await fleet.DiagnoseAsync([task], ct));

        var beforeRestart = (await fleet.FactsAsync(task, ct))!.Value;
        Assert.Equal(0, beforeRestart.InfrastructureRequeues);

        await fleet.RestartPlaneAsync(ct);

        // ── 1. The new plane re-adopts what the machine still holds. This is also the
        // bound on landbridged's reconnect: its backoff starts at 200ms and doubles to a 10s
        // ceiling, so a reconnect plus re-adoption well inside the transition budget is the
        // observable fact, not an assumed one.
        var readopted = await fleet.WaitForPlaneLineAsync(
            l => l.Contains("re-adopted", StringComparison.Ordinal), TransitionBudget);
        Assert.True(readopted is not null,
            "the restarted plane never re-adopted the in-flight task, so landbridged either did " +
            "not reconnect within the budget or reconnected to a plane that scanned nothing\n" +
            await fleet.DiagnoseAsync([task], ct));
        Assert.Contains(task.ToString(), readopted!);

        // ── 2. Back under both clocks — reclaimed, and by the progress clock.
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                async () =>
                {
                    var facts = await fleet.FactsAsync(task, ct);
                    return facts is { } f && f.InfrastructureRequeues > 0;
                },
                TransitionBudget, ct),
            "the re-adopted task was never reclaimed, so it is stranded in working with no " +
            "clock over it — the #86 symptom\n" + await fleet.DiagnoseAsync([task], ct));

        var reasons = await fleet.RequeueReasonsAsync(task, ct);
        Assert.Contains(LivenessLossReason.NoProgress, reasons);

        // ── 3. Nothing lost: the Lead resumes, and it goes back out to the machine
        // that is still there.
        await fleet.ResumeFailedAsync(task, ct);
        await AssertReachesAsync(fleet, task, SessionState.Working,
            "the reclaimed task was never redispatched after the plane restart", ct);
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                async () =>
                {
                    var seated = await fleet.FactsAsync(task, ct);
                    return seated is { } s
                        && s.CurrentInstanceId is not null
                        && s.LiveInstanceCount == 1
                        && s.CurrentInstanceId != beforeRestart.CurrentInstanceId
                        && s.InfrastructureRequeues >= 1;
                },
                TransitionBudget, ct),
            "the reclaimed task was marked working before dispatch seated a live instance\n"
            + await fleet.DiagnoseAsync([task], ct));
    }

    /// <summary>
    /// §17.8: "Partition a machine" — the other half of the wedged worker, and #84's
    /// regression test. The scenario above asserts the plane reclaims the TASK; this one
    /// asserts it also disposes of the PROCESS.
    ///
    /// <para>A requeue used to abandon the task without saying anything to the machine, so the
    /// wedged harness the no-progress ceiling had just given up on kept running — and, for a
    /// real agent, kept spending model tokens — until its <c>landbridged</c> restarted and the §10
    /// stray sweep reaped it. Nothing in committed state can show that: the row records the
    /// plane's decision, not whether anything acted on it. So this scenario reads the worker's
    /// own OS pid off the marker it writes at startup and asserts the process is gone.</para>
    ///
    /// <para>The second assertion is the subtler half, and it is why the kill needs care
    /// rather than just a send. The runner reports the plane's own kill as an ordinary
    /// <c>exited</c> naming only the task, and by the time it arrives the requeue's
    /// <c>pg_notify</c> has very likely redispatched that task onto this same machine — the
    /// only one here. Read as news, that echo requeues the SUCCESSOR attempt as
    /// <see cref="LivenessLossReason.ProcessExited"/>: two requeues off the §9 check 7 cap for
    /// one liveness loss, and a healthy worker left running for a task put back in the queue.
    /// The requeue trail staying purely <c>NoProgress</c> across several wedge cycles is what
    /// says that did not happen — each cycle gives the race a fresh chance, since the kill and
    /// the redispatch are genuinely concurrent here.</para>
    ///
    /// <para>Tolerant about how many times the wedge is retried, like its sibling: the wedge is
    /// deterministic, so it re-wedges on every redispatch and would walk to its cap given long
    /// enough. This asserts it stops short of it.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_reclaimed_wedged_workers_process_is_killed_rather_than_left_running()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        Skip.If(OperatingSystem.IsWindows(), WindowsSkip);

        using var cts = new CancellationTokenSource(ScenarioBudget);
        var ct = cts.Token;
        await using var fleet = new ChaosFleet(pg, new ChaosFleetOptions
        {
            // As in the sibling scenario: the heartbeat keeps the aliveness clock fresh, so
            // the no-progress ceiling is the only clock that can reclaim this task.
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            PerTaskLivenessWindow = TimeSpan.FromSeconds(5),
            NoProgressCeiling = TimeSpan.FromSeconds(6),
        });
        await fleet.StartAsync(ct);

        var task = await fleet.CreateSessionAsync("chaos wedge kill", ChaosProfiles.Wedge, ct);
        await AssertReachesAsync(fleet, task, SessionState.Working, "the wedged worker never started", ct);

        // Read while this attempt is live: the work dir is per task, so redispatch overwrites
        // the marker and the predecessor's pid is only readable before its requeue.
        var wedged = 0;
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                () => Task.FromResult(fleet.WorkerPid(task) is { } pid && (wedged = pid) > 0),
                TransitionBudget, ct),
            "the wedged worker never recorded its pid, so it never really ran\n" +
            await fleet.DiagnoseAsync([task], ct));
        Assert.True(ChaosProcess.PidAlive(wedged),
            $"the wedged worker (pid {wedged}) was not running before the ceiling lapsed\n" +
            await fleet.DiagnoseAsync([task], ct));

        var working = (await fleet.FactsAsync(task, ct))!.Value;
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

        // ── 1. The process the plane gave up on is actually gone.
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                () => Task.FromResult(!ChaosProcess.PidAlive(wedged)), TransitionBudget, ct),
            $"the wedged worker (pid {wedged}) was still running after its task was requeued: " +
            $"the requeue abandoned the task but not the process (#84)\n" +
            await fleet.DiagnoseAsync([task], ct));

        // ── 2. And the machine is still good for work — a kill takes down one dispatch, not
        // the daemon's ability to accept the next one. The Lead resumes; dispatch places it.
        await fleet.ResumeFailedAsync(task, ct);
        await AssertReachesAsync(fleet, task, SessionState.Working,
            "the reclaimed task was never redispatched after its worker was killed", ct);
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                () => Task.FromResult(fleet.WorkerPid(task) is { } pid && ChaosProcess.PidAlive(pid)),
                TransitionBudget, ct),
            "no live worker process for the redispatched task\n" + await fleet.DiagnoseAsync([task], ct));

        // ── 3. The trail is the clock's, not the kill's. Resume once more so the
        // kill/redispatch race gets another run at producing a ProcessExited row.
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                async () =>
                {
                    var facts = await fleet.FactsAsync(task, ct);
                    return facts is { } f && f.State == SessionState.Failed
                        && f.InfrastructureRequeues >= working.InfrastructureRequeues + 2;
                },
                TransitionBudget, ct),
            "the wedge did not re-wedge, so the echo race only got one run\n" +
            await fleet.DiagnoseAsync([task], ct));

        var reasons = await fleet.RequeueReasonsAsync(task, ct);
        Assert.Contains(LivenessLossReason.NoProgress, reasons);

        var reclaimed = (await fleet.FactsAsync(task, ct))!.Value;
        Assert.Equal(LivenessLossReason.NoProgress, reclaimed.LastRequeueReason);
        Assert.True(reclaimed.InfrastructureRequeues < reclaimed.InfrastructureRequeueLimit,
            $"the wedge reached its requeue cap ({reclaimed.InfrastructureRequeues}/" +
            $"{reclaimed.InfrastructureRequeueLimit}) before this scenario could assert on it\n" +
            await fleet.DiagnoseAsync([task], ct));
    }

    /// <summary>
    /// §17.8: "Close a laptop and reattach" — previously on the uncovered list, and #94's
    /// regression test.
    ///
    /// <para>A suspended machine leaves an accepted <c>/runner</c> connection behind that the
    /// plane still believes is live: nothing was closed, the socket simply stopped carrying
    /// bytes. On wake the machine dials a fresh one, so for a while the plane holds TWO
    /// connections for one machine — and then the stale one's endpoint eventually notices and
    /// runs its teardown. That teardown used to unregister by machine id, which by then meant
    /// the connection that had REPLACED it: the machine's running tasks requeued out from
    /// under it, and its live socket left registered nowhere, invisible to dispatch until it
    /// happened to reconnect again.</para>
    ///
    /// <para><b>How the overlap is produced.</b> A genuinely half-open TCP connection needs
    /// packets dropped in the network — root-only and unportable — so the test dials the
    /// second connection itself, with the machine's real credential over the real
    /// <c>/runner</c> endpoint, and never sends on it. What the plane sees is what matters and
    /// it is exactly right: two accepted, authenticated connections for one machine, the older
    /// of which is silent. Sending nothing is faithful rather than merely convenient — a stale
    /// connection reports no heartbeat, so it never becomes ready and dispatch never considers
    /// it. The roles are the same shape as production with the parts swapped: here the
    /// test holds the socket that will be superseded and the real <c>landbridged</c> holds the one
    /// that supersedes, which is what lets the assertions be about real running work.</para>
    ///
    /// <para>What must hold when the stale socket finally dies: <b>one requeue at most</b> —
    /// in fact none, because nothing about the machine changed — the reattached connection is
    /// still tracked, and no task is stranded. The last of those is asserted from the outside,
    /// by running a fresh task end to end afterwards: that can only pass if the machine is
    /// still registered, still ready, and still reachable.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_superseded_connection_tearing_down_costs_the_reattached_machine_nothing()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        Skip.If(OperatingSystem.IsWindows(), WindowsSkip);

        using var cts = new CancellationTokenSource(ScenarioBudget);
        var ct = cts.Token;
        // A long ceiling keeps the liveness sweeper out: the requeue count asserted below has
        // to be attributable to the teardown and nothing else.
        await using var fleet = new ChaosFleet(pg, new ChaosFleetOptions
        {
            NoProgressCeiling = TimeSpan.FromMinutes(10),
        });
        await fleet.StartPlaneOnlyAsync(ct);

        // The socket the closed laptop left behind, registered before landbridged exists so that
        // the daemon's connection is the one that supersedes.
        using var stale = await fleet.DialRunnerAsync(ct);
        Assert.True(
            await fleet.WaitForPlaneLineAsync(
                l => l.Contains("runner connected:", StringComparison.Ordinal), TransitionBudget) is not null,
            "the plane never registered the stale connection, so there was no overlap to test\n" +
            await fleet.DiagnoseAsync([], ct));

        // The reattach.
        await fleet.StartLandbridgedAsync(ct);
        Assert.True(
            await fleet.WaitForPlaneLineAsync(
                l => l.Contains("while an earlier connection was still registered", StringComparison.Ordinal),
                TransitionBudget) is not null,
            "the plane never saw two connections for this machine, so the overlap this scenario " +
            "is about never existed\n" + await fleet.DiagnoseAsync([], ct));

        var held = await fleet.CreateSessionAsync("chaos reattach", ChaosProfiles.Wedge, ct);
        await AssertReachesAsync(fleet, held, SessionState.Working, "the task never reached working", ct);
        Assert.True(
            await ChaosFleet.WaitUntilAsync(
                () => Task.FromResult(fleet.WorkerStarted(held)), TransitionBudget, ct),
            "the worker never started, so the machine was not really holding live work\n" +
            await fleet.DiagnoseAsync([held], ct));
        var beforeTeardown = (await fleet.FactsAsync(held, ct))!.Value;
        Assert.Equal(0, beforeTeardown.InfrastructureRequeues);

        // The half-open socket finally errors out, and its endpoint runs the teardown that
        // used to take the live connection down with it.
        stale.Abort();
        Assert.True(
            await fleet.WaitForPlaneLineAsync(
                l => l.Contains("superseded runner connection closed", StringComparison.Ordinal),
                TransitionBudget) is not null,
            "the stale connection's teardown never ran, or ran as an ordinary disconnect — " +
            "either way this scenario asserted nothing\n" + await fleet.DiagnoseAsync([held], ct));

        // ── The machine is still there: a fresh task runs the whole loop. Pre-fix the
        // teardown left landbridged registered nowhere, so nothing could be dispatched at all.
        var after = await fleet.CreateSessionAsync("chaos post-reattach", profile: null, ct);
        await AssertReportedAsync(fleet, after,
            "a task created after the superseded teardown never reported, so the live " +
            "connection was unregistered with it", ct, [held, after]);
        await fleet.AcceptAsync(after, ct);
        await AssertReachesAsync(fleet, after, SessionState.Completed,
            "a task created after the superseded teardown never completed", ct, [held, after]);

        // ── And the work that was already in flight never noticed: same attempt, same
        // incumbent instance, no requeue at all — not the "one at most" a real disconnect
        // costs, because nothing about this machine actually went away.
        var facts = (await fleet.FactsAsync(held, ct))!.Value;
        Assert.Equal(SessionState.Working, facts.State);
        Assert.Equal(0, facts.InfrastructureRequeues);
        Assert.Empty(await fleet.RequeueReasonsAsync(held, ct));
        Assert.Equal(beforeTeardown.CurrentInstanceId, facts.CurrentInstanceId);
        Assert.Equal(1, facts.LiveInstanceCount);
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
        ChaosFleet fleet, SessionId task, SessionState expected, string because,
        CancellationToken ct, IEnumerable<SessionId>? context = null)
    {
        if (await fleet.WaitForStateAsync(task, expected, TransitionBudget, ct))
            return;
        var actual = await fleet.StateAsync(task, ct);
        Assert.Fail(
            $"{because}: task {task} is {actual?.ToString() ?? "(gone)"}, expected {expected} " +
            $"within {TransitionBudget}\n" + await fleet.DiagnoseAsync(context ?? [task], ct));
    }

    private static async Task AssertReportedAsync(
        ChaosFleet fleet, SessionId task, string because,
        CancellationToken ct, IEnumerable<SessionId>? context = null)
    {
        if (await fleet.WaitForReportAsync(task, TransitionBudget, ct))
            return;
        var actual = await fleet.MessageStateAsync(task, ct);
        Assert.Fail(
            $"{because}: task {task} message is {actual?.ToString() ?? "(gone)"}, expected awaiting_report " +
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
                "get_session", new Dictionary<string, object?>(), cancellationToken: ct);
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
    /// The count out of landbridged's announcement line, which §10 prints only after the
    /// restart sweep: <c>landbridged up: machine=… profiles=[…] strays_reaped=N control=…</c>.
    /// Asserted as a lower bound, never an equality — the reaper counts pids it resolved,
    /// so a stray that dies on its own between the scan and the kill still counts, and a
    /// tree kill can take the grandchild before the loop reaches it.
    /// </summary>
    private static int ParseStraysReaped(string upLine)
    {
        const string key = "strays_reaped=";
        var at = upLine.IndexOf(key, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no strays_reaped in landbridged's announcement: {upLine}");
        var rest = upLine[(at + key.Length)..];
        var end = rest.IndexOf(' ');
        return int.Parse(end >= 0 ? rest[..end] : rest);
    }
}
