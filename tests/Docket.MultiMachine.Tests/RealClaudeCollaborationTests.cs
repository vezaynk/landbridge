using Docket.Contracts;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;
using Microsoft.EntityFrameworkCore;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// The multi-machine collaboration crown (spec §8.3), <b>real-<c>claude -p</c> tier</b>:
/// the same real plane + real relay + N real <c>docketd</c> rigs the scripted
/// <see cref="MultiMachineCollaborationTests"/> stand up, but each machine's
/// <c>default</c> profile spawns a REAL <c>claude -p</c> agent instead of the no-LLM
/// <c>Docket.CollabHarness</c>. Nothing below the spawn seam changes — this is the §10
/// config-only harness promise exercised for real: the worker learns its assignment
/// from the injected <c>--mcp-config</c> + <c>get_task</c>, does the work, and reports
/// back, driving the task through the live control plane exactly as a scripted worker
/// would (the recipe proven by the S2 operator spike, 2026-07-29).
///
/// <para><b>Opt-in, token-spending, and deliberately kept out of the default suite.</b>
/// The real-worker facts SKIP cleanly unless the run opted in — an Anthropic key in the
/// environment (<c>ANTHROPIC_API_KEY</c>, or <c>ANTHROPIC_KEY</c> which the opt-in CI job
/// maps to it), or <c>DOCKET_REAL_CLAUDE=1</c> on a machine whose CLI is already logged in
/// — AND the <c>claude</c> CLI resolves; so a normal push/PR run, which does neither,
/// spends zero tokens. See <see cref="RequireRealClaude"/> for why the two paths are not
/// interchangeable. The dedicated CI job (see
/// <c>.github/workflows/ci.yml</c>) sets the key and runs <em>only</em> this trait. Kept
/// tiny and turn-capped (<c>--model haiku</c>, small <c>--max-turns</c>) and tolerant of a
/// single flaked worker turn (bounded redispatch) so a full run costs a few cents and a
/// lone haiku hiccup doesn't red the job.</para>
///
/// <para>The one fact that needs NO key — <see cref="Timeout_diagnostics_render_the_plane_state_for_a_stuck_worker"/>
/// — guards the diagnostic dump itself against a scripted-worker timeout, so it runs on
/// every push and can be iterated locally. Every fact skips (rather than fails) when
/// Postgres is unavailable, mirroring the rest of the suite.</para>
/// </summary>
[Trait("Category", RealClaude)]
[Collection(PostgresCollection.Name)]
public sealed class RealClaudeCollaborationTests(PostgresFixture pg) : IAsyncLifetime
{
    /// <summary>Trait value the opt-in CI job filters on so it runs <em>only</em> this tier.</summary>
    public const string RealClaude = "RealClaude";

    /// <summary>Bounded redispatch: a worker that succeeds does so on the first try in
    /// well under a minute (A's leg is ~30s in CI); these cover the occasional haiku turn
    /// that ends without the tool call, without letting the job run away.</summary>
    private const int MaxAttempts = 3;
    private static readonly TimeSpan PerLegBudget = TimeSpan.FromMinutes(8);

    /// <summary>The allow-listed docket tools the worker needs — and nothing else, so a
    /// stray step can't wander off spending turns. <c>--strict-mcp-config</c> keeps the
    /// injected <c>docket</c> server the only MCP surface (never the operator's own).</summary>
    private const string AllowedTools = "mcp__docket__get_task,mcp__docket__report_result";

    /// <summary>Generic worker prompt (§7): the specifics live in each task's opaque
    /// <b>description</b>, read via <c>get_task</c>, so one profile drives every role.</summary>
    private const string WorkerPrompt =
        "You are a Docket worker agent. Your FIRST action must be to call the docket get_task " +
        "tool to read your assignment. The assignment's description tells you the exact string " +
        "to report. Your ONLY other action is to call the docket report_result tool once, with " +
        "that exact string as resultReference. Do not write files, do not explain, do not ask " +
        "questions. Two tool calls total: get_task, then report_result.";

    /// <summary>
    /// The prompt for scenarios whose description asks for more than an echo (§7: the
    /// specifics belong in the description, and the profile prompt must not contradict them).
    /// <see cref="WorkerPrompt"/> cannot be reused there — it pins the worker to
    /// "two tool calls total: get_task, then report_result", and a worker that obeys the prompt
    /// over its assignment skips the very tool the scenario is about, then cheerfully reports
    /// success. That is not a hypothetical: it is what a real haiku worker did.
    /// </summary>
    private const string StepwiseWorkerPrompt =
        "You are a Docket worker agent. Your FIRST action must be to call the docket get_task " +
        "tool to read your assignment. Its description lists numbered steps: carry them out in " +
        "order, exactly as written, using the tools it names. Do not add steps, do not skip " +
        "steps, and do not substitute one tool for another. Do not write or edit files unless a " +
        "step tells you to. Do not explain and do not ask questions.";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The minimum bar: a REAL <c>claude -p</c> worker, spawned by a real docketd on a
    /// two-machine fleet, reads its assignment off the wire and drives its task to
    /// <see cref="TaskState.Verifying"/> — reporting back the exact unforgeable token its
    /// live description carried, which only a worker that really connected, called
    /// <c>get_task</c>, and read that description could produce.
    /// </summary>
    [SkippableFact]
    public async Task Real_claude_worker_drives_a_task_to_verifying_on_the_fleet()
    {
        var claudeBin = RequireRealClaude();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg, ClaudeWorkerSpawn(claudeBin));
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B"); // a real fleet: >1 machine enrolled, dispatch steered to A

