using Docket.Contracts;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// The §10 BYO-harness promise against a <b>second real harness</b>: OpenAI's Codex CLI
/// (<c>codex exec</c>) driving Docket tasks on the same real plane + relay + docketd rigs
/// the <see cref="RealClaudeCollaborationTests"/> tier uses, with no change below the spawn
/// seam. If Docket's "everything harness-specific is data" claim is true, a Codex worker is
/// a profile, not a code change — and this tier is where that stops being an assertion in a
/// design doc.
///
/// <para><b>Opt-in and token-spending</b>, exactly like the claude tier: the facts SKIP
/// unless the run opted in — an OpenAI key in the environment (<c>CODEX_API_KEY</c>, or
/// <c>OPENAI_API_KEY</c>/<c>OPENAI_KEY</c> which the gate maps to it), or
/// <c>DOCKET_REAL_CODEX=1</c> on a machine whose CLI is already logged in — AND the
/// <c>codex</c> CLI resolves. A normal push/PR run does neither and spends nothing. The
/// dedicated CI job (<c>.github/workflows/ci.yml</c>, <c>real-codex-e2e</c>) sets the key
/// and runs only this trait.</para>
///
/// <para><b>Nothing here has ever been executed.</b> <c>codex</c> is not installed on the
/// machine where this tier was written and will not be. Every Codex claim below was instead
/// verified by reading the CLI's own source at tag <c>rust-v0.147.0</c> — the version
/// <c>npm install -g @openai/codex</c> resolves to, and the one the CI job installs — with
/// file:line citations. Source reading settles far more than the docs did, including the one
/// thing that decides whether this tier can pass at all:</para>
///
/// <para><b>CONFIRMED BLOCKER — <c>codex exec</c> hangs on docketd's dead-man stdin.</b>
/// docketd redirects stdin on every spawn and holds the write end open for the worker's whole
/// life; that pipe <em>is</em> the §10 dead-man's switch. Codex's cold-start path is
/// <c>resolve_root_prompt</c> (<c>codex-rs/exec/src/lib.rs:1961</c>), which — even when a
/// prompt was supplied as argv — calls <c>read_prompt_from_stdin(OptionalAppend)</c>. That
/// function (<c>lib.rs:1888</c>) returns early <em>only</em> when
/// <c>std::io::stdin().is_terminal()</c>; a pipe is not a terminal, so it falls through to
/// <c>std::io::stdin().read_to_end(&amp;mut bytes)</c> at <c>lib.rs:1909</c> and blocks until
/// EOF. docketd never sends EOF, so the worker never starts a turn. <c>claude -p</c> survives
/// the same pipe only because it gives up after ~3s.</para>
///
/// <para>There is no argv-only escape: no flag suppresses the append-read, and
/// <c>codex exec -</c> forces stdin as the prompt, which blocks identically. So
/// <b>a Codex worker cannot run under docketd today</b> — it needs the per-profile stdin
/// policy the gaps report proposes. <see cref="A_cold_codex_worker_hangs_on_docketds_dead_man_stdin_and_never_takes_a_turn"/>
/// pins that against the real binary and costs nothing, because Codex never reaches the model.
/// The three end-to-end facts are written, gated off behind
/// <c>DOCKET_CODEX_STDIN_FIXED=1</c>, and become live the moment that gap closes — they are
/// not deleted, because everything in them below the stdin seam is expected to work.</para>
///
/// <para><b>The resume path is exempt</b>, which is a genuinely useful asymmetry:
/// <c>codex exec resume &lt;id&gt; "&lt;prompt&gt;"</c> resolves through <c>resolve_prompt</c>
/// (<c>lib.rs:1944</c>), whose first arm returns a non-<c>-</c> argv prompt immediately and
/// never touches stdin. So resume-with-argv-prompt would work under docketd unchanged.</para>
///
/// <para><b>MCP reachability — confirmed workable.</b> Codex has no
/// <c>--mcp-config &lt;file&gt;</c>; its only client surface is a <c>config.toml</c> under
/// <c>CODEX_HOME</c>, so the injected <c>{mcp_config}</c> is unusable and
/// <see cref="CodexHome"/> writes a TOML table instead. The per-instance bearer rides
/// <c>bearer_token_env_var = "DOCKET_WORKER_TOKEN"</c>, and
/// <c>resolve_bearer_token</c> (<c>codex-rs/codex-mcp/src/rmcp_client.rs:822</c>) reads that
/// variable from the <em>live process environment</em> at connect time — which is exactly
/// where docketd injects the fresh per-spawn token. One static file is therefore correct for
/// every dispatch, and the token never touches disk.</para>
///
/// <para><b>No turn cap.</b> The claude recipe bounds cost with <c>--max-turns</c>; the Codex
/// CLI has no equivalent for <c>exec</c>. Cost is bounded here only by the pinned mini model,
/// trivial tasks, the per-leg budget, and the outer deadline.</para>
/// </summary>
[Trait("Category", RealCodex)]
[Collection(PostgresCollection.Name)]
public sealed class RealCodexCollaborationTests(PostgresFixture pg) : IAsyncLifetime
{
    /// <summary>Trait value the opt-in CI job filters on so it runs <em>only</em> this tier.</summary>
    public const string RealCodex = "RealCodex";

