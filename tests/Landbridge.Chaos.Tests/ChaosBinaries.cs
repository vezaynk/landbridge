namespace Docket.Chaos.Tests;

/// <summary>
/// Resolves the REAL binaries this suite spawns. Each is resolved from its OWN build
/// output rather than the copy beside this test assembly, which is the established
/// idiom here (WalkingSkeletonEndToEndTests, FleetRig, DocketdStrayReapEndToEndTests)
/// and not merely a preference: a copy beside a test carries only what the test's own
/// closure needed, so an apphost copied here can be missing dependencies it has in its
/// own bin and fail to start. The build-only ProjectReferences in the csproj are what
/// guarantee all four exist by the time a test runs.
/// </summary>
internal static class ChaosBinaries
{
    /// <summary>The control plane / MCP host (<c>Docket.Mcp</c>).</summary>
    public static string ControlPlane() => FromSrc("Docket.Mcp", "Docket.Mcp");

    /// <summary>docketd — note <c>Docket.Runner</c>'s AssemblyName is <c>docketd</c>.</summary>
    public static string Docketd() => FromSrc("Docket.Runner", "docketd");

    /// <summary>The scripted no-LLM worker that reports a result and exits.</summary>
    public static string WorkerHarness() => FromTests("Docket.WorkerHarness", "Docket.WorkerHarness");

    /// <summary>
    /// The runner suite's reference harness: <c>run</c> is this suite's wedged worker,
    /// <c>spawn-child</c> its planted stray tree.
    /// </summary>
    public static string RunnerTestHarness() =>
        FromTests("Docket.Runner.TestHarness", "Docket.Runner.TestHarness");

    private static string FromSrc(string project, string apphostName) =>
        Resolve(Path.Combine("src", project, "bin"), apphostName, project);

    private static string FromTests(string project, string apphostName) =>
        Resolve(Path.Combine("tests", project, "bin"), apphostName, project);

    /// <summary>
    /// Swaps this test project's <c>bin</c> segment for the target project's, keeping
    /// the configuration/TFM tail — so a Debug run finds Debug binaries and a Release
    /// run finds Release ones without either being named here.
    /// </summary>
    private static string Resolve(string binSegment, string apphostName, string project)
    {
        var testDir = Path.GetDirectoryName(typeof(ChaosBinaries).Assembly.Location)!;
        var ownSegment = Path.Combine("tests", "Docket.Chaos.Tests", "bin");
        if (!testDir.Contains(ownSegment, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"cannot locate {project}: this assembly is not running from {ownSegment} (at {testDir})");

        var dir = testDir.Replace(ownSegment, binSegment, StringComparison.Ordinal);
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? apphostName + ".exe" : apphostName);
        return File.Exists(apphost)
            ? apphost
            : throw new FileNotFoundException(
                $"{project}'s apphost not found at {apphost}; is {project} built?");
    }
}
