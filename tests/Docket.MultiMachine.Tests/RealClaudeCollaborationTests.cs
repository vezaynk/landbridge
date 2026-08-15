using Docket.Contracts;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;
using Microsoft.EntityFrameworkCore;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// The multi-machine collaboration crown (spec §8.3), <b>real Claude ACP tier</b>:
/// the same real plane + real relay + N real <c>docketd</c> rigs the scripted
/// <see cref="MultiMachineCollaborationTests"/> stand up, but each machine's
/// <c>default</c> profile spawns <c>claude-agent-acp</c> instead of the no-LLM
/// <c>Docket.CollabHarness</c>. Nothing below the spawn seam changes — this is the §10
/// config-only harness promise exercised for real: the worker learns its assignment
/// from <c>session/new</c> MCP + <c>get_task</c>, does the work, and reports back.
///
/// <para><b>Opt-in, token-spending, and deliberately kept out of the default suite.</b>
/// The real-worker facts SKIP cleanly unless the run opted in — an Anthropic key in the
/// environment (<c>ANTHROPIC_API_KEY</c>, or <c>ANTHROPIC_KEY</c> which the opt-in CI job
/// maps to it), or <c>DOCKET_REAL_CLAUDE=1</c> on a machine whose CLI is already logged in
/// — AND the <c>claude</c> CLI resolves; so a normal push/PR run, which does neither,
/// spends zero tokens. See <see cref="RequireRealClaude"/> for why the two paths are not
/// interchangeable. The dedicated CI job (see
/// <c>.github/workflows/ci.yml</c>) sets the key and runs <em>only</em> this trait. Kept
/// tiny and tolerant of a single flaked worker turn (bounded redispatch) so a full
/// run costs a few cents and a lone haiku hiccup doesn't red the job.</para>
///
/// <para>The portable minimum bar — verifying + session ref, usage/cost, park → resume —
/// lives in <see cref="RealHarnessBar"/> and is wrapped below so this class's
/// <c>Category=RealClaude</c> trait still isolates the job. Characterization that is
/// Claude's own (handoff, continuation, stop-as-unread-turn, permission, service)
/// stays in this file.</para>
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

    /// <summary>
    /// The standing rule every prompt here carries, and it is a fix for a real failure rather
    /// than boilerplate. These prompts used to say "call the docket get_task tool"; a real
    /// claude 2.1.231 worker read that as a shell command and ran <c>docket get_task</c> through
    /// <c>Bash</c> — a program that does not exist anywhere in this repo, which builds
    /// <c>docketd</c> and nothing else — rather than calling the MCP tool it was already
    /// allow-listed for. Two facts died that way, and one of them looked like a permission-bridge
    /// regression because the phantom shell command is what the bridge dutifully recorded.
    ///
    /// <para>So the tool names are now spelled the way the harness actually exposes them, which
    /// has no reading as a command line. Two clauses are scoped narrowly on purpose, because the
    /// obvious blanket wording would break the very facts this is meant to protect. The
    /// <c>curl</c> ban names the MCP server only — the service scenario's consumer must curl a
    /// <em>forwarded service port</em>. And the last sentence says "missing or errors" rather than
    /// "refused": a refusal is the permission bridge working, and a worker told to report refusals
    /// would abandon the Bash call it is supposed to wait out and then complete.</para>
    /// </summary>
    private const string McpToolsRule =
        " Docket's tools are MCP tools, named exactly mcp__docket__get_task, " +
        "mcp__docket__report_result and so on — call them as tools, under those names. There is " +
        "no `docket` program: no such command exists on this machine, so never run `docket` in a " +
        "shell, and never try to reach the docket MCP server yourself over HTTP or with curl. (A " +
        "shell command your assignment explicitly asks for is a different thing, and is fine.) If " +
        "a docket MCP tool is missing or errors, report that with mcp__docket__report_result " +
        "instead of working around it.";

    /// <summary>Generic worker prompt (§7): the specifics live in each task's opaque
    /// <b>description</b>, read via <c>get_task</c>, so one profile drives every role.</summary>
    private const string WorkerPrompt =
        "You are a Docket worker agent. Your FIRST action must be to call the " +
        "mcp__docket__get_task tool to read your assignment. The assignment's description tells " +
        "you the exact string to report. Your ONLY other action is to call the " +
        "mcp__docket__report_result tool once, with that exact string as resultReference. Do not " +
        "write files, do not explain, do not ask questions. Two tool calls total: " +
        "mcp__docket__get_task, then mcp__docket__report_result." + McpToolsRule;

    /// <summary>
    /// The prompt for scenarios whose description asks for more than an echo (§7: the
    /// specifics belong in the description, and the profile prompt must not contradict them).
    /// <see cref="WorkerPrompt"/> cannot be reused there — it pins the worker to
    /// "two tool calls total: get_task, then report_result", and a worker that obeys the prompt
    /// over its assignment skips the very tool the scenario is about, then cheerfully reports
    /// success. That is not a hypothetical: it is what a real haiku worker did.
    /// </summary>
    private const string StepwiseWorkerPrompt =
        "You are a Docket worker agent. Your FIRST action must be to call the " +
        "mcp__docket__get_task tool to read your assignment. Its description lists numbered " +
        "steps: carry them out in order, exactly as written, using the tools it names. Do not add " +
        "steps, do not skip steps, and do not substitute one tool for another. Do not write or " +
        "edit files unless a step tells you to. Do not explain and do not ask questions." +
        McpToolsRule;

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public Task Real_worker_drives_a_task_to_verifying_on_the_fleet() =>
        RealHarnessBar.DriveToVerifyingAsync(pg, RealHarnessProfiles.Claude(RequireRealClaude()));

    [SkippableFact]
    public Task Real_worker_reports_usage_the_harness_emits() =>
        RealHarnessBar.ReportsUsageAsync(pg, RealHarnessProfiles.Claude(RequireRealClaude()));

    [SkippableFact]
    public Task Real_worker_resumes_its_transcript_after_a_park_and_reports_a_memory_only_nonce() =>
        RealHarnessBar.ResumesAfterParkAsync(pg, RealHarnessProfiles.Claude(RequireRealClaude()));

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
        var profile = RealHarnessProfiles.Claude(RequireRealClaude());
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg, profile.AcpSpawn, prompt: profile.EchoPrompt, followUp: profile.FollowUpTurn);
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

    /// <summary>
    /// §11 <b>continuation</b> — "talk to the agent that has the context" — end to end against
    /// the real CLI: a completed task's transcript is carried into a <em>new</em> task, and the
    /// continuation worker reports a value that only that conversation holds.
    ///
    /// <para>This is the second half of what #102 blocked, and it needed one thing park-resume
    /// did not: the harness has to run in the continued task's directory
    /// (<c>DispatchCommand.WorkDirTask</c>, seeded from the row and resolved transitively so
    /// chains land on the root). A continuation runs there whether or not it resumes — the
    /// workspace is the work (§7) — and resuming additionally <em>requires</em> it, which is
    /// what this fact turns on: a harness session is <b>directory</b>-local as well as
    /// machine-local, so a resume aimed at the continuation's own new, never-used directory
    /// fails outright. A task, never a path: work_root is machine-local runner config, so the
    /// plane names a task and the runner maps it. The cold-start half of the same rule is
    /// scripted and token-free, in <c>MultiMachineCollaborationTests</c>.</para>
    ///
    /// <para>The nonce is the proof, and it is airtight for the same reason as above: it appears
    /// only in the FIRST task's spawn prompt. The continuation's row is new — its description
    /// never carries it, so <c>get_task</c> cannot supply it — and the resume argv is static
    /// profile config. A cold-started continuation reaches verifying too; only a resumed one
    /// reaches it with this value.</para>
    /// </summary>
    [SkippableFact]
    public async Task Real_claude_continues_a_finished_tasks_conversation_from_that_tasks_directory()
    {
        var claudeBin = RequireRealClaude();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cts.Token;

        // The value the first worker is told, and — separately — the part of it that is the
        // proof. The hex is random per run and appears ONLY in the first task's spawn prompt,
        // so a continuation reporting it can only have resumed the inherited conversation;
        // that is the whole assertion. The "nonce-" prefix is decoration, and a model can
        // defensibly read it as a label on the value rather than part of it — claude 2.1.226
        // did exactly that, reporting the hex alone. Asserting the prefixed string would make
        // this fact hostage to the next model's reading of a word we chose, and tightening the
        // prompt would only move that hostage, so the assertion is on the hex.
        var remembered = NewToken();
        var nonce = "nonce-" + remembered;
        await using var rig = new FleetRig(
            pg,
            // The first task's worker is told the nonce and reports something else; the
            // continuation's is asked for the nonce back. Same turn headroom as the park fact,
            // for the same reason.
            // One entry point, both legs: the continuation resumes with session/load on the
            // connection its spawn opens, so there is no second argv to declare.
            spawnArgv: ["claude-agent-acp"],
            prompt: RememberThenWorkPrompt(nonce),
            followUp: ContinuationReportPrompt);
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");

        // Task one: an ordinary task that finishes. Its worker holds the nonce in conversation.
        var first = await rig.CreateTaskAsync(EchoDescription("A", "first-done"), ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(first, "A", MaxAttempts, PerLegBudget, ct),
            "the first real claude worker never drove its task to verifying.\n"
            + await rig.RealWorkerDiagnosticsAsync(first, ct));
        var firstSession = await rig.HarnessSessionRefAsync(first, ct);
        Assert.False(
            string.IsNullOrWhiteSpace(firstSession),
            "no harness session ref was stamped on the first task, so there is nothing to continue.\n"
            + await rig.RealWorkerDiagnosticsAsync(first, ct));

        // Accept it, so the continuation really is of a FINISHED task — the ordinary shape of
        // "talk to the agent that has the context", and the one where the predecessor's process
        // is long gone rather than merely superseded.
        await rig.AcceptAsync(first, ct);
        Assert.Equal(TaskState.Completed, await rig.StateAsync(first, ct));

        // Task two continues it: a new task id, seeded with the inherited session ref and the
        // machine that ran it, and asking for the remembered value.
        var second = await rig.CreateTaskAsync(ContinuationDescription, ct, continues: first);
        Assert.Equal(firstSession, await rig.HarnessSessionRefAsync(second, ct));

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(second, "A", MaxAttempts, PerLegBudget, ct),
            "the continuation worker never drove its task to verifying.\n"
            + await rig.RealWorkerDiagnosticsAsync(second, ct));

        // The value only the inherited conversation held — the hex, not the prefixed string
        // (see where `remembered` is minted).
        Assert.Contains(remembered, await rig.ResultReferenceAsync(second, ct));

        // Harness-side proof, independent of anything the agent said: two DIFFERENT tasks'
        // captured instances report the SAME session id on their own system/init. A cold start
        // mints a new one, so only a real resume produces this — and the resume could only have
        // happened from the first task's directory, since that is the only one holding a
        // session. Transcripts stay keyed by the dispatched task even though the work dir is
        // shared, which is what keeps the two legible apart here.
        //
        // Counted as "every instance, whichever leg it belonged to", not as one apiece: either
        // leg is allowed its bounded retry (a haiku that ends a turn without the tool call), and
        // a retry resumes rather than re-briefs, so an extra instance carries the same id and is
        // not a different outcome. Zero instances would be, hence the non-empty check.
        var firstInstances = rig.InstanceSessionIdsOn("A", first);
        var continuationInstances = rig.InstanceSessionIdsOn("A", second);
        Assert.NotEmpty(firstInstances);
        Assert.NotEmpty(continuationInstances);
        Assert.All(firstInstances, id => Assert.Equal(firstSession, id));
        Assert.All(continuationInstances, id => Assert.Equal(firstSession, id));
        Assert.Equal("A", rig.MachineRanOn(second));
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
        RequireRealClaude();
        FleetRig.PublishDotnetRootForSpawnedApphosts();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: ["claude-agent-acp"],
            agentProcesses: true,
            prompt: StepwiseWorkerPrompt,
            followUp: "There is new input on your assignment. Call mcp__docket__get_task to read it, then continue.");
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");

        var processName = "probe-" + NewToken();
        var port = PlaneProbe.ReserveLoopbackPort();
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
        RequireRealClaude();
        FleetRig.PublishDotnetRootForSpawnedApphosts();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: ["claude-agent-acp"],
            agentProcesses: true,
            prompt: StepwiseWorkerPrompt,
            followUp: "There is new input on your assignment. Call mcp__docket__get_task to read it, then continue.");
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        var serviceName = "svc" + NewToken();
        var processName = "serve-" + NewToken();
        var port = PlaneProbe.ReserveLoopbackPort();
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


    // ── Recipe + descriptions ───────────────────────────────────────────────────

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

    // ── §11 continuation prompts and description ───────────────────────────────

    /// <summary>
    /// The first task's spawn prompt, and the only place its nonce ever appears: the task it
    /// then does is an ordinary echo, so the nonce goes nowhere near the row, the result, or
    /// any file — only the conversation a later continuation inherits.
    /// </summary>
    private static string RememberThenWorkPrompt(string nonce) =>
        "You are a Docket worker agent. Remember this value for the rest of this conversation: " +
        $"{nonce}. Do not write it to any file, and do not put it in any tool call. Now call the " +
        "mcp__docket__get_task tool and do exactly what its description tells you." + McpToolsRule;

    /// <summary>
    /// The follow-up turn for the continuation leg (§11) — generic config carrying no
    /// task content, and deliberately not the nonce. Worded for a worker whose assignment
    /// is new but whose conversation is not.
    /// </summary>
    private const string ContinuationReportPrompt =
        "This conversation continues under a new task. FIRST call the mcp__docket__get_task tool " +
        "to read that new assignment, then do exactly what its description says. The value it asks " +
        "for is one you were told earlier in this conversation." + McpToolsRule;

    /// <summary>The continuation task's own description: it names no value, because the whole
    /// point is that the value comes from the inherited conversation and from nowhere the new
    /// task row could supply it.</summary>
    private const string ContinuationDescription =
        """
        Call report_result exactly once, with resultReference set to the exact value you were
        asked to remember earlier in this conversation, and nothing else.

        That is the entire task. Do not create or edit files. Do not do anything else.
        """;

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
