using Docket.Contracts;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// §10 BYO-harness, <b>third</b> harness: the same fleet driven by OpenCode
/// (<c>opencode run --format json</c>) instead of <c>claude -p</c> or <c>codex exec</c>.
/// Opt-in and token-spending, gated exactly like the other two real tiers.
///
/// <para><b>Provenance.</b> Everything about the recipe below was established by reading
/// OpenCode's source at tag <c>v1.18.17</c> (npm <c>opencode-ai@1.18.17</c>, the version the CI
/// job installs) — <b>no <c>opencode</c> binary was run</b> while writing it. The per-key
/// citations live on the members that use them; the parser half is pinned for $0 by
/// <c>Docket.Runner.Tests/OpenCodeStreamMappingTests</c>. This tier is where the reading gets
/// checked against reality.</para>
///
/// <para><b>Why OpenCode was the cheap harness to add.</b> It needed no seam Codex had not
/// already forced into existence: <c>stdin: closed</c> (#110) and the flat tool-call mode (#111)
/// are exactly the two knobs it requires. What it <em>did</em> force is one generalization of the
/// usage keys from bare property names to dotted paths (#142) — no new key — plus one semantic
/// boolean, <c>usage_reasoning_is_subset</c>, mirroring the <c>usage_cached_is_subset</c> that
/// Codex forced one bucket over.</para>
///
/// <para><b>Three differences from the Codex tier worth knowing before reading the facts.</b>
/// (1) The dead-man incompatibility is the same landmine but <em>silent</em> — where a hung
/// <c>codex exec</c> prints <c>Reading additional input from stdin...</c> to stderr, OpenCode
/// prints nothing at all and leaves an empty transcript. (2) MCP tool names are
/// <c>docket_get_task</c>, not <c>mcp__docket__get_task</c>
/// (<c>packages/opencode/src/mcp/catalog.ts:119</c>), so the worker prompt names tools bare —
/// the portable phrasing across all three harnesses. (3) There is no tool-call <em>start</em>
/// event, only completion (<c>run.ts:719</c>), so the progress clock necessarily lags by each
/// tool call's duration. None of these facts depends on that lag, but a long build on an
/// OpenCode profile looks wedged in a way it would not on Codex.</para>
///
/// <para><b>No turn cap, as with Codex.</b> <c>opencode run</c> has no <c>--max-turns</c> and no
/// budget flag, so cost here is bounded by the pinned model, trivial tasks, the per-leg budget
/// and the outer deadline.</para>
/// </summary>
[Trait("Category", RealOpenCode)]
[Collection(PostgresCollection.Name)]
public sealed class RealOpenCodeCollaborationTests(PostgresFixture pg) : IAsyncLifetime
{
    /// <summary>Trait value the opt-in CI job filters on so it runs <em>only</em> this tier.</summary>
    public const string RealOpenCode = "RealOpenCode";

    private const int MaxAttempts = 3;
    private static readonly TimeSpan PerLegBudget = TimeSpan.FromMinutes(8);

    /// <summary>
    /// Generic worker prompt (§7), deliberately the same shape as the other two tiers' so a
    /// cross-harness comparison means something — with one difference that is the point of
    /// OC-G3: the docket tools are named <b>bare</b>. OpenCode would spell them
    /// <c>docket_get_task</c> and claude/Codex <c>mcp__docket__get_task</c>, so a prompt that
    /// spells the qualified name is the one thing that does not port between harnesses. Naming
    /// them bare works on all three.
    /// </summary>
    private const string WorkerPrompt =
        "You are a Docket worker agent. Your FIRST action must be to call the docket get_task " +
        "tool to read your assignment. The assignment's description tells you the exact string " +
        "to report. Your ONLY other action is to call the docket report_result tool once, with " +
        "that exact string as resultReference. Do not write files, do not explain, do not ask " +
        "questions. Two tool calls total: get_task, then report_result.";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Why <c>stdin: closed</c> is not a preference for this harness either, and the cheapest
    /// fact in the tier: a cold <c>opencode run</c> worker under the §10 dead-man pipe
    /// <b>never takes a turn</b>. <c>run.ts:416</c> reads
    /// <c>const piped = process.stdin.isTTY ? undefined : await Bun.stdin.text()</c> —
    /// unconditionally, before the empty-prompt check at <c>:420</c> and before any session
    /// exists. A pipe is not a TTY, <c>Bun.stdin.text()</c> reads to EOF, and a <c>deadman</c>
    /// profile never sends one.
    ///
    /// <para><b>Worse than Codex in two ways</b>, which is why this is worth its own fact rather
    /// than a footnote on the Codex one. The argv prompt does not short-circuit the read (with
    /// both present <c>resolveRunInput</c> concatenates, <c>run.ts:40-50</c>), and there is no
    /// diagnostic: Codex at least announces <c>Reading additional input from stdin...</c> on
    /// stderr, while OpenCode hangs mutely. An operator who declares the wrong stdin policy here
    /// gets a worker that starts, produces zero bytes, and dies of the liveness window.</para>
    ///
    /// <para>Costs nothing — the worker hangs before it ever contacts a model — and is a
    /// permanent characterization rather than a delete-me tripwire, for the same reason the Codex
    /// one is: it holds the <em>reason</em> the profiles below close stdin. If it ever fails,
    /// OpenCode changed; re-read <c>run.ts</c> at the tag CI installs. The <c>closed</c> profiles
    /// stay correct either way, because an EOF nobody waits for costs nothing.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_cold_opencode_worker_hangs_on_docketds_dead_man_stdin_and_never_takes_a_turn()
    {
        var openCodeBin = RequireRealOpenCode();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: OpenCodeWorkerSpawn(openCodeBin, WorkerPrompt),
            terminalEvents: true,
            eventMapping: OpenCodeEventMapping,
            // The whole point of this fact. Declared explicitly rather than left to the default
            // so a future change of default cannot quietly turn this into a duplicate.
            stdin: StdinPolicy.Deadman);
        await rig.StartAsync(ct);
        using var config = OpenCodeConfig.Create(rig.McpUrl);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(EchoDescription("A", NewToken()), ct);
        await rig.DispatchToAsync("A", ct);

