using System.Diagnostics;
using System.Text.Json;

namespace Landbridge.Runner.Tests;

/// <summary>
/// The §10 restart-is-reboot guarantee, end to end against the REAL <c>landbridged</c>
/// binary — not the in-process supervisor seam the unit tests use. A previous
/// landbridged incarnation is simulated by planting a tagged harness process (plus
/// its grandchild, which inherits the env tag); then the actual daemon is
/// started with the same machine id and must discover and kill both BEFORE
/// announcing itself ready — the cleanup that survives a SIGKILLed daemon,
/// exercised with the real per-OS <see cref="IProcessInventory"/> inside the
/// real process (ProcFs on Linux, KERN_PROCARGS2 on macOS).
///
/// Windows is skipped, not failed: discovery there is a documented deferral
/// (<see cref="NullProcessInventory"/>) until Job-Object containment lands —
/// at which point Windows gets its own, stronger test (the OS kills the tree
/// at daemon death; no restart sweep needed).
/// </summary>
public sealed class LandbridgedStrayReapEndToEndTests
{
    [SkippableFact]
    public async Task Restarted_landbridged_reaps_a_previous_incarnations_strays_before_accepting_dispatch()
    {
        Skip.If(OperatingSystem.IsWindows(),
            "stray discovery on Windows is a documented deferral (Job-Object containment pending)");

        var machineId = $"e2e-reap-{Guid.NewGuid():N}";
        var workRoot = TestKit.NewWorkRoot();
        Process? stray = null;
        Process? landbridged = null;
        var grandchildPid = 0;

        try
        {
            // ── 1. The "previous incarnation's" leftovers: a tagged harness that
            //       itself spawned a grandchild (which inherits the env tag, as any
            //       real agent-spawned dev server would).
            var strayDir = Path.Combine(workRoot, "stray");
            Directory.CreateDirectory(strayDir);
            var psi = new ProcessStartInfo(TestKit.HarnessPath())
            {
                WorkingDirectory = strayDir,
                UseShellExecute = false,
                // A planted stray must SURVIVE its spawner until landbridged reaps it —
                // that is what makes it a stray. So the test holds its stdin open
                // (in CI the runner's own stdin is closed, and an inherited closed
                // stdin trips the harness's dead-man switch instantly)…
                RedirectStandardInput = true,
            };
            psi.ArgumentList.Add("spawn-child");
            psi.Environment["LANDBRIDGE_MACHINE_ID"] = machineId;
            psi.Environment["LANDBRIDGE_SESSION_ID"] = Guid.NewGuid().ToString();
            // …and disables PDEATHSIG for the planted tree: on Linux the arm is
            // keyed to the spawning THREAD (an xunit pool thread here), whose
            // retirement mid-test would kill the stray early. Real strays outlive
            // a SIGKILLed landbridged precisely because nothing fires — the reaper is
            // what must find them; pinning to no-mechanism reproduces that.
            // (Literal must match ParentDeathSignal.DisableEnvVar, internal to the
            // harness assemblies.)
            psi.Environment["LANDBRIDGE_TEST_DISABLE_PDEATHSIG"] = "1";
            stray = Process.Start(psi)!;

            var pidPath = Path.Combine(strayDir, "child.pid");
            Assert.True(await TestKit.WaitUntilAsync(() => File.Exists(pidPath), TimeSpan.FromSeconds(15)),
                "the planted stray never recorded its grandchild pid");
            grandchildPid = int.Parse(await File.ReadAllTextAsync(pidPath));
            Assert.True(await TestKit.WaitUntilAsync(
                () => TestKit.PidAlive(stray.Id) && TestKit.PidAlive(grandchildPid), TimeSpan.FromSeconds(10)),
                "the planted stray tree is not alive");

            // ── 2. The real daemon starts for the same machine. Spec §10: it must
            //       reap the strays BEFORE announcing itself — the announcement line
            //       carries the count.
            var configPath = Path.Combine(workRoot, "landbridged.json");
            await File.WriteAllTextAsync(configPath, $$"""
                {
                  "machine": { "work_root": {{JsonSerializer.Serialize(workRoot)}} },
                  "profiles": [ { "name": "default", "prompt": "go", "spawn": ["unused-no-dispatch-in-this-test"] } ]
                }
                """);

            var landbridgedPsi = new ProcessStartInfo(LandbridgedPath())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            landbridgedPsi.ArgumentList.Add("--config");
            landbridgedPsi.ArgumentList.Add(configPath);
            landbridgedPsi.ArgumentList.Add("--machine-id");
            landbridgedPsi.ArgumentList.Add(machineId);
            landbridged = Process.Start(landbridgedPsi)!;
            _ = landbridged.StandardError.ReadToEndAsync(); // drain so a full pipe can never block the daemon

            var upLine = await ReadLineMatchingAsync(landbridged, l => l.Contains("landbridged up:"), TimeSpan.FromSeconds(60));
            Assert.NotNull(upLine);

            // The grandchild dies with the stray's tree kill, so the count is 1 or
            // 2 depending on scan order; the guarantee is that everything tagged
            // with this machine id is gone.
            var reaped = ParseReaped(upLine!);
            Assert.True(reaped >= 1, $"landbridged announced strays_reaped={reaped}; expected >= 1 ({upLine})");

            Assert.True(await TestKit.WaitUntilAsync(
                () => !TestKit.PidAlive(stray.Id) && !TestKit.PidAlive(grandchildPid), TimeSpan.FromSeconds(10)),
                "the stray tree survived the daemon's restart sweep");
        }
        finally
        {
            foreach (var p in new[] { landbridged, stray })
            {
                try
                {
                    if (p is { HasExited: false })
                        p.Kill(entireProcessTree: true);
                }
                catch { /* already gone */ }
                p?.Dispose();
            }
            if (grandchildPid > 0)
            {
                try
                {
                    using var g = Process.GetProcessById(grandchildPid);
                    if (!g.HasExited) g.Kill(entireProcessTree: true);
                }
                catch { /* already gone */ }
            }
            TestKit.TryDeleteRoot(workRoot);
        }
    }

