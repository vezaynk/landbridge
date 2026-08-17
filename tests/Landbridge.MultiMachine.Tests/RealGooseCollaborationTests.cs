using Landbridge.ControlPlane.Tests;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// §10 BYO-harness: Goose (<c>goose acp</c>). Opt-in and token-spending, gated like
/// the other real tiers. The <c>real-goose-e2e</c> dispatch cell runs this bar
/// and the ACP-bridge facts under the same <see cref="RealGoose"/> trait.
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
    public Task Real_worker_drives_a_task_to_verifying_on_the_fleet() =>
        RealHarnessBar.DriveToVerifyingAsync(pg, RealHarnessProfiles.Goose(RequireRealGoose()));

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public Task Real_worker_reports_usage_the_harness_emits() =>
        RealHarnessBar.ReportsUsageAsync(pg, RealHarnessProfiles.Goose(RequireRealGoose()));

    [SkippableFact(Timeout = RealHarnessBar.TwoLegTimeoutMs)]
    public Task Real_worker_resumes_its_transcript_after_a_park_and_reports_a_memory_only_nonce() =>
        RealHarnessBar.ResumesAfterParkAsync(pg, RealHarnessProfiles.Goose(RequireRealGoose()));

    private string RequireRealGoose()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var optedIn = Environment.GetEnvironmentVariable("LANDBRIDGE_REAL_GOOSE") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(!optedIn,
            "no LANDBRIDGE_REAL_GOOSE — the real goose E2E is opt-in");

        var bin = RealHarnessProfiles.ResolveBin("goose", "LANDBRIDGE_GOOSE_BIN");
        Skip.If(bin is null, "goose CLI not found (set LANDBRIDGE_GOOSE_BIN or put goose on PATH)");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOSE_PROVIDER")))
            Environment.SetEnvironmentVariable("GOOSE_PROVIDER", "anthropic");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOSE_MODEL")))
            Environment.SetEnvironmentVariable("GOOSE_MODEL", "claude-haiku-4-5-20251001");

        return bin!;
    }
}