    /// <summary>Bounded redispatch, same rationale as the claude tier: a worker that succeeds
    /// does so on the first try; these absorb the occasional turn that ends without the tool
    /// call without letting the job run away.</summary>
    private const int MaxAttempts = 3;
    private static readonly TimeSpan PerLegBudget = TimeSpan.FromMinutes(8);

    /// <summary>
    /// The docket tools a Codex worker may call. Codex's per-server allow-list is a config
    /// key rather than a CLI flag — <c>enabled_tools</c> (optional): Tool allow list, per
    /// <c>developers.openai.com/codex/mcp.md</c> — and takes <b>bare</b> tool names, so this
    /// is not the <c>mcp__docket__*</c> spelling the claude tier's <c>--allowedTools</c> uses.
    /// The names the <em>model</em> sees are still <c>mcp__docket__&lt;tool&gt;</c>, which is
    /// the one piece of Codex's MCP surface that matches Claude Code exactly.
    /// </summary>
    private static readonly string[] AllowedDocketTools = ["get_task", "report_result"];

    /// <summary>Generic worker prompt (§7), deliberately the same shape as the claude tier's:
    /// the specifics live in the task's opaque description, read via <c>get_task</c>.</summary>
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
    /// The finding, asserted against the real binary: a cold <c>codex exec</c> worker spawned
    /// by docketd <b>never takes a turn</b>, because it blocks reading the dead-man stdin pipe
    /// that docketd holds open for the task's whole life (see the class remarks for the source
    /// trace, <c>exec/src/lib.rs:1961 → 1888 → 1909</c>).
    ///
    /// <para>This is the same discipline as the claude tier's stop characterization: pin the
    /// observed behaviour rather than the hoped-for one, because the gap between them <em>is</em>
    /// the finding. And it is the cheap fact in this tier — Codex hangs before it ever contacts
    /// a model, so this spends no tokens at all.</para>
    ///
    /// <para>The assertions are deliberately about absence, and each rules out a different
    /// innocent explanation: the process really did start (so this is not a spawn failure); it
    /// produced no session ref within a window many times longer than a Codex startup (so it is
    /// not merely slow); and the task never reached <see cref="TaskState.Verifying"/>. Together
    /// that is "the worker is alive and idle, holding a turn it will never begin".</para>
    ///
    /// <para><b>When docketd grows a per-profile stdin policy this fact should FAIL</b>, and
    /// that failure is the signal to delete it and drop <c>DOCKET_CODEX_STDIN_FIXED</c> from
    /// the three facts below. It is written to be falsified.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_cold_codex_worker_hangs_on_docketds_dead_man_stdin_and_never_takes_a_turn()
    {
        var codexBin = RequireRealCodex();
        Skip.If(StdinFixDeclared,
            "DOCKET_CODEX_STDIN_FIXED is set, so this characterization no longer applies — "
            + "the end-to-end facts cover the fixed behaviour instead");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: CodexWorkerSpawn(codexBin, WorkerPrompt),
            terminalEvents: true,
            eventMapping: CodexEventMapping);
        await rig.StartAsync(ct);
        using var home = CodexHome.Create(rig.McpUrl, AllowedDocketTools);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(EchoDescription("A", NewToken()), ct);
        await rig.DispatchToAsync("A", ct);