    private static int ParseReaped(string upLine)
    {
        // "landbridged up: machine=… profiles=[…] strays_reaped=N control=…"
        const string key = "strays_reaped=";
        var at = upLine.IndexOf(key, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no strays_reaped in: {upLine}");
        var rest = upLine[(at + key.Length)..];
        var end = rest.IndexOf(' ');
        return int.Parse(end >= 0 ? rest[..end] : rest);
    }

    private static async Task<string?> ReadLineMatchingAsync(Process process, Func<string, bool> match, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null)
                    return null; // stdout closed — landbridged exited early
                if (match(line))
                    return line;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The real landbridged apphost, resolved from <c>src/Landbridge.Runner</c>'s own
    /// build output (the ProjectReference guarantees it is built), mirroring how
    /// the walking-skeleton test resolves the worker harness from its sibling bin.
    /// </summary>
    private static string LandbridgedPath()
    {
        const string apphostName = "landbridged"; // Landbridge.Runner's AssemblyName
        var testDir = Path.GetDirectoryName(typeof(LandbridgedStrayReapEndToEndTests).Assembly.Location)!;
        var runnerDir = testDir.Replace(
            Path.Combine("tests", "Landbridge.Runner.Tests", "bin"),
            Path.Combine("src", "Landbridge.Runner", "bin"),
            StringComparison.Ordinal);
        var apphost = Path.Combine(runnerDir, OperatingSystem.IsWindows() ? apphostName + ".exe" : apphostName);
        return File.Exists(apphost)
            ? apphost
            : throw new FileNotFoundException($"landbridged apphost not found at {apphost}; is Landbridge.Runner built?");
    }
}
