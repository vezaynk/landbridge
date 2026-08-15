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
/// <para>The portable bar (verifying + session ref, usage/cost, park → resume via
/// <c>--resume</c>) is <see cref="RealHarnessBar"/>, wrapped below so
/// <c>Category=RealGrok</c> still isolates the job. Dead-man-still-takes-a-turn stays
/// here — it is the Codex/OpenCode contrast, not a portable claim.</para>
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

    [SkippableFact]
    public Task Real_worker_drives_a_task_to_verifying_on_the_fleet() =>
        RealHarnessBar.DriveToVerifyingAsync(pg, RealHarnessProfiles.Grok(RequireRealGrok()));

    [SkippableFact]
    public Task Real_worker_reports_usage_the_harness_emits() =>
        RealHarnessBar.ReportsUsageAsync(pg, RealHarnessProfiles.Grok(RequireRealGrok()));

    [SkippableFact]
    public Task Real_worker_resumes_its_transcript_after_a_park_and_reports_a_memory_only_nonce() =>
        RealHarnessBar.ResumesAfterParkAsync(pg, RealHarnessProfiles.Grok(RequireRealGrok()));


    private static string[] GrokWorkerSpawn(string grokBin, string prompt) =>
        RealHarnessProfiles.GrokSpawn(grokBin, prompt);

    private static string GrokModel => RealHarnessProfiles.GrokModel;


    /// <summary>
    /// Project-local Grok MCP file (#112 G2). Grok merges
    /// <c>{cwd}/.grok/config.toml</c> with <c>~/.grok</c>, so this keeps operator
    /// auth/skills/MCPs and does not set <c>GROK_HOME</c>. Both <c>{mcp_url}</c> and
    /// <c>{worker_token}</c> are docketd file-substitutions (§13), written verbatim,
    /// so the bearer is a concrete token grok sends as-is — <em>not</em> a
    /// <c>${ENV}</c> reference grok would have to expand itself (it does not, so the
    /// earlier form produced an empty Bearer and a 401). The file therefore carries
    /// the token and is written owner-only (0600), the same posture as Claude's
    /// <c>mcp.json</c>.
    /// </summary>
    private static readonly ProfileFile[] GrokMcpFile = RealHarnessProfiles.GrokMcpFile;

    private static string EchoDescription(string label, string token) =>
        RealHarnessProfiles.EchoDescription(label, token);

    private string RequireRealGrok()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var key = RealHarnessProfiles.FirstNonEmpty("XAI_API_KEY", "XAI_KEY");
        var optedIn = Environment.GetEnvironmentVariable("DOCKET_REAL_GROK") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(string.IsNullOrWhiteSpace(key) && !optedIn,
            "no XAI_API_KEY/XAI_KEY and no DOCKET_REAL_GROK — the real grok E2E is opt-in");
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable("XAI_API_KEY", key);

        var bin = RealHarnessProfiles.ResolveBin("grok", "DOCKET_GROK_BIN");
        Skip.If(bin is null, "grok CLI not found (set DOCKET_GROK_BIN or put grok on PATH)");
        return bin!;
    }

    private static string GrokFailureHypotheses() =>
        $$"""

         Suspect, in order:
           1. MODEL SLUG. This tier pins '{{GrokModel}}'. Override with DOCKET_GROK_MODEL.
           2. MCP WIRING. files[] writes {work_dir}/.grok/config.toml with {mcp_url}
              and Bearer {worker_token}, both docketd file-substitutions written
              verbatim (grok does NOT expand ${ENV} in config.toml). A 401 here means
              the minted token or url is wrong; a stale docket CLI means grok never
              loaded the file at all.
           3. TOOL NAMES. Grok spells MCP tools <server>__<tool>, so docket__get_task, NOT
              mcp__docket__get_task and NOT docket_get_task.
           4. STDIN. A deadman profile starts then never exits. This file's closed facts are
              the working path.
           5. AUTH. XAI_API_KEY (not XAI_KEY) must be in the environment, or grok login
              under ~/.grok (this tier does not set GROK_HOME).

         """;

    private static string NewToken() => RealHarnessProfiles.NewToken();
}
