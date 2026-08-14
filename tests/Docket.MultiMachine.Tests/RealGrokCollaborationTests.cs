using Docket.Contracts;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// §10 BYO-harness, fourth harness: Grok Build (<c>grok -p --output-format
/// streaming-messages-json</c>). Opt-in and token-spending, gated like the other real tiers.
///
/// <para>Provenance: Grok 1.0.3, live runs on 2026-08-14 plus
/// <c>~/.grok/docs/user-guide/14-headless-mode.md</c>. The $0 parser half is
/// <c>GrokStreamMappingTests</c> against a captured <c>streaming-messages-json</c> stream.</para>
///
/// <para><b>Why this harness is cheap.</b> The Messages-shaped stream matches Claude's
/// defaults — no <c>events.mapping</c>. What it did force is <c>stdin: closed</c>, for a
/// different reason than Codex/OpenCode: <c>grok -p</c> starts immediately with a held-open
/// pipe, then <em>never exits</em> until stdin EOF. Deadman would complete the MCP loop and
/// leak the process.</para>
/// </summary>
[Trait("Category", RealGrok)]
[Collection(PostgresCollection.Name)]
public sealed class RealGrokCollaborationTests(PostgresFixture pg) : IAsyncLifetime
{
    public const string RealGrok = "RealGrok";

    private const int MaxAttempts = 3;
    private static readonly TimeSpan PerLegBudget = TimeSpan.FromMinutes(8);

    private const string McpToolsRule =
        " Docket's tools are MCP tools, named exactly docket__get_task, docket__report_result " +
        "and so on — call them as tools, under those names. There is no `docket` program: no " +
        "such command exists on this machine, so never run `docket` in a shell, and never try " +
        "to reach the docket MCP server yourself over HTTP or with curl. If a docket MCP tool " +
        "is missing or errors, report that with docket__report_result instead of working around it.";

    private const string WorkerPrompt =
        "You are a Docket worker agent. Your FIRST action must be to call the docket__get_task " +
        "tool to read your assignment. The assignment's description tells you the exact string " +
        "to report. Your ONLY other action is to call the docket__report_result tool once, with " +
        "that exact string as resultReference. Do not write files, do not explain, do not ask " +
        "questions. Two tool calls total: docket__get_task, then docket__report_result." +
        McpToolsRule;

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The Codex/OpenCode contrast: a <c>grok -p</c> worker under the dead-man pipe
    /// <b>still takes a turn</b> (session ref arrives). Isolation (`sleep | grok`) then
    /// waits for stdin EOF before exiting; under docketd the process has been observed
    /// to exit after the turn. <c>stdin: closed</c> is still the profile default so we
    /// do not depend on that, and so <c>stop.mode: signal</c> stays legal.
    /// </summary>
    [SkippableFact]
    public async Task A_grok_worker_under_deadman_still_takes_a_turn()
    {
        var grokBin = RequireRealGrok();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: GrokWorkerSpawn(grokBin, WorkerPrompt),
            terminalEvents: true,
            stdin: StdinPolicy.Deadman);
        await rig.StartAsync(ct);
        using var home = GrokHome.Create(rig.McpUrl);
        await rig.AddMachineAsync("A");

        var task = await rig.CreateTaskAsync(EchoDescription("A", NewToken()), ct);
        await rig.DispatchToAsync("A", ct);

