using Landbridge.Contracts;
using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Landbridge.Runner;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// The §10 BYO-harness promise against a <b>second real harness</b>: OpenAI's Codex
/// (<c>codex-acp</c>) driving Landbridge tasks on the same real plane + relay + landbridged rigs
/// the <see cref="RealClaudeCollaborationTests"/> tier uses, with no change below the spawn
/// seam. If Landbridge's "everything harness-specific is data" claim is true, a Codex worker is
/// a profile, not a code change — and this tier is where that stops being an assertion in a
/// design doc.
///
/// <para><b>Opt-in and token-spending</b>, exactly like the claude tier: the facts SKIP
/// unless the run opted in — an OpenAI key in the environment (<c>CODEX_API_KEY</c>, or
/// <c>OPENAI_API_KEY</c>/<c>OPENAI_KEY</c> which the gate maps to it), or
/// <c>LANDBRIDGE_REAL_CODEX=1</c> on a machine whose CLI is already logged in — AND the
/// <c>codex</c> CLI resolves. A normal push/PR run does neither and spends nothing. The
/// dedicated CI job (<c>.github/workflows/ci.yml</c>, <c>real-codex-e2e</c>) sets the key
/// and runs only this trait.</para>
///
/// <para>The portable minimum bar — report + session ref, tokens (Codex computes no
/// cost), park → resume via <c>codex exec resume</c> — lives in <see cref="RealHarnessBar"/>
/// and is wrapped below so <c>Category=RealCodex</c> still isolates the job. Dead-man hang,
/// stop-as-signal, and the mixed claude+codex fleet stay in this file.</para>
///
/// <para>The API-key catalog does not serve every slug the CLI lists.
/// <c>gpt-5.1-codex-mini</c> and an unpinned <c>gpt-5.6-sol</c> 404 on
/// <c>/v1/responses</c> for this project. The pin is <c>gpt-5.3-codex</c>,
/// matching the CI dispatch default. <c>LANDBRIDGE_CODEX_MODEL</c> overrides it.</para>
///
/// <para><b>Source reading is still the authority for every claim below</b>, and that is
/// deliberate rather than leftover: <c>codex</c> is not installed on the machine where this
/// tier was written and will not be, so the reasoning was established by reading the CLI's own
/// source at tag <c>rust-v0.147.0</c> — the version <c>npm install -g @openai/codex</c>
/// resolves to, and the one the CI job installs — with file:line citations. The green run
/// confirms the conclusions; it does not replace them, because a passing test says *that* the
/// profiles are right and the citations say *why*. That includes the one thing which decides
/// whether this tier can pass at all:</para>
///
/// <para><b>The blocker, and what closed it.</b> A held-open stdin pipe is fatal to
/// <c>codex exec</c>. Codex's cold-start path is <c>resolve_root_prompt</c>
/// (<c>codex-rs/exec/src/lib.rs:1961</c>), which — even when a prompt was supplied as
/// argv — calls <c>read_prompt_from_stdin(OptionalAppend)</c>. That function
/// (<c>lib.rs:1888</c>) returns early <em>only</em> when
/// <c>std::io::stdin().is_terminal()</c>; a pipe is not a terminal, so it falls through to
/// <c>std::io::stdin().read_to_end(&amp;mut bytes)</c> at <c>lib.rs:1909</c> and blocks until
/// EOF. There is no argv-only escape: no flag suppresses the append-read, and
/// <c>codex exec -</c> forces stdin as the prompt, which blocks identically.
/// <c>claude -p</c> survives the same pipe only because it gives up after ~3s.</para>
///
/// <para>landbridged used to hold that pipe open unconditionally — it <em>is</em> the §10
/// dead-man's switch — so a Codex worker could not run here at all. #110 made the switch a
/// per-profile declaration, so the profiles below declare <c>stdin: closed</c> and get a
/// deterministic EOF right after spawn. The trade is honest and stated in
/// <c>runner-config.md</c>: such a worker no longer dies with landbridged, and the
/// <c>StrayReaper</c>'s next-start sweep is the only thing that collects it.</para>
///
/// <para>the dead-man-hang characterization (removed with stream mode)
/// declares <c>stdin: deadman</c> explicitly and keeps documenting the incompatibility, for
/// $0, because Codex never reaches a model there. It is a <b>permanent characterization</b>
/// of this harness, not a tripwire waiting to flip — the flip already happened, and the
/// remaining value is that the reason <c>closed</c> is required stays proven against the
/// real binary rather than resting on a source citation.</para>
///
/// <para><b>The resume path is exempt</b>, which is a genuinely useful asymmetry:
/// <c>codex exec resume &lt;id&gt; "&lt;prompt&gt;"</c> resolves through <c>resolve_prompt</c>
/// (<c>lib.rs:1944</c>), whose first arm returns a non-<c>-</c> argv prompt immediately and
/// never touches stdin. So resume-with-argv-prompt would work under landbridged unchanged.</para>
///
/// <para><b>MCP reachability — confirmed workable.</b> Codex has no
/// <c>--mcp-config &lt;file&gt;</c>; its only client surface is a <c>config.toml</c> under
/// <c>CODEX_HOME</c>, so the injected <c>{mcp_config}</c> is unusable and
/// <see cref="RealHarnessProfiles.CodexHome"/> writes a TOML table instead. The per-instance bearer rides
/// <c>bearer_token_env_var = "LANDBRIDGE_WORKER_TOKEN"</c>, and
/// <c>resolve_bearer_token</c> (<c>codex-rs/codex-mcp/src/rmcp_client.rs:822</c>) reads that
/// variable from the <em>live process environment</em> at connect time — which is exactly
/// where landbridged injects the fresh per-spawn token. One static file is therefore correct for
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
    private const int MaxAttempts = RealHarnessBar.MaxAttempts;
    private static readonly TimeSpan PerLegBudget = RealHarnessBar.PerLegBudget;

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_reports_on_the_fleet() =>
        RealHarnessBar.DriveToReportAsync(pg, RealHarnessProfiles.Codex(RequireRealCodex()));

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_reports_usage_the_harness_emits() =>
        RealHarnessBar.ReportsUsageAsync(pg, RealHarnessProfiles.Codex(RequireRealCodex()));

    [SkippableFact(Timeout = RealHarnessBar.TwoLegTimeoutMs)]
    public Task Real_worker_resumes_its_transcript_after_a_park_and_reports_a_memory_only_nonce() =>
        RealHarnessBar.ResumesAfterParkAsync(pg, RealHarnessProfiles.Codex(RequireRealCodex()));


    /// <summary>
    /// What a graceful <c>stop</c> actually is against a real <c>codex exec</c> worker, pinned
    /// the way #103's honesty framework requires: the profile declares
    /// <c>session/cancel</c> plus the deadline because that is the <em>true</em> claim about this
    /// harness, and the assertions are that landbridged armed a deadline and the worker died on
    /// it with its transcript ref intact.
    ///
    /// <para><b>Why <c>signal</c> and not <c>message</c>.</b> <c>mode: message</c> is a
    /// declaration that a running session reads turns off its stdin, and landbridged cannot check
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
    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public async Task A_stop_reaches_a_real_codex_worker_as_a_kill_deadline_with_its_thread_ref_preserved()
    {
        var codexBin = RequireRealCodex();
        using var cts = new CancellationTokenSource(RealHarnessBar.EchoTimeout);
        var ct = cts.Token;

        var profile = RealHarnessProfiles.Codex(codexBin);
        await using var rig = new FleetRig(
            pg,
            spawnArgv: profile.AcpSpawn,
            prompt: profile.EchoPrompt,
            followUp: profile.FollowUpTurn,
            stop: new StopConfig(WindDown: TimeSpan.FromSeconds(5)),
            authMethod: profile.AuthMethod);
        await rig.StartAsync(ct);
        using var home = profile.AttachTo(rig);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateSessionAsync(EchoDescription("A", NewToken()), ct);

        // Wait until the worker is demonstrably mid-turn — it reported a thread ref, so its
        // harness is up and streaming — before stopping it. Stopping earlier would race the spawn.
        Assert.True(
            await rig.DispatchUntilAsync(
                task, "A", async () => await rig.HarnessSessionRefAsync(task, ct) is { Length: > 0 },
                MaxAttempts, PerLegBudget, ct),
            "the real codex worker never reported a thread ref, so it was never observably working.\n"
            + CodexFailureHypotheses(rig, task) + await rig.RealWorkerDiagnosticsAsync(task, ct));

        var sessionRef = await rig.HarnessSessionRefAsync(task, ct);
        Assert.True(
            await rig.SendStopAsync(
                "A", task, TimeSpan.FromMinutes(1), StopDisposition.PreserveAndPark,
                "characterizing real-codex stop delivery", ct),
            "the stop was not delivered to the machine holding the task");

        // ACP stop is session/cancel, then the deadline kill. The session is open
        // (we waited for a session ref), so the ack is CancelSent — sent, not
        // confirmed obeyed.
        var ack = rig.StopAckFor(task);
        Assert.NotNull(ack);
        Assert.True(ack!.Value.Actioned);
        Assert.Equal(StopDelivery.CancelSent, ack.Value.Delivery);

        Assert.True(
            await FleetRig.WaitUntilAsync(
                () => Task.FromResult(rig.WorkerObserved(task) is { Exits: > 0 }),
                TimeSpan.FromSeconds(30)),
            "the worker never ended, so the deadline did not fire either.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));

        // Preservation is the plane's record: the ref outlives the kill, so the Codex thread
        // stays resumable.
        Assert.Equal(sessionRef, await rig.HarnessSessionRefAsync(task, ct));
    }

    /// <summary>
    /// Landbridge's whole pitch, as one fact: <b>a fleet of mixed harnesses</b>. Machine A runs
    /// <c>claude-agent-acp</c>, machine B runs <c>codex-acp</c>, under one control plane,
    /// and B reports a token that only A could have produced — read off the plane, not
    /// out of the test's own constant. Two vendors' ACP agents, one task graph, and
    /// nothing between them but Landbridge.
    ///
    /// <para>The per-machine spawn override is what makes this expressible: the fleet's
    /// profile is one shape, but each machine's <c>default</c> profile spawns its own harness
    /// (see <see cref="FleetRig.AddMachineAsync"/>). Everything else — dispatch, the worker
    /// token, <c>get_session</c>, <c>report_result</c>, the result reference — is identical for
    /// both, which is the point being made.</para>
    ///
    /// <para>Needs BOTH CLIs, so it skips when either is missing rather than half-testing. The
    /// shared <c>events.mapping</c> is Codex's: it is additive for claude on the one key that
    /// matters here (claude's <c>system</c>/<c>init</c> line is not what this mapping matches),
    /// so A contributes no session ref under it — which is why this fact asserts on results and
    /// machines, never on A's ref.</para>
    /// </summary>
    [SkippableFact(Timeout = RealHarnessBar.TwoLegTimeoutMs)]
    public async Task A_claude_worker_and_a_codex_worker_hand_off_a_token_across_one_fleet()
    {
        var codex = RealHarnessProfiles.Codex(RequireRealCodex());
        var claude = RealHarnessProfiles.Claude(RequireRealClaudeForMixedFleet());
        using var cts = new CancellationTokenSource(RealHarnessBar.TwoLegTimeout);
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: codex.AcpSpawn,
            prompt: codex.EchoPrompt,
            followUp: codex.FollowUpTurn,
            authMethod: codex.AuthMethod);
        await rig.StartAsync(ct);
        using var home = codex.AttachTo(rig);

        await rig.AddMachineAsync("A", claude.AcpSpawn, prompt: claude.EchoPrompt, followUp: claude.FollowUpTurn);
        await rig.AddMachineAsync("B");

        // Step A, on the claude machine: mint + report an unforgeable token.
        var token = NewToken();
        var stepA = await rig.CreateSessionAsync(EchoDescription("A", token), ct);
        Assert.True(
            await rig.DispatchUntilReportedAsync(stepA, "A", MaxAttempts, PerLegBudget, ct),
            "the real claude worker never mailed a report on step A.\n"
            + await rig.RealWorkerDiagnosticsAsync(stepA, ct));

        // The handoff: read what A actually committed to the plane, not the test's constant.
        var referenceA = await rig.ResultReferenceAsync(stepA, ct);
        Assert.Contains(token, referenceA);

        // Step B, on the codex machine: report the token the claude worker produced.
        var stepB = await rig.CreateSessionAsync(EchoDescription("B", token), ct);
        Assert.True(
            await rig.DispatchUntilReportedAsync(stepB, "B", MaxAttempts, PerLegBudget, ct),
            "the real codex worker never confirmed the cross-harness handoff.\n"
            + CodexFailureHypotheses(rig, stepB) + await rig.RealWorkerDiagnosticsAsync(stepB, ct));

        Assert.Contains(token, await rig.ResultReferenceAsync(stepB, ct)); // crossed the harness boundary
        Assert.Equal("A", rig.MachineRanOn(stepA));
        Assert.Equal("B", rig.MachineRanOn(stepB));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The model every fact in this tier pins, and it is pinned rather than left to the
    /// machine's default deliberately: the default is whatever the operator or the server-side
    /// catalog says, which for a token-spending CI job is an open cheque.
    ///
    /// <para><c>gpt-5.3-codex</c> is the slug the API-key catalog actually serves. Cheaper
    /// names the CLI still lists (<c>gpt-5.1-codex-mini</c>, the adapter's fallback) 404 on
    /// <c>/v1/responses</c> for this project, and an unpinned <c>CODEX_HOME</c> then
    /// defaults to <c>gpt-5.6-sol</c>, which this key also cannot call. Measured
    /// 2026-08-16, twice. <c>LANDBRIDGE_CODEX_MODEL</c> overrides without a code change if
    /// the catalog moves again. The CI dispatch default is the same slug.</para>
    /// </summary>
    private static string CodexModel => RealHarnessProfiles.CodexModel;

    private static string EchoDescription(string label, string token) =>
        RealHarnessProfiles.EchoDescription(label, token);

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
    /// <c>LANDBRIDGE_REAL_CODEX=1</c> is the path for a machine whose CLI is already logged in;
    /// no key is published there, and <see cref="RealHarnessProfiles.CodexHome"/> carries the existing
    /// credentials instead.</para>
    /// </summary>
    private string RequireRealCodex()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var key = RealHarnessProfiles.FirstNonEmpty("CODEX_API_KEY", "OPENAI_API_KEY", "OPENAI_KEY");
        var optedIn = Environment.GetEnvironmentVariable("LANDBRIDGE_REAL_CODEX") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(string.IsNullOrWhiteSpace(key) && !optedIn,
            "no CODEX_API_KEY/OPENAI_API_KEY/OPENAI_KEY and no LANDBRIDGE_REAL_CODEX — the real "
            + "codex exec E2E is opt-in (see the gated CI job)");
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable("CODEX_API_KEY", key);

        var codexBin = RealHarnessProfiles.ResolveBin("codex", "LANDBRIDGE_CODEX_BIN");
        Skip.If(codexBin is null, "codex CLI not found (set LANDBRIDGE_CODEX_BIN or put codex on PATH)");
        RealHarnessProfiles.ScrubInheritedSessionMarkers();
        return codexBin!;
    }

    /// <summary>The mixed-fleet fact needs a real claude too; skip rather than half-test.
    /// Deliberately does not re-check the Anthropic opt-in: on a logged-in machine the CLI
    /// needs no key, and <see cref="RequireRealCodex"/> already established this is an
    /// opted-in real run.</summary>
    private static string RequireRealClaudeForMixedFleet()
    {
        var claudeBin = RealHarnessProfiles.ResolveBin("claude", "LANDBRIDGE_CLAUDE_BIN");
        Skip.If(claudeBin is null,
            "claude CLI not found — the mixed-harness fleet fact needs BOTH CLIs "
            + "(set LANDBRIDGE_CLAUDE_BIN or put claude on PATH)");
        return claudeBin!;
    }

    /// <summary>
    /// The diagnostic that earns its keep on a red run of this tier, and it earned it on the
    /// first one: <c>codex</c> is not installed on the machine where this was written, so CI is
    /// the only place these facts execute, and a failure has to name its own cause rather than
    /// hand the next reader a wall of state. That is not hypothetical — the first dispatch came
    /// back red on a retired model slug, and what made it a five-minute fix instead of an
    /// investigation was this branching naming the cause.
    ///
    /// <para>It branches on what the ring actually observed, because the three plausible
    /// failures have three different signatures and completely different fixes. Printed above
    /// <see cref="FleetRig.RealWorkerDiagnosticsAsync"/>, whose transcript tail (§12 capture is
    /// on for every rig profile) is where Codex's own stderr — the line that usually settles
    /// it — will be.</para>
    /// </summary>
    private static string CodexFailureHypotheses(FleetRig rig, SessionId task)
    {
        var observed = rig.WorkerObserved(task);
        if (observed is null or { Starts: 0 })
        {
            // Never spawned: nothing about Codex is implicated yet, and the dump's event log is
            // the thing to read. Say that rather than speculating about a harness that was
            // never launched.
            return """
                   HYPOTHESIS: no worker spawn was observed at all, so this is a dispatch or
                   enrollment failure and says nothing about Codex. Read the event log below:
                   a task still `submitted` was never claimed (check the machine is ready and
                   the profile name matches), and a `dispatched` with no spawn means landbridged
                   refused or failed the spawn (check the machine process log for the reason —
                   a bad `codex` path is reported only there).

                   """;
        }

        if (observed is { Exits: 0 })
        {
            // Alive and quiet. Under stdin: closed this should be impossible for the reason it
            // used to be the default outcome, which makes it the highest-signal failure in the
            // tier: it says the policy did not reach the spawn.
            return """
                   HYPOTHESIS: the worker started, is STILL RUNNING, and produced no usable
                   stream. That is the signature of `codex exec` blocking on a held-open stdin
                   pipe (exec/src/lib.rs:1961 -> 1888 -> 1909), which this profile declares
                   `stdin: closed` specifically to avoid — so suspect the POLICY, not the
                   harness. Check, in order: (1) landbridged's startup output for the
                   `profile 'default': stdin is 'closed'` notice — its ABSENCE means the profile
                   did not declare it and the dead-man pipe is still held open; (2) that
                   ProcessSupervisor still closes StandardInput right after Process.Start for
                   StdinPolicy.Closed; (3) that this rig passed ``.
                   A_cold_codex_worker_hangs_on_landbridgeds_dead_man_stdin_and_never_takes_a_turn
                   characterizes exactly this state deliberately, and should be passing.

                   """;
        }

        if (observed is { LastExitCode: not (null or 0) })
        {
            // Started and died. Codex exits non-zero for several wiring reasons, and each has a
            // distinctive line in the captured stderr — so point at the tail and at what to
            // look for, in rough order of how likely a first run is to hit it.
            return $"""
                   HYPOTHESIS: the worker started and EXITED with code {observed.Value.LastExitCode},
                   so it launched and then refused or failed — not a stdin hang. The captured
                   stderr in the transcript tail below names which; the candidates, in first-run
                   likelihood order:
                     * AUTH. CODEX_HOME is a throwaway directory, so it has no credentials of its
                       own. `codex exec` reads CODEX_API_KEY and NOT OPENAI_API_KEY
                       (exec/src/lib.rs:541, login/src/auth/manager.rs:841) — the gate maps the
                       latter onto the former, so an unset CODEX_API_KEY in CI means the mapping
                       did not fire. On the LANDBRIDGE_REAL_CODEX path, auth.json was copied from
                       ~/.codex and may have gone stale (Codex rewrites refresh tokens in place).
                     * MCP WIRING. config.toml sets `required = true`, so a landbridge server that
                       fails to initialize exits the run by design. A wrong url, an unreachable
                       plane, or an unset LANDBRIDGE_WORKER_TOKEN at connect time all land here.
                     * MODEL SLUG. The catalog is fetched server-side, so `{CodexModel}` can be
                       retired out from under this tier. Set LANDBRIDGE_CODEX_MODEL to a current
                       codex-family slug; no code change needed.

                   """;
        }

        // Started, ran, exited cleanly, and still did not satisfy the assertion: the harness
        // worked and the AGENT did not do the task. A different kind of problem entirely.
        return """
               HYPOTHESIS: the worker started and exited CLEANLY, so the harness, auth, and MCP
               wiring all worked and the agent simply did not complete the assignment. Read the
               transcript tail for which tools it called: no get_session/report_result pair means
               the tools were unavailable or unused — check `enabled_tools` in config.toml holds
               the BARE names (get_session, report_result), not the mcp__landbridge__* spelling the
               model sees. A turn that ended early is ordinary agent flakiness and the fact
               already redispatches for it (MaxAttempts).

               """;
    }

    /// <summary>A short unforgeable token the worker can only report if it really read the
    /// live task description — the proof a real agent, not a fake, closed the loop.</summary>
    private static string NewToken() => RealHarnessProfiles.NewToken();
}