        Assert.True(
            await FleetRig.WaitUntilAsync(
                () => Task.FromResult(rig.WorkerObserved(task) is { Starts: > 0 }),
                TimeSpan.FromMinutes(1)),
            "the opencode worker never even started, so this run says nothing about stdin.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // And then nothing. OpenCode's first JSON line follows its first assistant step, which
        // on any working run arrives within seconds; two minutes of silence is the hang.
        Assert.False(
            await FleetRig.WaitUntilAsync(
                async () => await rig.HarnessSessionRefAsync(task, ct) is { Length: > 0 },
                TimeSpan.FromMinutes(2)),
            "an opencode worker under stdin: deadman DID report a session ref, so it no longer "
            + "blocks on the held-open pipe. That is a change in OPENCODE, not in Docket — check "
            + "whether cli/cmd/run.ts still reads Bun.stdin.text() unconditionally when stdin is "
            + "not a TTY (run.ts:416 at v1.18.17). Nothing below needs to change if so: the other "
            + "facts declare stdin: closed, and an EOF nobody waits for is harmless. Update this "
            + "characterization to say what the CLI does now.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // No session id reached any captured transcript either — the direct, harness-side half of
        // "it produced nothing", and the part that distinguishes OpenCode's silent hang from a
        // harness that at least said something before stalling.
        Assert.Empty(rig.InstanceSessionIdsOn("A", task));
        Assert.NotEqual(TaskState.Verifying, await rig.StateAsync(task, ct));
    }

    /// <summary>
    /// The minimum bar: a REAL <c>opencode run</c> worker, spawned by a real docketd on a
    /// two-machine fleet, reads its assignment off the wire and drives its task to
    /// <see cref="TaskState.Verifying"/>, reporting the exact unforgeable token its live
    /// description carried. Only a worker that really connected to Docket's HTTP MCP —
    /// authenticated with the per-instance bearer it resolved through <c>{env:...}</c>
    /// substitution — called <c>get_task</c> and read that description could produce it.
    ///
    /// <para>It is also the first real-stream confirmation of the two mapping seams
    /// <c>OpenCodeStreamMappingTests</c> establishes against source-derived fixtures: the §11
    /// session ref off <c>step_start</c>/<c>sessionID</c>, and the bearer arriving via the
    /// static config file rather than docketd's generated <c>{mcp_config}</c>.</para>
    /// </summary>
    [SkippableFact]
    public async Task Real_opencode_worker_drives_a_task_to_verifying_on_the_fleet()
    {
        var openCodeBin = RequireRealOpenCode();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: OpenCodeWorkerSpawn(openCodeBin, WorkerPrompt),
            terminalEvents: true,
            eventMapping: OpenCodeEventMapping,
            stdin: OpenCodeStdin);
        await rig.StartAsync(ct);
        using var config = OpenCodeConfig.Create(rig.McpUrl);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B"); // a real fleet: >1 machine enrolled, dispatch steered to A

        var token = NewToken();
        var task = await rig.CreateTaskAsync(EchoDescription("A", token), ct);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            "the real opencode worker never drove its task to verifying.\n"
            + OpenCodeFailureHypotheses() + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var reference = await rig.ResultReferenceAsync(task, ct);
        Assert.Contains(token, reference); // the live-description token round-tripped
        Assert.Equal("A", rig.MachineRanOn(task));

        Assert.False(
            string.IsNullOrWhiteSpace(await rig.HarnessSessionRefAsync(task, ct)),
            "no harness session ref was stamped, so events.mapping did not carry OpenCode's "
            + "step_start/sessionID — §11 resume would silently cold-start.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));
    }

    /// <summary>
    /// What a <c>stop</c> actually is against a real <c>opencode run</c> worker: a TTL'd kill,
    /// declared as <see cref="StopMode.Signal"/> because that is the true claim about this
    /// harness. Two source facts settle it. Stdin is read exactly once, at <c>run.ts:416</c>,
    /// before the event loop starts, and nothing reads it afterwards — so a wind-down turn
    /// written mid-task has nowhere to land. And the CLI entry point installs no
    /// <c>SIGTERM</c>/<c>SIGINT</c> handler at all: its only teardown is an unconditional
    /// <c>process.exit()</c> in a <c>finally</c> (<c>packages/opencode/src/index.ts:136-142</c>),
    /// so docketd's tree-kill arrives unhandled.
    ///
    /// <para>What survives is the plane's record rather than the agent's cooperation: the
    /// <c>sessionID</c> docketd captured is exactly what <c>opencode run --session &lt;id&gt;</c>
    /// takes, and it outlives the kill — so <c>preserve</c> means something without anything
    /// having been negotiated.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_stop_reaches_a_real_opencode_worker_as_a_kill_deadline_with_its_session_ref_preserved()
    {
        var openCodeBin = RequireRealOpenCode();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: OpenCodeWorkerSpawn(openCodeBin, SlowWorkerPrompt),
            terminalEvents: true,
            stop: new StopConfig(
                StopMode.Signal, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(5)),
            eventMapping: OpenCodeEventMapping,
            // signal + closed is the only pairing RunnerConfig accepts for this harness, and it
            // is the honest one twice over — see OpenCodeStdin.
            stdin: OpenCodeStdin);
        await rig.StartAsync(ct);
        using var config = OpenCodeConfig.Create(rig.McpUrl);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(EchoDescription("A", NewToken()), ct);
        await rig.DispatchToAsync("A", ct);

        // Wait for the worker to be genuinely under way — a session ref means its stream started,
        // which is the earliest point at which stopping it tests anything.
        Assert.True(
            await FleetRig.WaitUntilAsync(
                async () => await rig.HarnessSessionRefAsync(task, ct) is { Length: > 0 },
                TimeSpan.FromMinutes(5)),
            "the opencode worker never reported a session ref, so there was nothing to stop.\n"
            + OpenCodeFailureHypotheses() + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var refBeforeStop = await rig.HarnessSessionRefAsync(task, ct);

        Assert.True(
            await rig.SendStopAsync(
                "A", task, TimeSpan.FromMinutes(1), StopDisposition.PreserveAndPark,
                "characterizing real-opencode stop delivery", ct),
            "the stop was not delivered to the machine holding the task.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // A signal-mode stop writes no turn: docketd reports the deadline it armed, which is the
        // only thing it actually did. OpenCode installs no SIGTERM handler (index.ts:136-142), so
        // there is nothing on the other side to negotiate with even in principle.
        var ack = rig.StopAckFor(task);
        Assert.NotNull(ack);
        Assert.True(ack!.Value.Actioned);
        Assert.Equal(StopDelivery.DeadlineArmed, ack.Value.Delivery);

        // The kill really landed: the process is gone, and it did not exit cleanly on its own.
        Assert.True(
            await FleetRig.WaitUntilAsync(
                () => Task.FromResult(rig.WorkerObserved(task) is { Exits: > 0 }),
                TimeSpan.FromMinutes(3)),
            "the stopped opencode worker never exited, so the deadline kill did not reach it.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // And the ref is intact, which is what makes `preserve` meaningful for a harness that
        // cannot be handed a wind-down turn.
        Assert.Equal(refBeforeStop, await rig.HarnessSessionRefAsync(task, ct));
    }

    /// <summary>
    /// The BYO-harness pitch, one harness further: a claude worker and an OpenCode worker hand a
    /// token across a single fleet. Machine A runs <c>claude -p</c>, machine B runs
    /// <c>opencode run</c>, and B reports back a value only A could have produced — so the two
    /// harnesses collaborated through Docket without either knowing the other existed.
    /// </summary>
    [SkippableFact]
    public async Task A_claude_worker_and_an_opencode_worker_hand_off_a_token_across_one_fleet()
    {
        var openCodeBin = RequireRealOpenCode();
        var claudeBin = RequireRealClaudeForMixedFleet();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: OpenCodeWorkerSpawn(openCodeBin, WorkerPrompt), // fleet default; A overrides
            terminalEvents: true,
            eventMapping: OpenCodeEventMapping,
            // Fleet-wide, so machine A's claude worker also gets a closed stdin — harmless there
            // (claude -p abandons its stdin read after ~3s) and the same asymmetry the Codex tier
            // papers over. A real mixed fleet declares stdin per profile.
            stdin: OpenCodeStdin);
        await rig.StartAsync(ct);
        using var config = OpenCodeConfig.Create(rig.McpUrl);

        await rig.AddMachineAsync("A", ClaudeWorkerSpawn(claudeBin));  // claude machine
        await rig.AddMachineAsync("B");                                // opencode machine

        // Step A, on the claude machine: mint + report an unforgeable token.
        var token = NewToken();
        var stepA = await rig.CreateTaskAsync(EchoDescription("A", token), ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(stepA, "A", MaxAttempts, PerLegBudget, ct),
            "the real claude worker never drove step A to verifying.\n"
            + await rig.RealWorkerDiagnosticsAsync(stepA, ct));

        // The handoff: read what A actually committed to the plane, not the test's constant.
        var referenceA = await rig.ResultReferenceAsync(stepA, ct);
        Assert.Contains(token, referenceA);

        // Step B, on the opencode machine: report the token the claude worker produced.
        var stepB = await rig.CreateTaskAsync(EchoDescription("B", token), ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(stepB, "B", MaxAttempts, PerLegBudget, ct),
            "the real opencode worker never confirmed the cross-harness handoff.\n"
            + OpenCodeFailureHypotheses() + await rig.RealWorkerDiagnosticsAsync(stepB, ct));

        Assert.Contains(token, await rig.ResultReferenceAsync(stepB, ct)); // crossed the boundary
        Assert.Equal("A", rig.MachineRanOn(stepA));
        Assert.Equal("B", rig.MachineRanOn(stepB));
    }

    /// <summary>
    /// <b>The measured-telemetry substrate, anchored against the real binary.</b> Everything
    /// docketd knows about what an OpenCode task cost comes off the same stdout stream it already
    /// drains for tool calls — there is no OTLP metrics exporter in OpenCode
    /// (<c>packages/core/src/observability/otlp.ts:50-77</c> ships logs and traces only), so the
    /// <c>step_finish</c> line is the whole channel. Every other fact in this tier would pass
    /// with the usage keys mismapped; this one is why they are right.
    ///
    /// <para><b>What makes it worth a real run.</b> The shapes the parser tests use are derived
    /// from OpenCode's source, not captured — <c>StepFinishPart</c> at
    /// <c>packages/schema/src/v1/session.ts:240-256</c> and the <c>emit</c> envelope at
    /// <c>run.ts:678-691</c>. Deriving a wire format from a schema is exactly the step that
    /// silently goes wrong, and the three nested paths this harness needs
    /// (<c>part.tokens</c>, <c>cache.read</c>, <c>part.cost</c>) are the most fragile part of the
    /// whole recipe. A green run here says the reading was right.</para>
    ///
    /// <para>Queried plane-side rather than off the transcript deliberately: the row is what the
    /// §12 measured view actually reads, so asserting there covers the mapping, the wire event,
    /// the accumulation of per-step deltas, and the upsert in one go. A cost that is present and
    /// positive is the OpenCode-specific half — claude self-reports USD too, but Codex reports
    /// none at all, so "this harness states dollars" is a real distinction to pin.</para>
    ///
    /// <para><b>The wait is not belt-and-braces.</b> Per <c>ReportedUsageAsync</c>'s contract
    /// (#145) an empty result is legitimate for a moment: the task reaches <c>verifying</c> on the
    /// <c>report_result</c> call, while the usage still has to travel the ring, the wire and the
    /// plane's sink. OpenCode is less exposed to this than claude — its <c>step_finish</c> lines
    /// arrive per step rather than once at the end, so early rows exist well before the run
    /// finishes — but it is <em>more</em> exposed to reading a stale one, because docketd
    /// accumulates per-step deltas and the row is a high-water mark. Polling for a cost rather
    /// than asserting on the first row is what makes this assert the total instead of a
    /// prefix.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_real_opencode_worker_reports_its_own_tokens_and_cost_on_the_stream_docketd_reads()
    {
        var openCodeBin = RequireRealOpenCode();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: OpenCodeWorkerSpawn(openCodeBin, WorkerPrompt),
            terminalEvents: true,
            eventMapping: OpenCodeEventMapping,
            stdin: OpenCodeStdin);
        await rig.StartAsync(ct);
        using var config = OpenCodeConfig.Create(rig.McpUrl);
        await rig.AddMachineAsync("A");

        var token = NewToken();
        var task = await rig.CreateTaskAsync(EchoDescription("A", token), ct);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            "the real opencode worker never drove its task to verifying, so there is no usage to "
            + "assert on.\n" + OpenCodeFailureHypotheses()
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // Wait for the report to land AND to carry a cost, so this reads the accumulated total
        // rather than whichever step happened to be committed first.
        Assert.True(
            await FleetRig.WaitUntilAsync(
                async () => await rig.ReportedUsageAsync(task, ct) is [{ CostUsd: not null }, ..],
                TimeSpan.FromMinutes(2)),
            "no usage report with a cost ever reached the plane for this task. Either "
            + "events.mapping never resolved OpenCode's step_finish line, or the usage never "
            + "crossed the runner->plane leg.\n"
            + OpenCodeFailureHypotheses() + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var row = Assert.Single(await rig.ReportedUsageAsync(task, ct));

        // OpenCode reports no per-model breakdown, so the row names no model — and docketd does
        // not substitute one, because a model the plane asserted would not be reported BY the
        // harness. This is the honest empty, not a mapping failure.
        Assert.Null(row.Model);

        // Real tokens, not a row of zeros. Input is the one bucket every run must have: the
        // worker was handed a prompt.
        Assert.True(
            row.InputTokens > 0,
            $"OpenCode reported no input tokens, so events.mapping did not reach part.tokens.input "
            + $"(row: in={row.InputTokens} out={row.OutputTokens} cr={row.CacheReadTokens} "
            + $"cw={row.CacheWriteTokens} cost={row.CostUsd?.ToString() ?? "null"})");
        Assert.True(row.OutputTokens > 0, "OpenCode reported no output tokens.");

        // The cost half: OpenCode computes USD itself, from the models.dev catalog price
        // (session.ts:382-399), and puts it beside the counters at part.cost. A null here means
        // usage_cost_key never resolved — the single most likely mapping mistake for this harness,
        // because cost is the one figure that does NOT live inside the counters object.
        Assert.NotNull(row.CostUsd);
        Assert.True(
            row.CostUsd > 0m,
            $"OpenCode stated a cost of {row.CostUsd}, which is not a cost. Check that "
            + "usage_cost_key is the dotted path 'part.cost' and not the bare 'cost' — the bare "
            + "name resolves against the line root, where OpenCode puts no cost at all.");
    }

    // ── The OpenCode recipe (all source-derived at v1.18.17; see the class remarks) ──

    /// <summary>
    /// The <c>opencode run</c> worker argv.
    /// <list type="bullet">
    ///   <item><c>run</c> + argv prompt — the non-interactive mode, which "sends a single prompt,
    ///     streams events to stdout, and exits when the session goes idle"
    ///     (<c>run.ts:3-11</c>; the idle exit is the <c>break</c> at <c>run.ts:788-794</c>). The
    ///     prompt MUST be argv: stdin is docketd's dead-man pipe (§10).</item>
    ///   <item><c>--format json</c> — the NDJSON stream <c>events.source: terminal</c> reads
    ///     (<c>run.ts:174-179</c>, emitter at <c>:678-691</c>). Needs no companion flag, unlike
    ///     <c>claude -p</c>, which also wants <c>--verbose</c>.</item>
    ///   <item><c>--auto</c> — the <c>bypassPermissions</c> equivalent
    ///     (<c>run.ts:274</c>, <c>:800-804</c>). <b>Not optional:</b> without it a headless
    ///     worker <em>auto-rejects</em> every permission ask (<c>run.ts:806-815</c>) and fails
    ///     the task rather than hanging, which is a quieter failure than it sounds.</item>
    ///   <item><c>--model</c> — pinned, because there is no turn cap to bound a runaway and
    ///     because the stream carries no model field, so this argv is the only record of what
    ///     produced the tokens.</item>
    /// </list>
    ///
    /// <para>No git flag is needed — unlike <c>codex exec</c>, OpenCode has no
    /// <c>--skip-git-repo-check</c> and needs none: a directory with no repository resolves to a
    /// project rooted at the filesystem root rather than failing
    /// (<c>packages/core/src/project.ts:111-112</c>). That fallback is also OC-G5: every non-git
    /// scratch dir collapses into <em>one</em> project, so concurrent workers share a session
    /// store. This tier runs its tasks in sequence, and the profile never uses
    /// <c>--continue</c> — which would find another task's session — so nothing here depends on
    /// the isolation that a per-task <c>OPENCODE_CONFIG_DIR</c> would provide.</para>
    ///
    /// <para>No MCP flag appears here either, and that absence is the same finding Codex
    /// produced: OpenCode has no <c>--mcp-config</c>, so the docket server is wired through
    /// <see cref="OpenCodeConfig"/> and docketd's generated <c>{mcp_config}</c> goes unread.</para>
    /// </summary>
    private static string[] OpenCodeWorkerSpawn(string openCodeBin, string prompt) =>
    [
        openCodeBin, "run", prompt,
        "--format", "json",
        "--auto",
        "--model", OpenCodeModel,
    ];

    /// <summary>
    /// The model this tier pins. <c>provider/model</c> is the required spelling
    /// (<c>run.ts:31-38</c> splits on the first <c>/</c>), and an Anthropic model is chosen so the
    /// job reuses the <c>ANTHROPIC_KEY</c> secret the claude tier already has rather than adding
    /// a fourth credential.
    ///
    /// <para>Pinned rather than left to the machine's default for the reason the Codex tier
    /// learned the hard way: a default is whatever the operator or a server-side catalog says,
    /// which for a token-spending CI job is an open cheque. And pinned <em>overridably</em> for
    /// the same reason — OpenCode resolves models through a fetched catalog
    /// (<c>OPENCODE_MODELS_URL</c>, <c>flag.ts:45</c>), so a slug can be retired out from under
    /// this constant. <c>DOCKET_OPENCODE_MODEL</c> is the escape hatch, and it exists because
    /// exactly this happened to the Codex tier on its first real dispatch.</para>
    /// </summary>
    private static string OpenCodeModel =>
        Environment.GetEnvironmentVariable("DOCKET_OPENCODE_MODEL") is { Length: > 0 } m
            ? m
            : "anthropic/claude-haiku-4-5-20251001";

    /// <summary>
    /// The <c>stdin</c> policy every end-to-end fact here declares (§10, #110) — the one line
    /// that makes an OpenCode worker possible at all, for the reason
    /// <see cref="A_cold_opencode_worker_hangs_on_docketds_dead_man_stdin_and_never_takes_a_turn"/>
    /// characterizes against the real binary.
    ///
    /// <para>The trade is the same as Codex's and costs the same nothing: OpenCode never reaches
    /// a read that would observe EOF-as-death, so the dead-man switch it gives up is one it could
    /// never have honoured. What is actually lost is docketd's ability to end a worker by dying;
    /// the <c>StrayReaper</c>'s next-start sweep is the backstop.</para>
    /// </summary>
    private const StdinPolicy OpenCodeStdin = StdinPolicy.Closed;

    /// <summary>
    /// The <c>events.mapping</c> the worked profile in <c>runner-config.md</c> declares, and the
    /// article <c>Docket.Runner.Tests/OpenCodeStreamMappingTests</c> exercises key-for-key.
    ///
    /// <para><b>Session ref.</b> OpenCode emits no init line, but <c>sessionID</c> is top-level on
    /// <em>every</em> line (<c>run.ts:682-685</c>), so the Codex trick applies: point the
    /// sub-discriminator back at <c>type</c>, match it against the same value, and let the id key
    /// work. <c>step_start</c> is the type chosen because it is the earliest line emitted.</para>
    ///
    /// <para><b>Usage.</b> Every usage key here is a dotted path, which is what OpenCode forced
    /// (#142): the counters are two levels down at <c>part.tokens</c>, the cache buckets one
    /// deeper still, and cost sits beside the counters at <c>part.cost</c> rather than at the line
    /// root. Note <c>usage_type</c> must differ from <c>system_type</c> — the reader returns early
    /// on a session-init line — which is why the ref rides <c>step_start</c> and usage rides
    /// <c>step_finish</c>. <c>RunnerConfig</c> now refuses the collision rather than letting it
    /// swallow the usage silently.</para>
    ///
    /// <para><b>The two semantic booleans, and why they disagree.</b> OpenCode subtracts cache out
    /// of input (<c>session.ts:366</c>), so its buckets are already disjoint like claude's and
    /// <c>usage_cached_is_subset</c> stays false. But it <em>also</em> subtracts reasoning out of
    /// output (<c>session.ts:373</c>), unlike either other harness — so
    /// <c>usage_reasoning_is_subset</c> is false and the reader folds it back, without which the
    /// plane's total would silently omit every reasoning token.</para>
    /// </summary>
    private static readonly Dictionary<string, string> OpenCodeEventMapping = new()
    {
        ["system_type"] = "step_start",
        ["subtype_key"] = "type",
        ["init_subtype"] = "step_start",
        ["session_id_key"] = "sessionID",
        ["tool_event_type"] = "tool_use",
        ["tool_name_path"] = "part.tool",
        ["usage_type"] = "step_finish",
        ["usage_key"] = "part.tokens",
        ["usage_input_key"] = "input",
        ["usage_output_key"] = "output",
        ["usage_cache_read_key"] = "cache.read",
        ["usage_cache_write_key"] = "cache.write",
        ["usage_reasoning_key"] = "reasoning",
        ["usage_cost_key"] = "part.cost",
        ["usage_models_key"] = "",
        ["usage_cached_is_subset"] = "false",
        ["usage_reasoning_is_subset"] = "false",
        ["usage_is_cumulative"] = "false",
    };

    /// <summary>
    /// A throwaway config file holding the one thing that wires an OpenCode worker to this
    /// fleet's plane — OpenCode's answer to claude's <c>--mcp-config</c>, which it does not have.
    /// Published through <c>OPENCODE_CONFIG</c>, a documented path variable
    /// (<c>flag.ts:21</c>, honoured at <c>config/config.ts:401-403</c>).
    ///
    /// <para><b>The per-instance-auth trick, and why one static file is enough.</b> Docket mints a
    /// fresh worker token per dispatch and docketd injects it as <c>DOCKET_WORKER_TOKEN</c> in the
    /// spawn environment. OpenCode applies <c>{env:VAR}</c> substitution to the raw config
    /// <em>text</em> before parsing it, reading <c>process.env</c> at load time
    /// (<c>packages/opencode/src/config/variable.ts:33-38</c>, called from
    /// <c>config/config.ts:220</c>) — so this file is correct for every dispatch and the token
    /// never touches disk. Strictly better than the 0600 JSON docketd writes for claude, and the
    /// same win Codex's <c>bearer_token_env_var</c> gives, through a generic mechanism rather than
    /// a special-cased field.</para>
    ///
    /// <para><b><c>"oauth": false</c> is load-bearing</b>, not defensive: without it OAuth
    /// auto-detection can take over a server that was meant to authenticate by bearer header. The
    /// documented recipe pairs the two exactly this way (<c>opencode.ai/docs/mcp-servers</c>, "API
    /// key authentication").</para>
    ///
    /// <para><b>OC-G6, deliberately not worked around.</b> An unset variable substitutes to the
    /// empty string rather than erroring (<c>variable.ts:37</c>, <c>|| ""</c>), and the remote
    /// server schema has no <c>required</c> flag the way Codex's does — so a broken wiring yields
    /// <c>Authorization: Bearer </c>, a plane 401, and an agent that runs happily with no docket
    /// tools. Nothing config-side prevents it; this tier failing loudly is the substitute.</para>
    ///
    /// <para><b>Known production gap (OC-G4), also not worked around.</b> One config file is
    /// shared by every worker this test process spawns, because docketd has no per-profile
    /// environment seam — a test can set <c>OPENCODE_CONFIG</c> process-wide, a real operator
    /// cannot set it per task. Fine for a rig that runs tasks in sequence; #112's
    /// <c>profiles[].env</c> is the real fix, and with it this file would not need to exist at all
    /// (<c>OPENCODE_CONFIG_CONTENT</c> carries a whole config inline, <c>flag.ts:22</c>).</para>
    ///
    /// <para><b>Tools are deliberately not gated here.</b> OpenCode's allow-list is a config map
    /// with wildcards rather than a CLI flag, and narrowing it to the docket tools alone would
    /// also have to enumerate the built-ins the agent needs — a strictness this tier cannot
    /// validate locally and does not need. The prompt is what constrains the worker; the
    /// <c>tools</c> map is documented in <c>runner-config.md</c> for operators who want the
    /// strict archetype.</para>
    /// </summary>
    private sealed class OpenCodeConfig : IDisposable
    {
        private readonly string _dir;
        private readonly string? _previous;

        private OpenCodeConfig(string dir, string? previous)
        {
            _dir = dir;
            _previous = previous;
        }

        public static OpenCodeConfig Create(string mcpUrl)
        {
            var previous = Environment.GetEnvironmentVariable("OPENCODE_CONFIG");
            var dir = Path.Combine(Path.GetTempPath(), "docket-opencode-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "opencode.json");

            File.WriteAllText(
                file,
                $$"""
                  {
                    "$schema": "https://opencode.ai/config.json",
                    "mcp": {
                      "docket": {
                        "type": "remote",
                        "url": "{{mcpUrl}}",
                        "enabled": true,
                        "headers": { "Authorization": "Bearer {env:DOCKET_WORKER_TOKEN}" },
                        "oauth": false,
                        "timeout": 120000
                      }
                    }
                  }
                  """);

            Environment.SetEnvironmentVariable("OPENCODE_CONFIG", file);
            return new OpenCodeConfig(dir, previous);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("OPENCODE_CONFIG", _previous);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The one description template every role uses (§7) — identical to the other two
    /// tiers', on purpose: a cross-harness comparison is only meaningful if both harnesses are
    /// given the same words.</summary>
    private static string EchoDescription(string label, string token) =>
        $"""
         Call report_result exactly once, with its resultReference set to this exact string
         (no quotes, no other text):

         {label}:{token}
         """;

    /// <summary>
    /// A prompt that keeps the worker busy long enough to be stopped mid-flight. Deliberately
    /// bounded work rather than an infinite loop: if the stop never arrives the worker finishes
    /// and the fact fails on its assertions rather than hanging until the outer deadline.
    /// </summary>
    private const string SlowWorkerPrompt =
        "You are a Docket worker agent. First call the docket get_task tool to read your "
        + "assignment. Then, before reporting anything, count slowly from 1 to 400, writing each "
        + "number on its own line with a short remark about it. Only after finishing the count "
        + "may you call the docket report_result tool with the exact string from the description.";

    /// <summary>The claude argv for the mixed-fleet fact's machine A. Kept minimal and
    /// independent of the claude tier's own recipe so this file has no cross-tier
    /// dependency.</summary>
    private static string[] ClaudeWorkerSpawn(string claudeBin) =>
    [
        claudeBin, "-p", WorkerPrompt,
        "--mcp-config", "{mcp_config}",
        "--output-format", "stream-json",
        "--verbose",
        "--permission-mode", "bypassPermissions",
        "--max-turns", "6",
    ];

    /// <summary>
    /// Skip unless this run is a real, opted-in OpenCode run: Postgres up, an explicit opt-in, and
    /// the <c>opencode</c> CLI resolvable. Returns the resolved binary when everything is in
    /// place; otherwise throws the xUnit skip, so an ordinary push spends nothing.
    ///
    /// <para>Auth is the simplest of the three harnesses: OpenCode resolves a provider key by
    /// mapping its catalog's per-provider <c>env</c> list over the process environment
    /// (<c>packages/opencode/src/provider/provider.ts:1527-1531</c>, tagged
    /// <c>source: "env"</c>), so <c>ANTHROPIC_API_KEY</c> in the environment is sufficient and
    /// nothing needs publishing under a harness-specific name — unlike Codex, whose
    /// <c>exec</c> path reads <c>CODEX_API_KEY</c> and not <c>OPENAI_API_KEY</c>.
    /// <c>DOCKET_REAL_OPENCODE=1</c> is the path for a machine whose CLI is already logged in
    /// (stored credentials live in <c>auth.json</c> under the global data dir,
    /// <c>src/auth/index.ts:10</c>).</para>
    /// </summary>
    private string RequireRealOpenCode()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var key = FirstNonEmpty("ANTHROPIC_API_KEY", "ANTHROPIC_KEY");
        var optedIn = Environment.GetEnvironmentVariable("DOCKET_REAL_OPENCODE") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(string.IsNullOrWhiteSpace(key) && !optedIn,
            "no ANTHROPIC_API_KEY/ANTHROPIC_KEY and no DOCKET_REAL_OPENCODE — the real "
            + "opencode run E2E is opt-in (see the gated CI job)");
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key);

        var bin = ResolveBin("opencode", "DOCKET_OPENCODE_BIN");
        Skip.If(bin is null, "opencode CLI not found (set DOCKET_OPENCODE_BIN or put opencode on PATH)");
        return bin!;
    }

    /// <summary>The mixed-fleet fact needs a real claude too; skip rather than half-test.</summary>
    private static string RequireRealClaudeForMixedFleet()
    {
        var claudeBin = ResolveBin("claude", "DOCKET_CLAUDE_BIN");
        Skip.If(claudeBin is null,
            "claude CLI not found — the mixed-harness fleet fact needs BOTH CLIs "
            + "(set DOCKET_CLAUDE_BIN or put claude on PATH)");
        return claudeBin!;
    }

    private static string? FirstNonEmpty(params string[] names)
    {
        foreach (var name in names)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

    /// <summary>Resolve a CLI: an explicit override variable, then PATH, then the common install
    /// locations — or null when none exists.</summary>
    private static string? ResolveBin(string name, string overrideVar)
    {
        var explicitBin = Environment.GetEnvironmentVariable(overrideVar);
        if (!string.IsNullOrWhiteSpace(explicitBin) && File.Exists(explicitBin))
            return explicitBin;

        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var fallback in new[] { $"/usr/local/bin/{name}", $"/opt/homebrew/bin/{name}" })
            if (File.Exists(fallback)) return fallback;

        return null;
    }

    /// <summary>
    /// What to suspect first when a fact in this tier goes red, ordered by how often each is
    /// actually the cause for a <em>new</em> harness. The Codex tier earned its equivalent the
    /// hard way — a retired model slug, not a Docket bug — so this exists before the first red
    /// run rather than after it.
    /// </summary>
    private static string OpenCodeFailureHypotheses() =>
        $$"""

         Suspect, in order:
           1. MODEL SLUG. This tier pins '{{OpenCodeModel}}'. OpenCode resolves models through a
              fetched catalog, so a slug can be retired server-side; the symptom is a worker that
              starts, authenticates, and fails before its first tool call. Override with
              DOCKET_OPENCODE_MODEL — it is a dispatch input, not a PR.
           2. MCP WIRING. The bearer arrives via {env:DOCKET_WORKER_TOKEN} substituted into the
              config file's Authorization header. An unset variable substitutes to the EMPTY
              STRING rather than failing (variable.ts:37), so the give-away is a plane 401 and an
              agent that ran with no docket tools and reported nothing. Check that "oauth": false
              is present too — without it OAuth auto-detection can displace the header.
           3. TOOL NAMES. OpenCode spells MCP tools <server>_<tool>, so the worker is looking for
              docket_get_task, NOT mcp__docket__get_task (mcp/catalog.ts:119). A prompt that
              names the qualified form will have the agent hunting a tool that does not exist.
           4. PERMISSIONS. Without --auto a headless worker AUTO-REJECTS every permission ask
              (run.ts:806-815) and fails rather than hangs.
           5. AUTH. ANTHROPIC_API_KEY must be in the environment, or the machine's opencode must
              already be logged in.

         """;

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];
}
