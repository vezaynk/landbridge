using Docket.Contracts;
using Docket.ControlPlane.Tests;
using Docket.Core;
using Docket.Runner;

namespace Docket.MultiMachine.Tests;

/// <summary>
/// §10 BYO-harness, fourth harness: Grok Build (<c>grok agent stdio</c>). Opt-in and
/// token-spending, gated like the other real tiers.
///
/// <para>The portable bar — verifying + session ref, usage/cost, park → resume via
/// <c>session/load</c> — lives in <see cref="RealHarnessBar"/>.</para>
/// </summary>
[Trait("Category", RealGrok)]
[Collection(PostgresCollection.Name)]
public sealed class RealGrokCollaborationTests(PostgresFixture pg) : IAsyncLifetime
{
    public const string RealGrok = "RealGrok";

    private const string McpToolsRule =
        " Docket's tools are MCP tools, named exactly docket__get_session, docket__report_result " +
        "and so on — call them as tools, under those names. There is no `docket` program: no " +
        "such command exists on this machine, so never run `docket` in a shell, and never try " +
        "to reach the docket MCP server yourself over HTTP or with curl. If a docket MCP tool " +
        "is missing or errors, report that with docket__report_result instead of working around it.";

    private const string WorkerPrompt =
        "You are a Docket worker agent. Your FIRST action must be to call the docket__get_session " +
        "tool to read your assignment. The assignment's description tells you the exact string " +
        "to report. Your ONLY other action is to call the docket__report_result tool once, with " +
        "that exact string as resultReference. Do not write files, do not explain, do not ask " +
        "questions. Two tool calls total: docket__get_session, then docket__report_result." +
        McpToolsRule;

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_drives_a_task_to_verifying_on_the_fleet() =>
        RealHarnessBar.DriveToVerifyingAsync(pg, RealHarnessProfiles.Grok(RequireRealGrok()));

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_reports_usage_the_harness_emits() =>
        RealHarnessBar.ReportsUsageAsync(pg, RealHarnessProfiles.Grok(RequireRealGrok()));

    [SkippableFact(Timeout = RealHarnessBar.TwoLegTimeoutMs)]
    public Task Real_worker_resumes_its_transcript_after_a_park_and_reports_a_memory_only_nonce() =>
        RealHarnessBar.ResumesAfterParkAsync(pg, RealHarnessProfiles.Grok(RequireRealGrok()));

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
}
