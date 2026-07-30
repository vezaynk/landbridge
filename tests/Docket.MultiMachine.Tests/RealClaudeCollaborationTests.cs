using Docket.ControlPlane.Tests;
using Docket.Core;

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
/// Every fact here SKIPS cleanly unless an Anthropic key is present in the environment
/// (<c>ANTHROPIC_API_KEY</c>, or <c>ANTHROPIC_KEY</c> which the opt-in CI job maps to it)
/// AND the <c>claude</c> CLI resolves — so a normal push/PR run, which never injects the
/// secret, spends zero tokens. The dedicated CI job (see <c>.github/workflows/ci.yml</c>)
/// sets the key and runs <em>only</em> this trait. Kept tiny and turn-capped
/// (<c>--model haiku</c>, small <c>--max-turns</c>) so a full run costs a few cents.</para>
///
/// <para>Skips (rather than fails) when Postgres is unavailable, mirroring every other
/// Postgres-backed suite.</para>
/// </summary>
[Trait("Category", RealClaude)]
[Collection(PostgresCollection.Name)]
public sealed class RealClaudeCollaborationTests(PostgresFixture pg) : IAsyncLifetime
{
    /// <summary>Trait value the opt-in CI job filters on so it runs <em>only</em> this tier.</summary>
    public const string RealClaude = "RealClaude";

    /// <summary>A real haiku call is ~1 min; give each task-to-verifying a generous bound
    /// (this tier only ever runs in the opt-in job, never on the latency-sensitive path).</summary>
    private static readonly TimeSpan ReachVerifying = TimeSpan.FromMinutes(4);

    /// <summary>The allow-listed docket tools the worker needs — and nothing else, so a
    /// stray step can't wander off spending turns. <c>--strict-mcp-config</c> keeps the
    /// injected <c>docket</c> server the only MCP surface (never the operator's own).</summary>
    private const string AllowedTools = "mcp__docket__get_task,mcp__docket__report_result";

    /// <summary>Generic worker prompt (§7): the specifics live in each task's opaque
    /// <b>description</b>, read via <c>get_task</c>, so one profile drives every role.</summary>
    private const string WorkerPrompt =
        "You are a Docket worker agent. Begin by calling the docket get_task tool to read " +
        "your assignment: it returns a description telling you exactly what to do. Do precisely " +
        "what the description says and nothing more. Finish by calling the docket report_result " +
        "tool with the exact resultReference the description specifies. Work fully autonomously; " +
        "never ask questions and never wait for input.";

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
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg, ClaudeWorkerSpawn(claudeBin));
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B"); // a real fleet: >1 machine enrolled, dispatch steered to A

        var token = NewToken();
        var task = await rig.CreateTaskAsync(EchoDescription("A", token), ct);
        await rig.DispatchToAsync("A", ct);

        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.StateAsync(task, ct) == TaskState.Verifying, ReachVerifying),
            "the real claude worker never drove its task to verifying. " + await rig.DiagnoseAsync(task, ct));

        var reference = await rig.ResultReferenceAsync(task, ct);
        Assert.Contains(token, reference); // the exact live-description token round-tripped through the real agent
        Assert.Equal("A", rig.MachineOf(task));
    }

    /// <summary>
    /// The two-machine handoff: a real claude worker on machine A produces a token and
    /// drives its task to verifying; the test then reads A's <em>committed</em> result off
    /// the control plane and threads it into a follow-up task a real claude worker on
    /// machine B must confirm. B reaching verifying with A's token is proof the value
    /// flowed A → plane → B across two distinct machines, each driven by a real agent.
    /// </summary>
    [SkippableFact]
    public async Task Real_claude_workers_hand_off_a_token_across_two_machines()
    {
        var claudeBin = RequireRealClaude();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = cts.Token;

        await using var rig = new FleetRig(pg, ClaudeWorkerSpawn(claudeBin));
        await rig.StartAsync(ct);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        // Step A on machine A: mint + report an unforgeable token.
        var token = NewToken();
        var stepA = await rig.CreateTaskAsync(EchoDescription("A", token), ct);
        await rig.DispatchToAsync("A", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.StateAsync(stepA, ct) == TaskState.Verifying, ReachVerifying),
            "machine A's real claude worker never drove step A to verifying. " + await rig.DiagnoseAsync(stepA, ct));

        // The handoff: read what A actually committed, not the test's own constant.
        var referenceA = await rig.ResultReferenceAsync(stepA, ct);
        Assert.Contains(token, referenceA);

        // Step B on machine B: confirm the token A produced.
        var stepB = await rig.CreateTaskAsync(ConfirmDescription(token), ct);
        await rig.DispatchToAsync("B", ct);
        Assert.True(
            await FleetRig.WaitUntilAsync(async () => await rig.StateAsync(stepB, ct) == TaskState.Verifying, ReachVerifying),
            "machine B's real claude worker never confirmed the handoff to verifying. " + await rig.DiagnoseAsync(stepB, ct));

        var referenceB = await rig.ResultReferenceAsync(stepB, ct);
        Assert.Contains(token, referenceB); // B reported A's token: the value crossed the fleet
        // The two steps really ran on two different machines.
        Assert.Equal("A", rig.MachineOf(stepA));
        Assert.Equal("B", rig.MachineOf(stepB));
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

    /// <summary>Step-A / minimum-bar description (§7): report <c>&lt;label&gt;:&lt;token&gt;</c>.</summary>
    private static string EchoDescription(string label, string token) =>
        $"""
         You are collaborating with other agents in a fleet. This is step {label} of a handoff.

         The shared token for this run is: {token}

         Your only job: call the report_result tool exactly once, with its resultReference
         argument set to this exact string, with no quotes and no extra text:

         {label}:{token}

         Do not create or edit any files. After you call report_result you are finished.
         """;

    /// <summary>Step-B description: confirm the token machine A produced.</summary>
    private static string ConfirmDescription(string token) =>
        $"""
         You are collaborating with other agents in a fleet. This is step B of a handoff.

         Machine A has finished step A and produced this token: {token}

         Your only job: call the report_result tool exactly once, with its resultReference
         argument set to this exact string, with no quotes and no extra text:

         B:{token}

         Do not create or edit any files. After you call report_result you are finished.
         """;

    // ── Gating ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Skip unless this run is a real, opted-in claude run: Postgres up, an Anthropic key
    /// present, and the <c>claude</c> CLI resolvable. Returns the resolved claude binary
    /// when everything is in place; otherwise throws the xUnit skip so a keyless push
    /// spends nothing. As a side effect it publishes <c>ANTHROPIC_API_KEY</c> into this
    /// process's environment (from <c>ANTHROPIC_KEY</c> if that is the only form present)
    /// so the spawned claude — which inherits docketd's environment — can authenticate.
    /// </summary>
    private string RequireRealClaude()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable("ANTHROPIC_KEY");
        Skip.If(string.IsNullOrWhiteSpace(key),
            "no ANTHROPIC_API_KEY/ANTHROPIC_KEY — the real claude -p E2E is opt-in (see the gated CI job)");
        // The child inherits this process's environment (ProcessStartInfo does not clear
        // it under UseShellExecute=false); make sure the canonical variable is set.
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key);

        var claudeBin = ResolveClaudeBin();
        Skip.If(claudeBin is null,
            "claude CLI not found (set DOCKET_CLAUDE_BIN or put claude on PATH)");
        return claudeBin!;
    }

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