        // It really launched: docketd observed a start. Without this the rest would also be
        // true of a binary that does not exist.
        Assert.True(
            await FleetRig.WaitUntilAsync(
                () => Task.FromResult(rig.WorkerObserved(task) is { Starts: > 0 }),
                TimeSpan.FromMinutes(1)),
            "the codex worker never even started, so this run says nothing about stdin.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // And then nothing happens. A Codex startup that is going to emit `thread.started`
        // does so in seconds; two minutes of silence is the hang, not slowness.
        Assert.False(
            await FleetRig.WaitUntilAsync(
                async () => await rig.HarnessSessionRefAsync(task, ct) is { Length: > 0 },
                TimeSpan.FromMinutes(2)),
            "a cold codex worker DID report a session ref — the dead-man-stdin blocker this "
            + "tier is built around no longer reproduces. Delete this fact and drop the "
            + "DOCKET_CODEX_STDIN_FIXED gate from the end-to-end facts.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        Assert.NotEqual(TaskState.Verifying, await rig.StateAsync(task, ct));
    }

    /// <summary>
    /// The minimum bar, and the fact that decides whether the rest of this tier means
    /// anything: a REAL <c>codex exec</c> worker, spawned by a real docketd on a two-machine
    /// fleet, reads its assignment off the wire and drives its task to
    /// <see cref="TaskState.Verifying"/>, reporting back the exact unforgeable token its live
    /// description carried. Only a worker that really connected to the plane over Docket's
    /// HTTP MCP — authenticated with the per-instance bearer it resolved from
    /// <c>DOCKET_WORKER_TOKEN</c> — called <c>get_task</c>, and read that description could
    /// produce it.
    ///
    /// <para>It also asserts the §11 session ref landed, because the same run is the first
    /// real-stream confirmation of what
    /// <c>Docket.Runner.Tests/CodexStreamMappingTests</c> establishes against the documented
    /// shapes: Codex's <c>thread.started</c> line reaches the plane as a session ref through
    /// <c>events.mapping</c> alone.</para>
    /// </summary>
    [SkippableFact]
    public async Task Real_codex_worker_drives_a_task_to_verifying_on_the_fleet()
    {
        var codexBin = RequireRealCodex();
        RequireStdinFix();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: CodexWorkerSpawn(codexBin, WorkerPrompt),
            terminalEvents: true,
            eventMapping: CodexEventMapping);
        await rig.StartAsync(ct);
        using var home = CodexHome.Create(rig.McpUrl, AllowedDocketTools);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B"); // a real fleet: >1 machine enrolled, dispatch steered to A

        var token = NewToken();
        var task = await rig.CreateTaskAsync(EchoDescription("A", token), ct);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            "the real codex worker never drove its task to verifying.\n"
            + StdinHypothesis(rig, task) + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var reference = await rig.ResultReferenceAsync(task, ct);
        Assert.Contains(token, reference); // the live-description token round-tripped through the real agent
        Assert.Equal("A", rig.MachineRanOn(task));

        // The §11 ref, off the real stream this time rather than the documented fixture.
        Assert.False(
            string.IsNullOrWhiteSpace(await rig.HarnessSessionRefAsync(task, ct)),
            "no harness session ref was stamped, so events.mapping did not carry Codex's "
            + "thread.started/thread_id — §11 resume would silently cold-start.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));
    }

    /// <summary>
    /// What a graceful <c>stop</c> actually is against a real <c>codex exec</c> worker, pinned
    /// the way #103's honesty framework requires: the profile declares
    /// <see cref="StopMode.Signal"/> because that is the <em>true</em> claim about this
    /// harness, and the assertions are that docketd armed a deadline and the worker died on
    /// it with its transcript ref intact.
    ///
    /// <para><b>Why <c>signal</c> and not <c>message</c>.</b> <c>mode: message</c> is a
    /// declaration that a running session reads turns off its stdin, and docketd cannot check
    /// it — so declaring it for a harness that does not read stdin buys nothing and makes
    /// <c>preserve</c> a promise the machine will break. <c>codex exec</c> takes its prompt as
    /// an argv positional and its docs describe no mid-run stdin read; there is therefore no
    /// documented seam for a wind-down turn, and <c>signal</c> is the honest declaration. This
    /// mirrors the claude tier's conclusion, reached the same way but from docs rather than a
    /// spike — the claude tier proves the <em>shape</em> of the finding, this fact records that
    /// Codex lands in the same place.</para>
    ///
    /// <para>So what makes <c>preserve</c> mean anything is the plane's record, not the
    /// agent's cooperation: the <c>thread_id</c> outlives the kill, and it is exactly the id
    /// <c>codex exec resume &lt;SESSION_ID&gt;</c> takes — so the transcript stays resumable
    /// even though nothing was negotiated with the agent.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_stop_reaches_a_real_codex_worker_as_a_kill_deadline_with_its_thread_ref_preserved()
    {
        var codexBin = RequireRealCodex();
        RequireStdinFix();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: CodexWorkerSpawn(codexBin, WorkerPrompt),
            terminalEvents: true,
            stop: new StopConfig(
                StopMode.Signal, Signal: null, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(5)),
            eventMapping: CodexEventMapping);
        await rig.StartAsync(ct);
        using var home = CodexHome.Create(rig.McpUrl, AllowedDocketTools);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(EchoDescription("A", NewToken()), ct);

        // Wait until the worker is demonstrably mid-turn — it reported a thread ref, so its
        // harness is up and streaming — before stopping it. Stopping earlier would race the spawn.
        Assert.True(
            await rig.DispatchUntilAsync(
                task, "A", async () => await rig.HarnessSessionRefAsync(task, ct) is { Length: > 0 },
                MaxAttempts, PerLegBudget, ct),
            "the real codex worker never reported a thread ref, so it was never observably working.\n"
            + StdinHypothesis(rig, task) + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var sessionRef = await rig.HarnessSessionRefAsync(task, ct);
        Assert.True(
            await rig.SendStopAsync(
                "A", task, TimeSpan.FromMinutes(1), StopDisposition.PreserveAndPark,
                "characterizing real-codex stop delivery", ct),
            "the stop was not delivered to the machine holding the task");

        // A signal-mode stop writes no turn: docketd reports the deadline it armed, which is
        // the only thing it actually did.
        var ack = rig.StopAckFor(task);
        Assert.NotNull(ack);
        Assert.True(ack!.Value.Actioned);
        Assert.Equal(StopDelivery.DeadlineArmed, ack.Value.Delivery);

        Assert.True(
            await FleetRig.WaitUntilAsync(
                () => Task.FromResult(rig.WorkerObserved(task) is { Exits: > 0 }),
                TimeSpan.FromMinutes(3)),
            "the worker never ended, so the deadline did not fire either.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // Preservation is the plane's record: the ref outlives the kill, so the Codex thread
        // stays resumable.
        Assert.Equal(sessionRef, await rig.HarnessSessionRefAsync(task, ct));
    }

    /// <summary>
    /// Docket's whole pitch, as one fact: <b>a fleet of mixed harnesses</b>. Machine A runs a
    /// real <c>claude -p</c> worker, machine B runs a real <c>codex exec</c> worker, under one
    /// control plane, and B reports a token that only A could have produced — read off the
    /// plane, not out of the test's own constant. Two vendors' CLIs, one task graph, and
    /// nothing between them but Docket.
    ///
    /// <para>The per-machine spawn override is what makes this expressible: the fleet's
    /// profile is one shape, but each machine's <c>default</c> profile spawns its own harness
    /// (see <see cref="FleetRig.AddMachineAsync"/>). Everything else — dispatch, the worker
    /// token, <c>get_task</c>, <c>report_result</c>, the result reference — is identical for
    /// both, which is the point being made.</para>
    ///
    /// <para>Needs BOTH CLIs, so it skips when either is missing rather than half-testing. The
    /// shared <c>events.mapping</c> is Codex's: it is additive for claude on the one key that
    /// matters here (claude's <c>system</c>/<c>init</c> line is not what this mapping matches),
    /// so A contributes no session ref under it — which is why this fact asserts on results and
    /// machines, never on A's ref.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_claude_worker_and_a_codex_worker_hand_off_a_token_across_one_fleet()
    {
        var codexBin = RequireRealCodex();
        RequireStdinFix();
        var claudeBin = RequireRealClaudeForMixedFleet();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: CodexWorkerSpawn(codexBin, WorkerPrompt), // the fleet default; A overrides it
            terminalEvents: true,
            eventMapping: CodexEventMapping);
        await rig.StartAsync(ct);
        using var home = CodexHome.Create(rig.McpUrl, AllowedDocketTools);

        await rig.AddMachineAsync("A", ClaudeWorkerSpawn(claudeBin));  // claude machine
        await rig.AddMachineAsync("B");                                // codex machine

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

        // Step B, on the codex machine: report the token the claude worker produced.
        var stepB = await rig.CreateTaskAsync(EchoDescription("B", token), ct);
        Assert.True(
            await rig.DispatchUntilVerifyingAsync(stepB, "B", MaxAttempts, PerLegBudget, ct),
            "the real codex worker never confirmed the cross-harness handoff.\n"
            + StdinHypothesis(rig, stepB) + await rig.RealWorkerDiagnosticsAsync(stepB, ct));

        Assert.Contains(token, await rig.ResultReferenceAsync(stepB, ct)); // crossed the harness boundary
        Assert.Equal("A", rig.MachineRanOn(stepA));
        Assert.Equal("B", rig.MachineRanOn(stepB));
    }

    // ── The Codex recipe (all doc-derived; see the class remarks) ───────────────

    /// <summary>
    /// The <c>codex exec</c> worker argv. Every flag is quoted from
    /// <c>developers.openai.com/codex/noninteractive.md</c> and the CLI reference at
    /// <c>developers.openai.com/codex/cli/reference</c>; none has been run here.
    ///
    /// <list type="bullet">
    ///   <item><c>exec</c> + argv prompt — "Pass a task prompt as a single argument". The
    ///     prompt MUST be argv, not stdin: stdin is docketd's dead-man pipe (§10).</item>
    ///   <item><c>--json</c> — "Print newline-delimited JSON events instead of formatted
    ///     text", the stream <c>events.source: terminal</c> reads. Codex's analogue of
    ///     claude's <c>--output-format stream-json --verbose</c> pair, and like it, dropping
    ///     it silently costs the session ref and every progress line.</item>
    ///   <item><c>--skip-git-repo-check</c> — "Allow running outside a Git repository";
    ///     required because docketd spawns in <c>{work_root}/{task_id}</c>, which is scratch
    ///     and not a repo.</item>
    ///   <item><c>--dangerously-bypass-approvals-and-sandbox</c> — the headless equivalent of
    ///     claude's <c>--permission-mode bypassPermissions</c>. Needed for two independent
    ///     reasons: <c>codex exec</c> "runs in a read-only sandbox" by default, which cannot
    ///     do work; and "By default, the agent runs with network access turned off", which a
    ///     worker that must reach the plane and forwarded services cannot live with. The
    ///     narrower alternative is <c>--sandbox workspace-write</c> plus
    ///     <c>-c sandbox_workspace_write.network_access=true</c>.</item>
    /// </list>
    ///
    /// <para>No MCP flag appears here, and that absence is the finding: Codex has no
    /// <c>--mcp-config</c>, so the docket server is wired through <see cref="CodexHome"/>
    /// instead and docketd's generated <c>{mcp_config}</c> goes unread.</para>
    ///
    /// <para><c>DOCKET_CODEX_MODEL</c> optionally supplies <c>--model</c>. Deliberately unset
    /// by default: a wrong model id fails every run, and the docs give no stable "cheap model"
    /// name to hard-code, so the machine's configured default is the safer choice.</para>
    /// </summary>
    private static string[] CodexWorkerSpawn(string codexBin, string prompt, params string[] extra)
    {
        var argv = new List<string>
        {
            codexBin, "exec", prompt,
            "--json",
            "--skip-git-repo-check",
            "--dangerously-bypass-approvals-and-sandbox",
        };
        argv.Add("--model");
        argv.Add(CodexModel);
        argv.AddRange(extra);
        return [.. argv];
    }

    /// <summary>
    /// The model every fact in this tier pins, and it is pinned rather than left to the
    /// machine's default deliberately: the default is whatever the operator or the server-side
    /// catalog says, which for a token-spending CI job is an open cheque.
    ///
    /// <para><c>gpt-5.1-codex-mini</c> is the cheapest codex-family model the pinned CLI knows
    /// about — the source describes it as "Optimized for codex. Cheaper, faster, but less
    /// capable." (<c>codex-rs/tui/src/model_migration.rs:520-525</c>), where it is also the
    /// <em>migration target</em> for the retired <c>gpt-5-codex-mini</c>, so it is the current
    /// slug and not a legacy alias. Note the model catalog is fetched server-side rather than
    /// hard-coded in the CLI, so a slug can be retired out from under this constant —
    /// <c>DOCKET_CODEX_MODEL</c> overrides it without a code change when that happens.</para>
    /// </summary>
    private static string CodexModel =>
        Environment.GetEnvironmentVariable("DOCKET_CODEX_MODEL") is { Length: > 0 } m
            ? m
            : "gpt-5.1-codex-mini";

    /// <summary>
    /// The <c>events.mapping</c> that lets the terminal reader find Codex's session ref, and
    /// the only reason §11 works for this harness. Codex emits
    /// <c>{"type":"thread.started","thread_id":"…"}</c> — no <c>subtype</c> property at all —
    /// so the sub-discriminator is pointed back at <c>type</c> and matched against the same
    /// value the outer check already matched, leaving <c>session_id_key</c> to do the real
    /// work. Established against Codex's documented stream by
    /// <c>Docket.Runner.Tests/CodexStreamMappingTests</c>, which also pins what this mapping
    /// canNOT recover: <c>tool-call</c> events, because Codex nests one call per event object
    /// where the reader requires an array of content blocks.
    /// </summary>
    private static readonly Dictionary<string, string> CodexEventMapping = new()
    {
        ["system_type"] = "thread.started",
        ["subtype_key"] = "type",
        ["init_subtype"] = "thread.started",
        ["session_id_key"] = "thread_id",
    };

    /// <summary>
    /// A throwaway <c>CODEX_HOME</c> holding the one file that wires a Codex worker to this
    /// fleet's plane — Codex's answer to claude's <c>--mcp-config</c>, which it does not have.
    ///
    /// <para><b>The per-instance-auth trick, and why one static file is enough.</b> Docket
    /// mints a fresh worker token per dispatch and docketd injects it as
    /// <c>DOCKET_WORKER_TOKEN</c> in the spawn environment. Codex's
    /// <c>bearer_token_env_var</c> is documented as an environment variable <em>name</em>
    /// whose value is sent as <c>Authorization: Bearer &lt;token&gt;</c>, so naming
    /// <c>DOCKET_WORKER_TOKEN</c> here resolves to whatever that spawn's token is. The file
    /// never needs regenerating per task, and the token never touches disk — which is
    /// strictly better than the JSON config docketd writes for claude.</para>
    ///
    /// <para><c>required = true</c> is deliberate: per the MCP docs, a required server that
    /// fails to initialize makes <c>codex exec</c> exit with an error instead of continuing —
    /// so a broken wiring is a loud failure rather than a toolless agent that cheerfully
    /// reports nothing.</para>
    ///
    /// <para><b>Auth.</b> <c>CODEX_HOME</c> is where Codex keeps credentials under file-based
    /// storage, so a fresh directory has none. When the run supplied a key that is fine —
    /// <c>CODEX_API_KEY</c> in the environment covers <c>codex exec</c>. On an
    /// already-logged-in machine (the <c>DOCKET_REAL_CODEX=1</c> path) the operator's
    /// <c>auth.json</c> is copied in, which is the documented headless fallback; note the docs
    /// warn that Codex rewrites refresh tokens in place, so a copy can go stale against the
    /// original.</para>
    ///
    /// <para><b>Known production gap, deliberately not worked around here.</b> One
    /// <c>CODEX_HOME</c> is shared by every worker this test process spawns, because docketd
    /// has no per-profile environment seam and no <c>{codex_home}</c> placeholder — a test can
    /// set it process-wide, a real operator cannot set it per task. Fine for a rig that runs
    /// tasks in sequence; called out in the gaps report as the real fix.</para>
    /// </summary>
    private sealed class CodexHome : IDisposable
    {
        private readonly string _dir;
        private readonly string? _previous;

        private CodexHome(string dir, string? previous)
        {
            _dir = dir;
            _previous = previous;
        }

        /// <summary>Create the directory (Codex requires <c>CODEX_HOME</c> to already exist),
        /// write the docket MCP server table, seed auth if needed, and publish the variable so
        /// spawned workers inherit it from docketd's environment.</summary>
        public static CodexHome Create(string mcpUrl, IReadOnlyList<string> allowedTools)
        {
            var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
            var dir = Path.Combine(Path.GetTempPath(), "docket-codex-home-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            var tools = string.Join(", ", allowedTools.Select(t => $"\"{t}\""));
            File.WriteAllText(
                Path.Combine(dir, "config.toml"),
                $"""
                 [mcp_servers.docket]
                 url = "{mcpUrl}"
                 bearer_token_env_var = "DOCKET_WORKER_TOKEN"
                 enabled_tools = [{tools}]
                 required = true
                 startup_timeout_sec = 30.0
                 tool_timeout_sec = 120.0

                 """);

            // No key in the environment means this is the already-logged-in path; carry the
            // operator's credentials across so a fresh CODEX_HOME is not an instant auth
            // failure. Copy only when absent, per the docs' warning about refresh rewrites.
            if (Environment.GetEnvironmentVariable("CODEX_API_KEY") is not { Length: > 0 })
            {
                var operatorAuth = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
                var seeded = Path.Combine(dir, "auth.json");
                if (File.Exists(operatorAuth) && !File.Exists(seeded))
                    try { File.Copy(operatorAuth, seeded); } catch { /* best effort; the run will report auth */ }
            }

            Environment.SetEnvironmentVariable("CODEX_HOME", dir);
            return new CodexHome(dir, previous);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", _previous);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The one description template both roles use (§7) — identical to the claude
    /// tier's, on purpose: a cross-harness comparison is only meaningful if both harnesses are
    /// given the same words.</summary>
    private static string EchoDescription(string label, string token) =>
        $"""
         Call report_result exactly once, with its resultReference set to this exact string
         (no quotes, no other text):

         {label}:{token}

         That is the entire task. Do not create or edit files. Do not do anything else.
         """;

    /// <summary>The claude argv, for the mixed-fleet fact only — the validated recipe from the
    /// claude tier, trimmed to what an echo task needs.</summary>
    private static string[] ClaudeWorkerSpawn(string claudeBin) =>
    [
        claudeBin, "-p", WorkerPrompt,
        "--mcp-config", "{mcp_config}",
        "--strict-mcp-config",
        "--allowedTools", "mcp__docket__get_task,mcp__docket__report_result",
        "--output-format", "stream-json",
        "--verbose",
        "--model", "haiku",
        "--max-turns", "8",
    ];

    // ── Gating ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Skip unless this run is a real, opted-in Codex run: Postgres up, an explicit opt-in,
    /// and the <c>codex</c> CLI resolvable. Returns the resolved binary when everything is in
    /// place; otherwise throws the xUnit skip, so an ordinary push spends nothing.
    ///
    /// <para>Two opt-in paths, mirroring the claude tier's two. A key is the CI path and is
    /// published as <c>CODEX_API_KEY</c> — the variable the docs name for <c>codex exec</c>
    /// ("Provides an API key for a single non-interactive run. This is only supported in
    /// <c>codex exec</c>"). <c>OPENAI_API_KEY</c> is <em>not</em> documented as a variable
    /// Codex itself reads, so it is treated as a source to map from, never relied on directly.
    /// <c>DOCKET_REAL_CODEX=1</c> is the path for a machine whose CLI is already logged in;
    /// no key is published there, and <see cref="CodexHome"/> carries the existing
    /// credentials instead.</para>
    /// </summary>
    private string RequireRealCodex()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var key = FirstNonEmpty("CODEX_API_KEY", "OPENAI_API_KEY", "OPENAI_KEY");
        var optedIn = Environment.GetEnvironmentVariable("DOCKET_REAL_CODEX") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(string.IsNullOrWhiteSpace(key) && !optedIn,
            "no CODEX_API_KEY/OPENAI_API_KEY/OPENAI_KEY and no DOCKET_REAL_CODEX — the real "
            + "codex exec E2E is opt-in (see the gated CI job)");
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable("CODEX_API_KEY", key);

        var codexBin = ResolveBin("codex", "DOCKET_CODEX_BIN");
        Skip.If(codexBin is null, "codex CLI not found (set DOCKET_CODEX_BIN or put codex on PATH)");
        ScrubInheritedSessionMarkers();
        return codexBin!;
    }

    /// <summary>
    /// Whether this run claims docketd can spawn a worker without an open-forever stdin pipe.
    /// Purely a declaration by whoever set it — nothing here can verify it, exactly as
    /// <c>stop.mode</c> is a declaration docketd cannot verify.
    /// </summary>
    private static bool StdinFixDeclared =>
        Environment.GetEnvironmentVariable("DOCKET_CODEX_STDIN_FIXED") is { Length: > 0 } v
        && !v.Equals("0", StringComparison.Ordinal)
        && !v.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Skip the end-to-end facts while the confirmed dead-man-stdin blocker stands (see the
    /// class remarks). These are <b>not</b> skipped because they are unfinished — they are
    /// skipped because the harness provably cannot reach the first turn, so running them would
    /// burn eight minutes per leg to observe a hang the cheap characterization fact already
    /// pins. Set <c>DOCKET_CODEX_STDIN_FIXED=1</c> once docketd can spawn without holding
    /// stdin open, and these become the real coverage with no other change.
    /// </summary>
    private static void RequireStdinFix() =>
        Skip.IfNot(StdinFixDeclared,
            "codex exec blocks forever on docketd's held-open dead-man stdin pipe "
            + "(exec/src/lib.rs:1961 -> 1888 -> 1909 at rust-v0.147.0), so a Codex worker "
            + "cannot reach its first turn. Set DOCKET_CODEX_STDIN_FIXED=1 when docketd grows "
            + "a per-profile stdin policy.");

    /// <summary>The mixed-fleet fact needs a real claude too; skip rather than half-test.
    /// Deliberately does not re-check the Anthropic opt-in: on a logged-in machine the CLI
    /// needs no key, and <see cref="RequireRealCodex"/> already established this is an
    /// opted-in real run.</summary>
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

    /// <summary>Resolve a CLI: an explicit override variable, then PATH, then the common
    /// install locations — or null when none exists.</summary>
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
    /// Remove the "you are running inside a Claude Code session" markers from this process's
    /// environment, because the worker inherits it (docketd's environment is the child's base,
    /// §10) and in production docketd is a daemon rather than a child of somebody's editor.
    /// Kept for the Codex tier too: the markers are inherited by <em>any</em> child, and a
    /// Codex worker that finds them has no more business acting on them than a claude one.
    /// Auth and routing variables are deliberately left alone.
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

    /// <summary>
    /// The diagnostic that makes the most likely Codex failure legible instead of mysterious.
    /// A worker that spawned, produced no events, and was never seen working is the exact
    /// signature of <c>codex exec</c> blocking on docketd's held-open stdin — so say so, at the
    /// top of the dump, rather than leaving the next reader to rediscover it.
    /// </summary>
    private static string StdinHypothesis(FleetRig rig, TaskId task)
    {
        var observed = rig.WorkerObserved(task);
        if (observed is null or { Starts: 0 })
            return "";
        return """
               HYPOTHESIS: the worker process started but produced no usable stream. The
               leading suspect is `codex exec` consuming stdin to EOF before it begins its
               turn — docketd redirects stdin on every spawn and holds it open for the task's
               whole life as the §10 dead-man's switch, so a harness that waits for EOF waits
               forever. `claude -p` survives this by giving up after ~3s; the Codex docs say
               nothing about it. Check whether the process is alive but idle with no output.
               If so this is a Docket gap (docketd needs a per-profile way to close/omit the
               dead-man pipe), not a broken test.

               """;
    }

    /// <summary>A short unforgeable token the worker can only report if it really read the
    /// live task description — the proof a real agent, not a fake, closed the loop.</summary>
    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];
}
