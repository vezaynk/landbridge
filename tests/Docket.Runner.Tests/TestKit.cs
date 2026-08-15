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

    /// <summary>
    /// Reads all lines from a file that may be open for writing right now — a LIVE
    /// transcript. Opens with <see cref="FileShare.ReadWrite"/> so it tolerates the
    /// writer's exclusive <see cref="FileAccess.Write"/> handle; a plain
    /// <see cref="File.ReadAllLines(string)"/> uses <see cref="FileShare.Read"/>, which
    /// Windows refuses against an open writer (Unix does not enforce it). This is what
    /// pins live-tailing of a running worker's transcript as a product behavior.
    /// </summary>
    public static string[] ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
            lines.Add(line);
        return lines.ToArray();
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
    /// (optionally with a mapping) to exercise the stdout event drain. Pass
    /// <paramref name="capture"/> to turn on §12 transcript capture (with an optional
    /// <paramref name="maxBytes"/> cap) — the supervisor still needs a
    /// <see cref="TranscriptStore"/> for anything to be written. Pass
    /// <paramref name="telemetry"/> to opt the profile into §10 harness telemetry;
    /// the default is the off-by-default block every other fixture wants. Pass
    /// <paramref name="stdin"/> to spawn without the held-open dead-man pipe (§10) — the
    /// default is <see cref="StdinPolicy.Deadman"/>, which every fixture but the
    /// stdin-policy tests wants, since the harness modes watch stdin for their
    /// lifetime.</summary>
    public static ProfileConfig Profile(
        string harnessMode,
        StopMode stopMode = StopMode.Signal,
        string name = "default",
        EventsSource events = EventsSource.None,
        IReadOnlyDictionary<string, string>? mapping = null,
        TimeSpan? windDown = null,
        bool capture = false,
        long? maxBytes = null,
        int pruneAfterDays = TranscriptDefaults.PruneAfterDays,
        TelemetryConfig? telemetry = null,
        StdinPolicy stdin = StdinPolicy.Deadman,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyList<ProfileFile>? files = null,
        ProfileHooks? hooks = null) =>
        new(
            name,
            [HarnessPath(), "acp", harnessMode],
            new StopConfig(stopMode, MessageTemplate: null, WindDown: windDown ?? TimeSpan.FromSeconds(30)),
            Resume: null,
            new EventsConfig(events, mapping ?? new Dictionary<string, string>()),
            telemetry ?? new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(Capture: capture,
                MaxBytes: maxBytes ?? TranscriptDefaults.MaxBytes, PruneAfterDays: pruneAfterDays),
            MaxConcurrent: null,
            Processes: null,
            Stdin: stdin,
            Env: env,
            Files: files,
            Hooks: hooks);

    /// <summary>
    /// A §11 resume profile: a cold <c>Spawn</c> (the given <paramref name="coldMode"/>)
    /// and a <c>Resume</c> argv that re-runs the harness in <c>echo-argv</c> mode with
    /// the <c>{session_id}</c>/<c>{mcp_config}</c> placeholders the supervisor
    /// substitutes when a dispatch carries a resume session ref. Both modes write the
    /// argv they received, so a test can read the marker either way.
    /// </summary>
    public static ProfileConfig ResumeProfile(
        string coldMode = "echo-argv",
        string name = "default",
        StdinPolicy stdin = StdinPolicy.Deadman) =>
        new(
            name,
            [HarnessPath(), "acp", coldMode],
            new StopConfig(StopMode.Signal, MessageTemplate: null, WindDown: TimeSpan.FromSeconds(30)),
            new ResumeConfig([HarnessPath(), "acp", "echo-argv"]),
            new EventsConfig(EventsSource.None, new Dictionary<string, string>()),
            new TelemetryConfig(Otel: false, Endpoint: null),
            new LogsConfig(),
            MaxConcurrent: null,
            Processes: null,
            Stdin: stdin);
}

/// <summary>A back-pressure reader the tests flip to simulate load (§10 concurrency).</summary>
internal sealed class FakeLoadReader : ISystemLoadReader
{
    public SystemLoad Load { get; set; } = new(0, 0, 0);

    /// <summary>Whether CPU is a real reading; flip false to model a reader that cannot observe it.</summary>
    public bool ObservesCpu { get; set; } = true;

    public SystemLoad Read() => Load;
}

/// <summary>
/// A CPU sampler that hands back a scripted sequence of snapshots so
/// <see cref="SystemLoadReader"/>'s delta math is exercised without a real
/// syscall. <see cref="SystemLoadReader.Read"/> samples twice per call, so queue
/// two snapshots per read. An empty queue models a failed sample (returns false).
/// </summary>
internal sealed class FakeCpuSampler : ICpuSampler
{
    private readonly Queue<CpuSample> _samples;

    public FakeCpuSampler(params CpuSample[] samples) => _samples = new Queue<CpuSample>(samples);

    public bool TrySample(out CpuSample sample)
    {
        if (_samples.Count == 0)
        {
            sample = default;
            return false;
        }
        sample = _samples.Dequeue();
        return true;
    }
}

/// <summary>An inventory the tests seed with known pids — the discovery half of
/// stray cleanup, so the portable kill half (StrayReaper) is exercised for real.</summary>
internal sealed class FakeProcessInventory(params TaggedProcess[] processes) : IProcessInventory
{
    public IReadOnlyList<TaggedProcess> ListDocketProcesses() => processes;
}