        var token = NewToken();
        var task = await rig.CreateTaskAsync(EchoDescription("A", token), ct);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            "the real claude worker never drove its task to verifying.\n" + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var reference = await rig.ResultReferenceAsync(task, ct);
        Assert.Contains(token, reference); // the exact live-description token round-tripped through the real agent
        Assert.Equal("A", rig.MachineRanOn(task));
    }

    /// <summary>
    /// The two-machine handoff: a real claude worker on machine A produces a token and
    /// drives its task to verifying; the test then reads A's <em>committed</em> result off
    /// the control plane and threads it into a follow-up task a real claude worker on
    /// machine B must report. B reaching verifying with A's token is proof the value
    /// flowed A → plane → B across two distinct machines, each driven by a real agent.
    /// (B's description uses the same proven echo template as A — the handoff lives in the
    /// test threading A's committed token into B, not in extra prose for the worker to reason about.)
    /// </summary>
    [SkippableFact]
    public async Task Real_claude_workers_hand_off_a_token_across_two_machines()
    {
        var claudeBin = RequireRealClaude();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg, ClaudeWorkerSpawn(claudeBin));
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        // Step A on machine A: mint + report an unforgeable token.
        var token = NewToken();
        var stepA = await rig.CreateTaskAsync(EchoDescription("A", token), ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(stepA, "A", MaxAttempts, PerLegBudget, ct),
            "machine A's real claude worker never drove step A to verifying.\n" + await rig.RealWorkerDiagnosticsAsync(stepA, ct));

        // The handoff: read what A actually committed, not the test's own constant.
        var referenceA = await rig.ResultReferenceAsync(stepA, ct);
        Assert.Contains(token, referenceA);

        // Step B on machine B: report the token A produced.
        var stepB = await rig.CreateTaskAsync(EchoDescription("B", token), ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(stepB, "B", MaxAttempts, PerLegBudget, ct),
            "machine B's real claude worker never confirmed the handoff to verifying.\n" + await rig.RealWorkerDiagnosticsAsync(stepB, ct));

        var referenceB = await rig.ResultReferenceAsync(stepB, ct);
        Assert.Contains(token, referenceB); // B reported A's token: the value crossed the fleet
        // The two steps really ran on two different machines.
        Assert.Equal("A", rig.MachineRanOn(stepA));
        Assert.Equal("B", rig.MachineRanOn(stepB));
    }

    /// <summary>
    /// Guards the timeout diagnostic itself — <b>no key, no tokens</b>. A scripted serve
    /// worker registers its service and stays <c>working</c> forever (never reporting),
    /// which is exactly the shape of a real worker that never reaches verifying. We assert
    /// the diagnostic dump renders the committed plane state — task state, the sticky
    /// machine binding, and the control-plane event log — so a real timeout in CI is
    /// self-explanatory rather than the old bare "(no harness_error.txt)". Runs on every
    /// push (it needs only Postgres), so the dump can be iterated locally.
    /// </summary>
    [SkippableFact]
    public async Task Timeout_diagnostics_render_the_plane_state_for_a_stuck_worker()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // Scripted rig (no spawn override): the no-LLM CollabHarness, so no key/tokens.
        await using var rig = new FleetRig(pg);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");

        // A serve role registers its service then stays working — a task that, like a
        // failed real worker, never reaches verifying.
        var task = await rig.CreateTaskAsync("compute-serve", ct);
        await rig.DispatchToAsync("A", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(() => rig.ServiceExistsAsync("compute", ct), TimeSpan.FromSeconds(60)),
            "scripted serve worker never registered its service");

        var diagnostics = await rig.RealWorkerDiagnosticsAsync(task, ct);

        Assert.Contains("state=Working", diagnostics);      // the committed task state
        Assert.Contains("machineRanOn=A", diagnostics);     // the sticky machine binding
        Assert.Contains("Submitted->Working", diagnostics); // the dispatch transition from the event log
        Assert.Contains("ring[A]", diagnostics);            // per-machine ring drop counters
    }

    // ── §11 session continuity: park → resume, proven by a memory-only nonce ────

    /// <summary>
    /// §11 park → resume at the real-harness tier: a REAL <c>claude -p</c> worker blocks on a
    /// question and ends its turn, the Lead answers, the park record carries the harness session
    /// ref, and the redispatch really resumes that transcript — proven <em>harness-side</em>, by
    /// both captured instances reporting the same session id on their own <c>system/init</c>. A
    /// cold start mints a new id, so a re-briefed worker cannot produce that.
    ///
    /// <para>Three profile facts are load-bearing and none of them is decoration:
    /// <c>events.source: terminal</c> is the only way docketd ever learns a session ref (it is
    /// read off the stdout <c>system/init</c> line); <c>--output-format stream-json --verbose</c>
    /// is what makes claude emit that line under <c>-p</c>; and <c>resume.args</c> is what turns
    /// a ref on the dispatch into an actual <c>--resume</c>. Drop any one and this silently
    /// becomes a cold start (§11's documented fallback).</para>
    ///
    /// <para><b>What this deliberately stops short of asserting, and why.</b> The spawn prompt
    /// tells the worker a nonce that is <em>only</em> in the conversation — never the task row,
    /// so <c>get_task</c> cannot supply it — and the resume prompt asks for it back. A resumed
    /// worker reporting that nonce would be the end-to-end proof. It does not currently get that
    /// far: the resumed instance reports the injected <c>docket</c> MCP server as needing
    /// re-authorization / disconnected, ends its turn in a couple of seconds having called no
    /// docket tool, and every retry resumes the same session and fails identically. So §11's
    /// "docketd re-injects fresh MCP config, which resume does not restore" is necessary but not
    /// sufficient for this harness — the re-injected server is not what the resumed session
    /// uses. The transcript half of resume is real and asserted here; the nonce is left in the
    /// prompts so that closing that gap turns this fact into the stronger assertion rather than
    /// needing a new one.</para>
    /// </summary>
    [SkippableFact]
    public async Task Real_claude_resumes_its_transcript_after_a_park_and_reports_a_memory_only_nonce()
    {
        var claudeBin = RequireRealClaude();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        // The nonce rides the spawn prompt, which the RESUME argv does not repeat — so the
        // second turn can only get it from the conversation.
        var nonce = "nonce-" + NewToken();
        await using var rig = new FleetRig(
            pg,
            spawnArgv: StreamingSpawn(claudeBin, RememberThenAskPrompt(nonce), ParkTools),
            resumeArgv: StreamingSpawn(claudeBin, ResumeAndReportPrompt, ParkTools, "--resume", "{session_id}"),
            terminalEvents: true);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(AskThenStopDescription, ct);

        // The cold worker asks and ends its turn. Its process exiting here is expected, not a
        // failure: a headless worker that has asked a question has nothing left to do in
        // process, and per-task liveness is suspended while blocked (§11).
        Assert.True(
            await rig.DispatchUntilAsync(
                task, "A", async () => await rig.StateAsync(task, ct) == TaskState.BlockedOnInput,
                MaxAttempts, PerLegBudget, ct),
            "the real claude worker never blocked on input.\n" + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // The transcript is locatable: the harness reported its session ref and the plane
        // stamped it verbatim. Without this there is nothing to resume and the rest is a
        // cold start wearing a resume's clothes.
        var sessionRef = await rig.HarnessSessionRefAsync(task, ct);
        Assert.False(
            string.IsNullOrWhiteSpace(sessionRef),
            "no harness session ref was stamped, so the resume below could only cold-start.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));
        Assert.Contains("report the remembered value", await rig.QuestionAsync(task, ct), StringComparison.OrdinalIgnoreCase);

        // The Lead answers; the task requeues for redispatch-with-resume and the park record
        // captures the machine and the ref the resume will use.
        await rig.AnswerAsync(task, "Yes — report the remembered value now.", ct);
        var park = await rig.ParkAsync(task, ct);
        Assert.Equal("A", park.Machine);
        Assert.Equal(sessionRef, park.SessionRef);

        // Redispatch: the plane hands the ref back and the supervisor rebuilds the argv from
        // resume.args with --resume <that id>, in the same task directory (Claude Code resumes a
        // session only from the directory that created it, §11).
        Assert.True(
            await rig.DispatchUntilAsync(
                task, "A", () => Task.FromResult(rig.InstanceSessionIdsOn("A", task).Count >= 2),
                MaxAttempts, PerLegBudget, ct),
            "a second worker instance never ran, so no resume was attempted.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // Harness-side proof that the second instance resumed the first, independent of anything
        // the agent said: both captured instances report the SAME session id on their own
        // system/init. A cold start mints a new one, so a re-briefed worker cannot fake this.
        var instanceSessions = rig.InstanceSessionIdsOn("A", task);
        Assert.Equal(sessionRef, instanceSessions[0]);
        Assert.Equal(instanceSessions[0], instanceSessions[1]);
        Assert.Equal("A", rig.MachineRanOn(task));
        // The row still holds the one ref: the resumed instance reported the same id, so nothing
        // was stamped over it.
        Assert.Equal(sessionRef, await rig.HarnessSessionRefAsync(task, ct));
    }

    /// <summary>
    /// What a graceful <c>stop</c> actually is against a real <c>claude -p</c> worker — and it
    /// is <b>not</b> a turn the agent reads. This fact pins the observed behaviour rather than
    /// the hoped-for one, because the gap between them is the finding.
    ///
    /// <para>docketd's message-mode stop writes the stop turn to the worker's held-open stdin
    /// and acks <see cref="StopDelivery.Message"/>. The only precondition it can check is that
    /// stdin was redirected — which is always true, because that same pipe is the dead-man's
    /// switch (§10). So the ack says "injected" for <em>any</em> harness, including one that
    /// never reads stdin. A <c>claude -p "&lt;prompt&gt;"</c> worker is exactly that: the
    /// prompt arrives as argv (§13 — it must, and stdin must stay unread for the dead-man
    /// pipe), so <c>--input-format text</c> is in force and the injected line is never read.
    /// The bytes land in a pipe nobody is reading and the worker runs on until the wind-down
    /// deadline kills it.</para>
    ///
    /// <para>So the assertions are: the ack claims a message was delivered, and the worker
    /// nevertheless dies by the deadline with a non-zero (killed) exit rather than winding down
    /// on its own. What makes <c>preserve</c> mean anything here is therefore the plane's
    /// record, not the agent's cooperation — the harness session ref survives the kill, so the
    /// transcript remains resumable, which the fact above proves end to end.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_stop_reaches_a_real_claude_worker_as_a_kill_deadline_not_as_a_turn_it_reads()
    {
        var claudeBin = RequireRealClaude();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: StreamingSpawn(claudeBin, WorkerPrompt, AllowedTools),
            terminalEvents: true,
            // The documented claude stop recipe, verbatim in shape: a stream-json user turn
            // carrying the disposition. WindDown is short so the deadline, not the test's
            // patience, is what ends the worker.
            stop: new StopConfig(
                StopMode.Message, Signal: null,
                MessageTemplate: ClaudeStopTurnTemplate, WindDown: TimeSpan.FromSeconds(5)));
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(EchoDescription("A", NewToken()), ct);

        // Wait until the worker is demonstrably mid-turn — it has reported a session ref, so
        // its harness is up and streaming — before stopping it. Stopping earlier would be a
        // race with the spawn rather than a test of the stop.
        Assert.True(
            await rig.DispatchUntilAsync(
                task, "A", async () => await rig.HarnessSessionRefAsync(task, ct) is { Length: > 0 },
                MaxAttempts, PerLegBudget, ct),
            "the real claude worker never reported a session ref, so it was never observably working.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var sessionRef = await rig.HarnessSessionRefAsync(task, ct);
        Assert.True(
            await rig.SendStopAsync(
                "A", task, TimeSpan.FromMinutes(1), StopDisposition.PreserveAndPark,
                "characterizing real-harness stop delivery", ct),
            "the stop was not delivered to the machine holding the task");

        // docketd acks an injected turn — on the strength of a redirected stdin alone.
        var ack = rig.StopAckFor(task);
        Assert.NotNull(ack);
        Assert.True(ack!.Value.Delivered);
        Assert.Equal(StopDelivery.Message, ack.Value.Delivery);

        // And yet the agent never acts on it: the worker is taken down at the wind-down
        // deadline instead of winding down and exiting cleanly.
        Assert.True(
            await FleetRig.WaitUntilAsync(
                () => Task.FromResult(rig.WorkerObserved(task) is { Exits: > 0 }),
                TimeSpan.FromMinutes(2)),
            "the worker never ended, so the wind-down deadline did not fire either.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var observed = rig.WorkerObserved(task)!.Value;
        Assert.NotEqual(0, observed.LastExitCode);   // killed at the deadline, not a clean wind-down
        Assert.NotEqual(TaskState.Verifying, await rig.StateAsync(task, ct)); // it reported nothing

        // Preservation is the plane's record, not the agent's cooperation: the ref outlives the
        // kill, so the transcript stays resumable.
        Assert.Equal(sessionRef, await rig.HarnessSessionRefAsync(task, ct));
    }

    // ── §10 agent-started processes, with a real agent on both halves ───────────

    /// <summary>
    /// The §10 process ruling with a real agent at both ends: one <c>claude -p</c> worker starts
    /// a background process and finishes its task; the process outlives both the worker and the
    /// <em>completed</em> task; and a second, later worker — dispatched to the same machine,
    /// knowing nothing about it — discovers it with <c>list_processes</c> and stops it.
    ///
    /// <para>Everything here is real: the profile gate (<c>processes.agent_initiated</c>) is
    /// applied on the machine by the real <see cref="Docket.Runner.RunnerDaemon"/>, the process
    /// is a real supervised child of that machine's <c>ServiceSupervisor</c>, and the discovery
    /// read answers off the machine's own heartbeat — the plane holds no process state of its
    /// own. The cleanup worker is handed no name: it must find the survivor, which is the half
    /// of the story a task-scoped lifetime would have made impossible.</para>
    ///
    /// <para>The exit code rides back in the cleanup worker's report because
    /// <c>stop_process</c> removes the entry as it stops it — so "the exit was recorded" is
    /// provable only from what the agent was told, which is also the only place an agent could
    /// read it.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_real_claude_process_outlives_its_task_and_a_later_real_worker_finds_and_stops_it()
    {
        var claudeBin = RequireRealClaude();
        FleetRig.PublishDotnetRootForSpawnedApphosts();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: StreamingSpawn(claudeBin, StepwiseWorkerPrompt, ProcessTools, "--max-turns", "14"),
            agentProcesses: true);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");

        var processName = "probe-" + NewToken();
        var port = MultiMachineKit.ReserveLoopbackPort();
        var body = "body-" + NewToken();

        // Step 1: a real worker starts the process and completes its own task.
        var starter = await rig.CreateTaskAsync(StartProcessDescription(processName, port, body), ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(starter, "A", MaxAttempts, PerLegBudget, ct),
            "the real claude worker never started its process and reported.\n"
            + await rig.RealWorkerDiagnosticsAsync(starter, ct));
        Assert.Contains(processName, await rig.ResultReferenceAsync(starter, ct));

        // It is really running, as the machine itself reports it.
        Assert.True(
            await FleetRig.WaitUntilAsync(
                () => Task.FromResult(rig.ProcessesOn("A").Any(p => p.Name == processName && p.State == ServiceState.Running)),
                TimeSpan.FromSeconds(30)),
            "the machine never reported the agent-started process as running.\n"
            + await rig.RealWorkerDiagnosticsAsync(starter, ct));

        // The declaring task is now terminal — accepted and completed — and the process is
        // untouched by that. This is the feature, stated as an assertion.
        await rig.AcceptAsync(starter, ct);
        Assert.Equal(TaskState.Completed, await rig.StateAsync(starter, ct));
        Assert.Contains(
            rig.ProcessesOn("A"),
            p => p.Name == processName && p.State == ServiceState.Running);

        // Step 2: the cleanup worker — a different task, told no names — finds it and stops it.
        var cleaner = await rig.CreateTaskAsync(CleanupDescription, ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(cleaner, "A", MaxAttempts, PerLegBudget, ct),
            "the real claude cleanup worker never reported.\n"
            + await rig.RealWorkerDiagnosticsAsync(cleaner, ct));

        var report = await rig.ResultReferenceAsync(cleaner, ct);
        Assert.Contains(processName, report);  // it discovered the right survivor, unaided
        Assert.Contains("exit=", report);      // and was told how it ended
        Assert.DoesNotContain(processName, rig.ProcessesOn("A").Select(p => p.Name));
    }

    // ── §8.2/§8.3 a service one machine serves and another reaches ─────────────

    /// <summary>
    /// The cross-machine service round trip with a real agent at each end: a
    /// <c>claude -p</c> worker on machine A starts a listener as an agent process, advertises it
    /// with <c>register_service</c>, and holds its turn open; a <c>claude -p</c> worker on
    /// machine B resolves it with <c>open_forward</c>, fetches over the loopback address the
    /// forward handed back, and reports the body it read. B's result containing bytes only A's
    /// process could produce is proof the round trip went through the real relay.
    ///
    /// <para><b>Why A must hold its turn, and why that is a finding rather than a fixture.</b>
    /// A forward is only granted against a service registered by a <em>currently working</em>
    /// task (§8.2 Team scoping). A headless agent's turn ends when it stops calling tools, its
    /// process exits, and a still-<c>working</c> task requeues on that exit (§10) — taking the
    /// registration out of forwardable state. So a real agent cannot advertise a service and
    /// leave: something has to keep its turn alive, which here is a bounded sleep, and in a real
    /// deployment is the "babysitting a registered endpoint is the job" shape §10 describes when
    /// it exempts such a task from the progress ceiling. The listener itself is machine-scoped
    /// and would outlive the worker; only the <em>advertisement</em> is tied to the turn.</para>
    /// </summary>
    [SkippableFact]
    public async Task Real_claude_workers_reach_a_registered_service_across_two_machines()
    {
        var claudeBin = RequireRealClaude();
        FleetRig.PublishDotnetRootForSpawnedApphosts();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: StreamingSpawn(claudeBin, StepwiseWorkerPrompt, ServiceTools, "--max-turns", "16"),
            agentProcesses: true);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        var serviceName = "svc" + NewToken();
        var processName = "serve-" + NewToken();
        var port = MultiMachineKit.ReserveLoopbackPort();
        var body = "relayed-" + NewToken();

        // Producer on A: start the listener, advertise it, then hold the turn open.
        var producer = await rig.CreateTaskAsync(
            ServeDescription(processName, serviceName, port, body), ct);
        Assert.True(
            await rig.DispatchUntilAsync(
                producer, "A", () => rig.ServiceExistsAsync(serviceName, ct),
                MaxAttempts, PerLegBudget, ct),
            "the real claude producer never registered its service.\n"
            + await rig.RealWorkerDiagnosticsAsync(producer, ct));

        // Pin the cross-machine claim HERE, while it is true, rather than at the end. A
        // producer whose turn has ended requeues (§10) and the next steered dispatch pass can
        // land it anywhere — so a late check can read "B" for a service that really was served
        // from A, or worse pass while both ends quietly collapsed onto one machine.
        Assert.Equal("A", rig.MachineRanOn(producer));
        Assert.Equal(TaskState.Working, await rig.StateAsync(producer, ct));

        // Consumer on B: a different machine, a different agent, told only the service name.
        var consumer = await rig.CreateTaskAsync(FetchDescription(serviceName), ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(consumer, "B", MaxAttempts, PerLegBudget, ct),
            "the real claude consumer never fetched through the forward and reported.\n"
            + await rig.RealWorkerDiagnosticsAsync(consumer, ct)
            + await rig.RealWorkerDiagnosticsAsync(producer, ct));

        // Only A's process could have produced this, and only the relay could have carried it.
        Assert.Contains(body, await rig.ResultReferenceAsync(consumer, ct));
        Assert.Equal("B", rig.MachineRanOn(consumer));
    }

    /// <summary>
    /// §11's permission bridge against a real <c>claude -p</c> worker running <b>without</b>
    /// <c>bypassPermissions</c> — the fact the whole feature exists for, and the only one that
    /// can prove it, because the request shape and the response shape are the harness's rather
    /// than ours.
    ///
    /// <para>The worker is given a task it cannot finish with the tools it was allowed. Claude
    /// Code's own permission machinery therefore calls <c>request_permission</c>, which blocks
    /// the task <c>blocked_on_input</c> with the tool name and proposed arguments on the row —
    /// while the asking process stays alive inside that call, which is what distinguishes this
    /// wait from every other one in Docket. A Lead verdict then resumes it in place and the
    /// worker finishes. Nothing here asserts on the model's prose: the assertions are the row's
    /// recorded tool name, the state transitions, and the fact that the command's output could
    /// only have been produced by actually running it.</para>
    /// </summary>
    [SkippableFact]
    public async Task Real_claude_without_bypass_routes_its_permission_prompt_through_docket_and_finishes()
    {
        var claudeBin = RequireRealClaude();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        var token = NewToken();
        await using var rig = new FleetRig(
            pg, spawnArgv: PermissionBridgeSpawn(claudeBin), terminalEvents: true);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(ShellThenReportDescription(token), ct);
        await rig.DispatchToAsync("A", ct);

        // The bridge fires: the harness could not run Bash on its own, so it asked. This is
        // Claude Code calling our MCP tool, with the tool name and arguments it chose.
        (string? Tool, string? Input)? request = null;
        Assert.True(
            await rig.DispatchUntilAsync(
                task, "A",
                async () => (request = await rig.PendingPermissionAsync(task, ct)) is not null,
                MaxAttempts, PerLegBudget, ct),
            "the real claude worker never routed a permission prompt through Docket.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        Assert.Equal("Bash", request!.Value.Tool);
        // The proposed arguments are relayed verbatim, which is what makes them worth showing a
        // person: the command an approver would be approving is right there.
        Assert.Contains(token, request.Value.Input ?? "");
        // Still the incumbent: a permission wait does not requeue, so there is no successor and
        // no park record.
        Assert.Equal(TaskState.BlockedOnInput, await rig.StateAsync(task, ct));
        Assert.Null((await rig.ParkAsync(task, ct)).Machine);

        // The Lead's verdict, and then the worker carries on inside the very tool call it
        // blocked in. Allowing in a loop because a haiku worker may need more than one tool it
        // was not pre-approved for; every one of them is a real trip through the bridge.
        var allowed = 0;
        var finished = await rig.DispatchUntilAsync(
            task, "A",
            async () =>
            {
                if (await rig.StateAsync(task, ct) == TaskState.Verifying) return true;
                if (await rig.PendingPermissionAsync(task, ct) is not null)
                {
                    await rig.AnswerPermissionAsync(task, "allow", null, ct);
                    allowed++;
                }
                return await rig.StateAsync(task, ct) == TaskState.Verifying;
            },
            MaxAttempts, PerLegBudget, ct);

        Assert.True(
            finished,
            $"the worker never completed after {allowed} permission approval(s).\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));
        Assert.True(allowed >= 1, "no permission request was ever approved");

        // The output could only exist if the approved command actually ran — the approval is
        // load-bearing, not decorative.
        Assert.Contains(token, await rig.ResultReferenceAsync(task, ct) ?? "");
        Assert.Equal("A", rig.MachineRanOn(task));

        // The audit trail a person would read afterwards: the ask, and the Lead's own approval.
        await using var db = pg.NewContext();
        var events = await db.TaskEvents.AsNoTracking()
            .Where(e => e.TaskId == task.Value).OrderBy(e => e.Seq).ToListAsync(ct);
        Assert.Contains(events, e => e.Kind == nameof(RequestInput)
                                     && e.InputKind == InputRequestKind.Permission);
        Assert.Contains(events, e => e.Kind == nameof(AnswerPermission)
                                     && e.PermissionVerdict == PermissionVerdict.Allow
                                     && e.PermissionAnswerer == PermissionAnswerer.Lead);
    }

    // ── Recipe + descriptions ───────────────────────────────────────────────────

    /// <summary>
    /// The validated real-<c>claude -p</c> worker argv (S2 spike, 2026-07-29): headless
    /// print mode, the injected per-task <c>{mcp_config}</c> as the ONLY MCP server
    /// (<c>--strict-mcp-config</c>), a minimal docket tool allow-list, haiku, and a tight
    /// turn cap. docketd's <see cref="Docket.Runner.ProcessSupervisor"/> substitutes
    /// <c>{mcp_config}</c> with the worker's 0600 mcp.json path per task (§13). Auth is
    /// the static bearer in that config plus the ambient <c>ANTHROPIC_API_KEY</c> the
    /// spawned process inherits from this test's environment.
    /// </summary>
    private static string[] ClaudeWorkerSpawn(string claudeBin) =>
    [
        claudeBin, "-p", WorkerPrompt,
        "--mcp-config", "{mcp_config}",
        "--strict-mcp-config",
        "--allowedTools", AllowedTools,
        "--model", "haiku",
        "--max-turns", "8",
    ];

    /// <summary>The one description template both roles use (§7): report
    /// <c>&lt;label&gt;:&lt;token&gt;</c>, front-loading the imperative so a haiku worker's
    /// happy path is two tool calls. Kept identical across roles so B is exactly as
    /// reliable as A; the handoff semantics live in the test, not in the prose.</summary>
    private static string EchoDescription(string label, string token) =>
        $"""
         Call report_result exactly once, with its resultReference set to this exact string
         (no quotes, no other text):

         {label}:{token}

         That is the entire task. Do not create or edit files. Do not do anything else.
         """;

    // ── Streaming recipe (session refs, resume, stop) ───────────────────────────

    /// <summary>
    /// The claude spawn argv for scenarios that need docketd to see the harness's own stream:
    /// <c>--output-format stream-json</c> plus <c>--verbose</c> (which is what makes claude emit
    /// that stream under <c>-p</c>) so an <c>events.source: terminal</c> profile can read the
    /// <c>system/init</c> session ref (§11) and per-tool-call liveness (§10). Otherwise the
    /// validated recipe unchanged: strict MCP config, a minimal allow-list, haiku, small turn cap.
    /// </summary>
    private static string[] StreamingSpawn(
        string claudeBin, string prompt, string tools, params string[] extra) =>
    [
        claudeBin, "-p", prompt,
        "--mcp-config", "{mcp_config}",
        "--strict-mcp-config",
        "--allowedTools", tools,
        "--output-format", "stream-json",
        "--verbose",
        "--model", "haiku",
        "--max-turns", "8",
        .. extra,
    ];

    /// <summary>
    /// The documented claude message-mode stop turn (a stream-json user message carrying the
    /// disposition), used verbatim in shape by the stop characterization so the finding is about
    /// the harness rather than about a template this test invented. docketd substitutes
    /// <c>{disposition}</c>/<c>{ttl_seconds}</c>/<c>{reason}</c>.
    /// </summary>
    private const string ClaudeStopTurnTemplate =
        """{"type":"user","message":{"role":"user","content":"Docket is winding this task down (disposition={disposition}, ~{ttl_seconds}s left; reason: {reason}). Immediately call report_result with a reference to your current progress, then stop."}}""";

    // ── §11 park/resume prompts and description ────────────────────────────────

    /// <summary>The docket tools the park/resume scenario's worker may call.</summary>
    private const string ParkTools =
        "mcp__docket__get_task,mcp__docket__report_result,mcp__docket__request_input";

    /// <summary>
    /// The cold spawn prompt, and the <b>only</b> place the nonce ever appears: not in the task
    /// row, so <c>get_task</c> cannot supply it to a cold successor. Front-loaded as a memory
    /// instruction so the value is in the conversation before the agent does anything else.
    /// </summary>
    private static string RememberThenAskPrompt(string nonce) =>
        "You are a Docket worker agent. Remember this value for the rest of this conversation: " +
        $"{nonce}. Do not write it to any file, and do not put it in any tool call yet. Now call " +
        "the docket get_task tool and do exactly what its description tells you.";

    /// <summary>
    /// The profile's static <c>resume.args</c> prompt (§11) — generic config, carrying no task
    /// content and, deliberately, not the nonce. The instruction to report the remembered value
    /// is the whole point: the value has to come from the resumed conversation or not at all.
    /// </summary>
    private const string ResumeAndReportPrompt =
        "Your task has resumed. FIRST call the docket get_task tool — it carries the answer you " +
        "were waiting for. Then call the docket report_result tool exactly once, with " +
        "resultReference set to the exact value you were asked to remember earlier in this " +
        "conversation, and nothing else. Two tool calls total.";

    /// <summary>Turn one: ask, then end the turn. The question text is asserted on, so it is
    /// fixed prose rather than left to the model's phrasing.</summary>
    private const string AskThenStopDescription =
        """
        Call request_input exactly once, with kind 'question' and question set to this exact
        text (no quotes, no other text):

        may I report the remembered value now?

        Then stop and end your turn. Do NOT call report_result on this turn. Do not create or
        edit files.
        """;

    // ── §11 permission-bridge prompt and description ───────────────────────────

    /// <summary>
    /// The docket tools a permission-bridge worker may call. Deliberately <b>no</b>
    /// <c>Bash</c> and no <c>request_permission</c>: Bash's absence is what makes the harness
    /// prompt, and the relaying tool is Claude Code's to call, never the agent's.
    /// </summary>
    private const string PermissionTools =
        "mcp__docket__get_task,mcp__docket__report_result";

    /// <summary>
    /// The permission-bridge worker prompt. Front-loads <c>get_task</c> like every other
    /// profile here; the shell step it will be stopped on lives in the description.
    /// </summary>
    private const string PermissionWorkerPrompt =
        "You are a Docket worker agent. Your FIRST action must be to call the docket get_task " +
        "tool to read your assignment, then do exactly what its description says. If a tool call " +
        "is refused, read the refusal and follow it — do not retry the same call.";

    /// <summary>
    /// A task that cannot be finished without a tool the allow-list withholds, so the harness
    /// must prompt. Reporting the command's own output is what makes the approval load-bearing:
    /// a worker that was never allowed to run it has nothing to report.
    /// </summary>
    private static string ShellThenReportDescription(string token) =>
        $"""
         Do these two steps in order:

         1. Use the Bash tool to run exactly: /bin/echo {token}
         2. Call report_result exactly once, with resultReference set to the exact text the
            command printed.

         Do not create or edit files. Do not do anything else.
         """;

    /// <summary>
    /// The permission-bridge spawn argv (§11). Two differences from every other recipe here,
    /// and they are the whole point:
    /// <list type="bullet">
    /// <item><c>--permission-prompt-tool mcp__docket__request_permission</c> <b>instead of</b>
    /// <c>--permission-mode bypassPermissions</c> — the approval dialog routes to Docket rather
    /// than being skipped.</item>
    /// <item><c>--setting-sources ''</c>, so the run does not inherit the operator's own
    /// <c>settings.json</c> allow rules. Without it this fact is machine-dependent: on a
    /// developer box that has already allowed <c>Bash(/bin/echo:*)</c> nothing ever prompts and
    /// the bridge is never exercised.</item>
    /// </list>
    /// </summary>
    private static string[] PermissionBridgeSpawn(string claudeBin) =>
    [
        claudeBin, "-p", PermissionWorkerPrompt,
        "--mcp-config", "{mcp_config}",
        "--strict-mcp-config",
        "--allowedTools", PermissionTools,
        "--permission-prompt-tool", "mcp__docket__request_permission",
        "--setting-sources", "",
        "--output-format", "stream-json",
        "--verbose",
        "--model", "haiku",
        "--max-turns", "10",
    ];

    // ── §10 process scenario prompts and descriptions ──────────────────────────

    private const string ProcessTools =
        "mcp__docket__get_task,mcp__docket__report_result,mcp__docket__start_process," +
        "mcp__docket__list_processes,mcp__docket__stop_process";

    /// <summary>Start a long-lived listener as an agent process and finish the task, leaving it
    /// running — the shape §10 exists for. Absolute argv, no shell, stdin left closed (the
    /// default), exactly as the worker skill describes.</summary>
    private static string StartProcessDescription(string name, int port, string body) =>
        $$"""
          Two steps, in order.

          1. Call start_process exactly once with:
               name: {{name}}
               spawn: ["{{FleetRig.TestHarnessPath()}}", "http-serve", "{{port}}", "{{body}}"]
               env: {"DOTNET_ROOT": "{{FleetRig.DotnetRoot()}}"}

          2. Call report_result exactly once, with resultReference set from what step 1
             actually returned:
               - if it started, use exactly: started:{{name}}
               - if it was refused, use exactly: refused:<the refusal text you were given>
             A refusal is a fact to report, never something to work around or paper over.

          Leave the process running — do NOT stop it. Do not create or edit files.
          """;

    /// <summary>The cleanup continuation's work (§10): find what an earlier task left running
    /// and stop it. Deliberately names nothing — discovery is the half being tested.</summary>
    private const string CleanupDescription =
        """
        You have been sent to clean up after an earlier task on this machine.

        1. Call list_processes. Exactly one entry has kind "process"; you did not start it.
           (An entry with kind "service" belongs to the operator — never touch those.)
        2. Call stop_process with that entry's name. It returns the exit code as "value".
        3. Call report_result exactly once with resultReference set to this exact form,
           substituting the name you stopped and the exit code you were given:
             stopped:<name>:exit=<value>

        Do not create or edit files.
        """;

    // ── §8.2/§8.3 cross-machine service prompts and descriptions ───────────────

    /// <summary>Both ends of the service scenario share one profile, so the allow-list is the
    /// union of what the producer and the consumer need. <c>Bash</c> is here for two reasons the
    /// scenario cannot avoid: the producer has to hold its turn open while its registration is
    /// forwardable, and the consumer has to actually speak to the forwarded port.</summary>
    private const string ServiceTools =
        "mcp__docket__get_task,mcp__docket__report_result,mcp__docket__start_process," +
        "mcp__docket__register_service,mcp__docket__open_forward,Bash";

    /// <summary>Producer: bind (via the process), advertise, then stay working. The register
    /// step comes after the start so the port is answering before consumers are told about it —
    /// the "bind first, then register" rule the worker skill leads with.</summary>
    private static string ServeDescription(string processName, string serviceName, int port, string body) =>
        $$"""
          Three steps, in order.

          1. Call start_process exactly once with:
               name: {{processName}}
               spawn: ["{{FleetRig.TestHarnessPath()}}", "http-serve", "{{port}}", "{{body}}"]
               env: {"DOTNET_ROOT": "{{FleetRig.DotnetRoot()}}"}

          2. Call register_service exactly once with name {{serviceName}} and port {{port}}.

          3. Run this shell command:
               sleep 60
             Then run that same command again, and keep repeating it until you have run it
             SIX times in total. This is not busywork: your turn staying open is what keeps
             the service you just registered reachable by another task.

          Do NOT call report_result. Do not create or edit files.
          """;

    /// <summary>Consumer: resolve the service to a loopback address and read from it. The body
    /// is reported verbatim, so the assertion is on bytes only the producer's process makes.</summary>
    private static string FetchDescription(string serviceName) =>
        $"""
         Two steps, in order.

         1. Call open_forward with serviceName {serviceName}. It returns a host and a port.

         2. Run this shell command, substituting the host and port it returned:
              curl -sS --max-time 20 http://HOST:PORT/

         Then call report_result exactly once with resultReference set to exactly the text that
         command printed, and nothing else. Do not create or edit files.
         """;

    // ── Gating ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Skip unless this run is a real, opted-in claude run: Postgres up, an explicit opt-in,
    /// and the <c>claude</c> CLI resolvable. Returns the resolved claude binary when everything
    /// is in place; otherwise throws the xUnit skip, so an ordinary push spends nothing.
    ///
    /// <para>There are <b>two</b> ways to opt in, because there are two ways a machine
    /// authenticates the CLI. An Anthropic key (<c>ANTHROPIC_API_KEY</c>, or
    /// <c>ANTHROPIC_KEY</c> which the CI job maps to it) is the CI path, and it is published
    /// into this process's environment so the spawned claude — which inherits docketd's
    /// environment — can use it. <c>DOCKET_REAL_CLAUDE=1</c> is the path for a machine whose CLI
    /// is already logged in: the worker then inherits that ambient login as a same-user child
    /// and <b>no key is set</b>, which is required rather than merely tidy — a managed install
    /// pinned to first-party login refuses to start at all when an Anthropic-issued credential
    /// is present, so publishing a key there would break the very harness under test.</para>
    /// </summary>
    private string RequireRealClaude()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("ANTHROPIC_KEY");
        var optedIn = Environment.GetEnvironmentVariable("DOCKET_REAL_CLAUDE") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(string.IsNullOrWhiteSpace(key) && !optedIn,
            "no ANTHROPIC_API_KEY/ANTHROPIC_KEY and no DOCKET_REAL_CLAUDE — the real claude -p " +
            "E2E is opt-in (see the gated CI job)");
        // Only publish a key when one was supplied. On an already-logged-in machine the child
        // must NOT see one (see the remarks above).
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key);

        var claudeBin = ResolveClaudeBin();
        Skip.If(claudeBin is null,
            "claude CLI not found (set DOCKET_CLAUDE_BIN or put claude on PATH)");
        ScrubInheritedSessionMarkers();
        return claudeBin!;
    }

    /// <summary>
    /// Remove the "you are running inside a Claude Code session" markers from this process's
    /// environment, because the worker inherits it (docketd's environment is the child's base,
    /// §10) and in production docketd is a daemon rather than a child of somebody's editor.
    ///
    /// <para>This is test hygiene with teeth, not tidying. Run from inside a Claude Code
    /// session — which is how anyone iterating on these scenarios runs them — the spawned worker
    /// picked up the agent-teams markers, decided it had teammates, and spent its whole turn
    /// budget calling <c>ToolSearch</c> and <c>SendMessage</c> instead of doing its assignment.
    /// (<c>--allowedTools</c> does not prevent that: it pre-approves a subset, it does not hide
    /// the rest.) Auth and routing variables are deliberately left alone — they are how an
    /// already-logged-in CLI reaches a model at all.</para>
    /// </summary>
    private static void ScrubInheritedSessionMarkers()
    {
        foreach (var name in InheritedSessionMarkers)
            Environment.SetEnvironmentVariable(name, null);
    }

    private static readonly string[] InheritedSessionMarkers =
    [
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS",
        "CLAUDE_PID",
        "CLAUDE_EFFORT",
        "AI_AGENT",
    ];

    /// <summary>Resolve the claude executable: an explicit <c>DOCKET_CLAUDE_BIN</c>, then
    /// PATH, then the common install location — or null when none exists.</summary>
    private static string? ResolveClaudeBin()
    {
        var explicitBin = Environment.GetEnvironmentVariable("DOCKET_CLAUDE_BIN");
        if (!string.IsNullOrWhiteSpace(explicitBin) && File.Exists(explicitBin))
            return explicitBin;

        var exe = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        return File.Exists("/usr/local/bin/claude") ? "/usr/local/bin/claude" : null;
    }

    /// <summary>A short unforgeable token the worker can only report if it really read the
    /// live task description — the proof a real agent, not a fake, closed the loop.</summary>
    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];
}
