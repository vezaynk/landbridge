using System.Diagnostics;
using Docket.Core;

namespace Docket.Runner.Tests;

/// <summary>Shared helpers: locating the harness binary, temp roots, and polling.</summary>
internal static class TestKit
{
    /// <summary>
    /// The apphost path of the built <c>Docket.Runner.TestHarness</c>, spawned
    /// directly (argv, no shell — §10). Spawning the native apphost rather than
    /// <c>dotnet exec</c> keeps <c>Environment.ProcessPath</c> pointing at the
    /// harness, so its own grandchild-spawn re-executes the harness.
    /// </summary>
    public static string HarnessPath()
    {
        var dll = typeof(Docket.Runner.TestHarness.Program).Assembly.Location;
        var dir = Path.GetDirectoryName(dll)!;
        var stem = Path.GetFileNameWithoutExtension(dll);
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? stem + ".exe" : stem);
        return File.Exists(apphost) ? apphost : dll; // dll fallback (would need dotnet)
    }

    public static string NewWorkRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "docketd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteRoot(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Polls a condition up to a timeout — for observing real process state transitions.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    public static bool PidAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static DispatchCommand Dispatch(TaskId task, string profile = "default") =>
        new(task, profile);

    public static MachineConfig Machine(string workRoot) =>
        new(workRoot, TimeSpan.FromSeconds(15), BackPressureThresholds.Default);

    /// <summary>A profile that runs the test harness in the given mode. Defaults to
    /// the honest <see cref="EventsSource.None"/>; pass <see cref="EventsSource.Terminal"/>
    /// (optionally with a mapping) to exercise the stdout event drain.</summary>
    public static ProfileConfig Profile(
        string harnessMode,
        StopMode stopMode = StopMode.Signal,
        string name = "default",
        EventsSource events = EventsSource.None,
        IReadOnlyDictionary<string, string>? mapping = null) =>
        new(
            name,
            [HarnessPath(), harnessMode],
            new StopConfig(stopMode, Signal: null, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(30)),
            Resume: null,
            new EventsConfig(events, mapping ?? new Dictionary<string, string>()),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(Path: null, Format: null),
            MaxConcurrent: null);
}

/// <summary>A back-pressure reader the tests flip to simulate load (§10 concurrency).</summary>
internal sealed class FakeLoadReader : ISystemLoadReader
{
    public SystemLoad Load { get; set; } = new(0, 0, 0);
    public SystemLoad Read() => Load;
}

/// <summary>An inventory the tests seed with known pids — the discovery half of
/// stray cleanup, so the portable kill half (StrayReaper) is exercised for real.</summary>
internal sealed class FakeProcessInventory(params TaggedProcess[] processes) : IProcessInventory
{
    public IReadOnlyList<TaggedProcess> ListDocketProcesses() => processes;
}
