using Docket.Contracts;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// §10 BYO-harness, <b>third</b> harness: the same fleet driven by OpenCode
/// (<c>opencode acp</c>) instead of <c>claude-agent-acp</c> or <c>codex-acp</c>.
/// Opt-in and token-spending, gated exactly like the other two real tiers. The portable
/// bar (verifying + session ref, usage/cost, park → resume via <c>--session</c>) is
/// <see cref="RealHarnessBar"/>, wrapped below so <c>Category=RealOpenCode</c> still
/// isolates the job.
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
/// (<c>packages/opencode/src/mcp/catalog.ts:119</c>), and the worker prompts here spell that
/// underscore form rather than the bare <c>get_task</c> they used to — see
/// <see cref="McpToolsRule"/> for why the portable bare spelling turned out to cost more than it
/// bought. (3) There is no tool-call <em>start</em>
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

    private const int MaxAttempts = RealHarnessBar.MaxAttempts;
    private static readonly TimeSpan PerLegBudget = RealHarnessBar.PerLegBudget;

    /// <summary>
    /// The standing rule both prompts here carry, ported from the claude tier where it fixed a real
    /// failure — a prompt that said "call the docket get_task tool" was read as a shell command and
    /// the worker ran <c>docket get_task</c> instead of calling its MCP tool.
    ///
    /// <para><b>The spelling is this tier's own, and that is the whole subtlety.</b> OpenCode names
    /// docket's tools <c>docket_get_task</c> (<c>mcp/catalog.ts:119</c>), where claude and Codex
    /// both use <c>mcp__docket__get_task</c>. Porting the claude wording verbatim would name a tool
    /// that does not exist on this harness — inventing a phantom tool, which is precisely the bug
    /// this rule exists to prevent. So the underscore form here is not a typo, and a "consistency"
    /// edit that aligns it with the other two tiers breaks this one.</para>
    ///
    /// <para>This supersedes OC-G3's original reasoning, which named the tools <em>bare</em>
    /// (<c>get_task</c>) on the grounds that the bare form is the one spelling that ports across
    /// all three harnesses. That was true and is still true — but portability was buying less than
    /// it cost: the bare form is exactly what a worker misread as <c>docket get_task</c>. Each tier
    /// naming its own real tool is unambiguous in a way no shared spelling can be.</para>
    /// </summary>
    private const string McpToolsRule =
        " Docket's tools are MCP tools, named exactly docket_get_task, docket_report_result and so " +
        "on — call them as tools, under those names. There is no `docket` program: no such command " +
        "exists on this machine, so never run `docket` in a shell, and never try to reach the " +
        "docket MCP server yourself over HTTP or with curl. (A shell command your assignment " +
        "explicitly asks for is a different thing, and is fine.) If a docket MCP tool is missing " +
        "or errors, report that with docket_report_result instead of working around it.";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_drives_a_task_to_verifying_on_the_fleet() =>
        RealHarnessBar.DriveToVerifyingAsync(pg, RealHarnessProfiles.OpenCode(RequireRealOpenCode()));

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_reports_usage_the_harness_emits() =>
        RealHarnessBar.ReportsUsageAsync(pg, RealHarnessProfiles.OpenCode(RequireRealOpenCode()));

    [SkippableFact(Timeout = RealHarnessBar.TwoLegTimeoutMs)]
    public Task Real_worker_resumes_its_transcript_after_a_park_and_reports_a_memory_only_nonce() =>
        RealHarnessBar.ResumesAfterParkAsync(pg, RealHarnessProfiles.OpenCode(RequireRealOpenCode()));


    /// <summary>
    /// What a <c>stop</c> actually is against a real <c>opencode run</c> worker: a TTL'd kill,
    /// declared as <c>session/cancel</c> plus the deadline because that is the true claim about this
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
    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public async Task A_stop_reaches_a_real_opencode_worker_as_a_kill_deadline_with_its_session_ref_preserved()
    {
        var openCodeBin = RequireRealOpenCode();
        using var cts = new CancellationTokenSource(RealHarnessBar.EchoTimeout);
        var ct = cts.Token;

        var profile = RealHarnessProfiles.OpenCode(openCodeBin);
        await using var rig = new FleetRig(
            pg,
            spawnArgv: profile.AcpSpawn,
            prompt: SlowWorkerPrompt,
            followUp: profile.FollowUpTurn,
            stop: new StopConfig(WindDown: TimeSpan.FromSeconds(5)),
            model: profile.Model);
        await rig.StartAsync(ct);
        using var config = profile.AttachTo(rig);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(EchoDescription("A", NewToken()), ct);
        await rig.DispatchToAsync("A", ct);

        // Wait for the worker to be genuinely under way — a session ref means its stream started,
        // which is the earliest point at which stopping it tests anything.
        Assert.True(
            await FleetRig.WaitUntilAsync(
                async () => await rig.HarnessSessionRefAsync(task, ct) is { Length: > 0 },
                RealHarnessBar.PerLegBudget),
            "the opencode worker never reported a session ref, so there was nothing to stop.\n"
            + OpenCodeFailureHypotheses() + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var refBeforeStop = await rig.HarnessSessionRefAsync(task, ct);

        Assert.True(
            await rig.SendStopAsync(
                "A", task, TimeSpan.FromMinutes(1), StopDisposition.PreserveAndPark,
                "characterizing real-opencode stop delivery", ct),
            "the stop was not delivered to the machine holding the task.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // ACP stop is session/cancel, then the deadline. The session is open (we waited
        // for a session ref), so the ack is CancelSent — sent, not confirmed obeyed.
        var ack = rig.StopAckFor(task);
        Assert.NotNull(ack);
        Assert.True(ack!.Value.Actioned);
        Assert.Equal(StopDelivery.CancelSent, ack.Value.Delivery);

        // The kill really landed: the process is gone, and it did not exit cleanly on its own.
        Assert.True(
            await FleetRig.WaitUntilAsync(
                () => Task.FromResult(rig.WorkerObserved(task) is { Exits: > 0 }),
                TimeSpan.FromSeconds(30)),
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
    [SkippableFact(Timeout = RealHarnessBar.TwoLegTimeoutMs)]
    public async Task A_claude_worker_and_an_opencode_worker_hand_off_a_token_across_one_fleet()
    {
        var openCode = RealHarnessProfiles.OpenCode(RequireRealOpenCode());
        var claude = RealHarnessProfiles.Claude(RequireRealClaudeForMixedFleet());
        using var cts = new CancellationTokenSource(RealHarnessBar.TwoLegTimeout);
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: openCode.AcpSpawn,
            prompt: openCode.EchoPrompt,
            followUp: openCode.FollowUpTurn,
            model: openCode.Model);
        await rig.StartAsync(ct);
        using var config = openCode.AttachTo(rig);

        await rig.AddMachineAsync("A", claude.AcpSpawn, prompt: claude.EchoPrompt, followUp: claude.FollowUpTurn);
        await rig.AddMachineAsync("B");

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

    // ── Helpers ────────────────────────────────────────────────────────────────

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
    private static string OpenCodeModel => RealHarnessProfiles.OpenCodeModel;

    /// <summary>The one description template every role uses (§7) — identical to the other two
    /// tiers', on purpose: a cross-harness comparison is only meaningful if both harnesses are
    /// given the same words.</summary>
    private static string EchoDescription(string label, string token) =>
        RealHarnessProfiles.EchoDescription(label, token);

    /// <summary>
    /// A prompt that keeps the worker busy long enough to be stopped mid-flight. Deliberately
    /// bounded work rather than an infinite loop: if the stop never arrives the worker finishes
    /// and the fact fails on its assertions rather than hanging until the outer deadline.
    /// </summary>
    private const string SlowWorkerPrompt =
        "You are a Docket worker agent. First call the docket_get_task tool to read your "
        + "assignment. Then, before reporting anything, count slowly from 1 to 400, writing each "
        + "number on its own line with a short remark about it. Only after finishing the count "
        + "may you call the docket_report_result tool with the exact string from the description."
        + McpToolsRule;

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

        var key = RealHarnessProfiles.FirstNonEmpty("ANTHROPIC_API_KEY", "ANTHROPIC_KEY");
        var optedIn = Environment.GetEnvironmentVariable("DOCKET_REAL_OPENCODE") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(string.IsNullOrWhiteSpace(key) && !optedIn,
            "no ANTHROPIC_API_KEY/ANTHROPIC_KEY and no DOCKET_REAL_OPENCODE — the real "
            + "opencode run E2E is opt-in (see the gated CI job)");
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key);

        var bin = RealHarnessProfiles.ResolveBin("opencode", "DOCKET_OPENCODE_BIN");
        Skip.If(bin is null, "opencode CLI not found (set DOCKET_OPENCODE_BIN or put opencode on PATH)");
        return bin!;
    }

    /// <summary>The mixed-fleet fact needs a real claude too; skip rather than half-test.</summary>
    private static string RequireRealClaudeForMixedFleet()
    {
        var claudeBin = RealHarnessProfiles.ResolveBin("claude", "DOCKET_CLAUDE_BIN");
        Skip.If(claudeBin is null,
            "claude CLI not found — the mixed-harness fleet fact needs BOTH CLIs "
            + "(set DOCKET_CLAUDE_BIN or put claude on PATH)");
        return claudeBin!;
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
           4. PERMISSIONS. session/request_permission is answered by the plane, not --auto.
           5. AUTH. ANTHROPIC_API_KEY must be in the environment, or the machine's opencode must
              already be logged in.

         """;

    private static string NewToken() => RealHarnessProfiles.NewToken();
}
