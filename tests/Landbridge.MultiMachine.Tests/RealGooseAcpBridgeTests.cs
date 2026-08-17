using Landbridge.ControlPlane.Tests;

namespace Landbridge.MultiMachine.Tests;

/// <summary>
/// Paid e2e of the ACP bridge against a real Goose: listen wraps
/// <c>goose acp</c>, landbridged's profile spawn is <c>landbridge-acp-bridge connect</c>.
/// Same opt-in as <see cref="RealGooseCollaborationTests"/> — not a CI cell.
/// </summary>
[Trait("Category", RealGooseCollaborationTests.RealGoose)]
[Collection(PostgresCollection.Name)]
public sealed class RealGooseAcpBridgeTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (pg.Available) await pg.ResetAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public async Task Real_goose_reaches_verifying_through_the_acp_bridge()
    {
        using var opened = OpenBridgedGoose();
        await RealHarnessBar.DriveToVerifyingAsync(pg, opened.Profile);
    }

    [SkippableFact(Timeout = RealHarnessBar.EchoTimeoutMs)]
    public async Task Real_goose_reports_usage_through_the_acp_bridge()
    {
        using var opened = OpenBridgedGoose();
        await RealHarnessBar.ReportsUsageAsync(pg, opened.Profile);
    }

    [SkippableFact(Timeout = RealHarnessBar.TwoLegTimeoutMs)]
    public async Task Real_goose_resumes_after_park_through_the_acp_bridge()
    {
        using var opened = OpenBridgedGoose();
        await RealHarnessBar.ResumesAfterParkAsync(pg, opened.Profile);
    }

    private BridgedGoose OpenBridgedGoose()
    {
        var goose = RequireRealGoose();
        var far = AcpBridgeFarSide.Start(AcpBridgeFarSide.BridgePath(), [goose, "acp"]);
        var profile = RealHarnessProfiles.GooseViaBridge(goose, AcpBridgeFarSide.BridgePath(), far.Url);
        return new BridgedGoose(far, profile);
    }

    private sealed class BridgedGoose(AcpBridgeFarSide far, RealHarnessProfile profile) : IDisposable
    {
        public RealHarnessProfile Profile { get; } = profile;
        public void Dispose() => far.Dispose();
    }

    private string RequireRealGoose()
    {
        Skip.IfNot(pg.Available, pg.SkipReason);

        var optedIn = Environment.GetEnvironmentVariable("LANDBRIDGE_REAL_GOOSE") is { Length: > 0 } o
                      && !o.Equals("0", StringComparison.Ordinal)
                      && !o.Equals("false", StringComparison.OrdinalIgnoreCase);

        Skip.If(!optedIn, "no LANDBRIDGE_REAL_GOOSE — the real goose E2E is opt-in");

        var bin = RealHarnessProfiles.ResolveBin("goose", "LANDBRIDGE_GOOSE_BIN");
        Skip.If(bin is null, "goose CLI not found (set LANDBRIDGE_GOOSE_BIN or put goose on PATH)");

        // listen, not connect, is what spawns goose — so the provider must be
        // on this process (the far side inherits it). goose configure is
        // interactive; env is the unattended path.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOSE_PROVIDER")))
            Environment.SetEnvironmentVariable("GOOSE_PROVIDER", "anthropic");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOSE_MODEL")))
            Environment.SetEnvironmentVariable("GOOSE_MODEL", "claude-haiku-4-5-20251001");

        return bin!;
    }
}
