using Landbridge.ControlPlane.Tests;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// §10 BYO-harness: Goose (<c>goose acp</c>). Opt-in and token-spending, gated like
/// the other real tiers: skip unless Postgres is up, <c>goose</c> is on PATH, and
/// either an Anthropic key is present (Goose defaults to that provider) or
/// <c>LANDBRIDGE_REAL_GOOSE=1</c> on a machine already configured. The
/// <c>real-goose-e2e</c> dispatch cell runs this bar and the ACP-bridge facts
/// under the same <see cref="RealGoose"/> trait.
/// </summary>
[Trait("Category", RealGoose)]
[Collection(PostgresCollection.Name)]
public sealed class RealGooseCollaborationTests(PostgresFixture pg) : IAsyncLifetime
{
    public const string RealGoose = "RealGoose";

    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_reports_on_the_fleet() =>
        RealHarnessBar.DriveToReportAsync(pg, RealHarnessProfiles.Goose(RequireRealGoose()));

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_reports_usage_the_harness_emits() =>
        RealHarnessBar.ReportsUsageAsync(pg, RealHarnessProfiles.Goose(RequireRealGoose()));

    [SkippableFact(Timeout = RealHarnessBar.TwoLegTimeoutMs)]
    public Task Real_worker_resumes_its_transcript_after_a_park_and_reports_a_memory_only_nonce() =>
        RealHarnessBar.ResumesAfterParkAsync(pg, RealHarnessProfiles.Goose(RequireRealGoose()));

    private string RequireRealGoose()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var key = RealHarnessProfiles.FirstNonEmpty("ANTHROPIC_API_KEY", "ANTHROPIC_KEY");
        var optedIn = RealHarnessProfiles.EnvFlag("LANDBRIDGE_REAL_GOOSE");
        Skip.If(string.IsNullOrWhiteSpace(key) && !optedIn,
            "no ANTHROPIC_API_KEY/ANTHROPIC_KEY and no LANDBRIDGE_REAL_GOOSE — the real goose E2E is opt-in");
        if (!string.IsNullOrWhiteSpace(key))
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", key);

        var bin = RealHarnessProfiles.ResolveBin("goose", "LANDBRIDGE_GOOSE_BIN");
        Skip.If(bin is null, "goose CLI not found (set LANDBRIDGE_GOOSE_BIN or put goose on PATH)");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOSE_PROVIDER")))
            Environment.SetEnvironmentVariable("GOOSE_PROVIDER", "anthropic");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOSE_MODEL")))
            Environment.SetEnvironmentVariable("GOOSE_MODEL", "claude-haiku-4-5-20251001");

        return bin!;
    }
}