        Assert.True(
            await FleetRig.WaitUntilAsync(
                async () => await rig.HarnessSessionRefAsync(task, ct) is { Length: > 0 },
                TimeSpan.FromMinutes(3)),
            "the grok worker never reported a session ref under deadman — that would make it "
            + "Codex-shaped (blocked before the first turn). Re-read grok -p stdin handling.\n"
            + GrokFailureHypotheses() + await rig.RealWorkerDiagnosticsAsync(task, ct));
    }

    [SkippableFact]
    public async Task Real_grok_worker_drives_a_task_to_verifying_on_the_fleet()
    {
        var grokBin = RequireRealGrok();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: GrokWorkerSpawn(grokBin, WorkerPrompt),
            terminalEvents: true,
            stdin: GrokStdin);
        await rig.StartAsync(ct);
        using var home = GrokHome.Create(rig.McpUrl);
        await rig.AddMachineAsync("A");
        await rig.AddMachineAsync("B");

        var token = NewToken();
        var task = await rig.CreateTaskAsync(EchoDescription("A", token), ct);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            "the real grok worker never drove its task to verifying.\n"
            + GrokFailureHypotheses() + await rig.RealWorkerDiagnosticsAsync(task, ct));

        Assert.Contains(token, await rig.ResultReferenceAsync(task, ct));
        Assert.Equal("A", rig.MachineRanOn(task));
        Assert.False(
            string.IsNullOrWhiteSpace(await rig.HarnessSessionRefAsync(task, ct)),
            "no harness session ref — streaming-messages-json init/session_id did not map.\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));
    }

    [SkippableFact]
    public async Task A_real_grok_worker_reports_its_own_tokens_and_cost_on_the_stream()
    {
        var grokBin = RequireRealGrok();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;

        await using var rig = new FleetRig(
            pg,
            spawnArgv: GrokWorkerSpawn(grokBin, WorkerPrompt),
            terminalEvents: true,
            stdin: GrokStdin);
        await rig.StartAsync(ct);
        using var home = GrokHome.Create(rig.McpUrl);
        await rig.AddMachineAsync("A");

        var token = NewToken();
        var task = await rig.CreateTaskAsync(EchoDescription("A", token), ct);

        Assert.True(
            await rig.DispatchUntilVerifyingAsync(task, "A", MaxAttempts, PerLegBudget, ct),
            "no verifying task, so no usage to assert on.\n"
            + GrokFailureHypotheses() + await rig.RealWorkerDiagnosticsAsync(task, ct));

        Assert.True(
            await FleetRig.WaitUntilAsync(
                async () => await rig.ReportedUsageAsync(task, ct) is [{ CostUsd: not null }, ..],
                TimeSpan.FromMinutes(2)),
            "no usage report with a cost reached the plane. Check that the profile uses "
            + "--output-format streaming-messages-json (not streaming-json).\n"
            + await rig.RealWorkerDiagnosticsAsync(task, ct));
    }

    private static string[] GrokWorkerSpawn(string grokBin, string prompt) =>
    [
        grokBin, "-p", prompt,
        "--output-format", "streaming-messages-json",
        "--always-approve",
        "--no-auto-update",
        "--max-turns", "8",
        "-m", GrokModel,
    ];

    private static string GrokModel =>
        Environment.GetEnvironmentVariable("DOCKET_GROK_MODEL") is { Length: > 0 } m
            ? m
            : "grok-4.6";

    private const StdinPolicy GrokStdin = StdinPolicy.Closed;

    /// <summary>
    /// Isolated <c>GROK_HOME</c> so the test does not write into the operator's
    /// <c>~/.grok</c>. Holds the static MCP server that replaces <c>--mcp-config</c>
    /// (Grok has none). Bearer is <c>${DOCKET_WORKER_TOKEN}</c>, expanded at load.
    /// </summary>
    private sealed class GrokHome : IDisposable
    {
        private readonly string _dir;
        private readonly string? _previous;

        private GrokHome(string dir, string? previous)
        {
            _dir = dir;
            _previous = previous;
        }

        public static GrokHome Create(string mcpUrl)
        {
            var previous = Environment.GetEnvironmentVariable("GROK_HOME");
            var dir = Path.Combine(Path.GetTempPath(), "docket-grok-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "config.toml"),
                $$"""
                 [mcp_servers.docket]
                 url = "{{mcpUrl.TrimEnd('/')}}"
                 enabled = true
                 headers = { "Authorization" = "Bearer ${DOCKET_WORKER_TOKEN}" }
                 """);
            Environment.SetEnvironmentVariable("GROK_HOME", dir);
            return new GrokHome(dir, previous);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GROK_HOME", _previous);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string EchoDescription(string label, string token) =>
        $"""
         Call report_result exactly once, with its resultReference set to this exact string
         (no quotes, no other text):

         {label}:{token}
         """;

    private string RequireRealGrok()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var key = FirstNonEmpty("XAI_API_KEY", "XAI_KEY");
        var optedIn = Environment.GetEnvironmentVariable("DOCKET_REAL_GROK") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(string.IsNullOrWhiteSpace(key) && !optedIn,
            "no XAI_API_KEY/XAI_KEY and no DOCKET_REAL_GROK — the real grok E2E is opt-in");
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable("XAI_API_KEY", key);

        var bin = ResolveBin("grok", "DOCKET_GROK_BIN");
        Skip.If(bin is null, "grok CLI not found (set DOCKET_GROK_BIN or put grok on PATH)");
        return bin!;
    }

    private static string? FirstNonEmpty(params string[] names)
    {
        foreach (var name in names)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v))
                return v;
        return null;
    }

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

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var fallback in new[]
                 {
                     $"/usr/local/bin/{name}",
                     $"/opt/homebrew/bin/{name}",
                     Path.Combine(home, ".grok", "bin", exe),
                 })
            if (File.Exists(fallback)) return fallback;

        return null;
    }

    private static string GrokFailureHypotheses() =>
        $$"""

         Suspect, in order:
           1. MODEL SLUG. This tier pins '{{GrokModel}}'. Override with DOCKET_GROK_MODEL.
           2. MCP WIRING. Bearer is ${DOCKET_WORKER_TOKEN} in GROK_HOME/config.toml.
              An unset variable becomes an empty Bearer and the plane 401s; the worker then
              invents a docket CLI. Confirm GROK_HOME points at the test config.
           3. TOOL NAMES. Grok spells MCP tools <server>__<tool>, so docket__get_task, NOT
              mcp__docket__get_task and NOT docket_get_task.
           4. STDIN. A deadman profile starts then never exits. This file's closed facts are
              the working path.
           5. AUTH. XAI_API_KEY (not XAI_KEY) must be in the environment, or grok login
              under the same GROK_HOME.

         """;

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];
}
