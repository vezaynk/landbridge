using Landbridge.Contracts;

namespace Landbridge.Runner;

/// <summary>Reads current system load. Abstracted so tests drive pressure deterministically.</summary>
public interface ISystemLoadReader
{
    SystemLoad Read();

    /// <summary>
    /// Whether <see cref="Read"/>'s <see cref="SystemLoad.CpuLoad"/> is a real
    /// measurement. When <c>false</c> the CpuLoad field is a placeholder, and
    /// <see cref="BackPressureMonitor"/> excludes CPU from the verdict so a
    /// non-reading is never mistaken for a healthy 0% that silently defeats
    /// <c>max_cpu_load</c> (§10).
    /// </summary>
    bool ObservesCpu { get; }
}

/// <summary>
/// The portable <b>fallback</b> reader for platforms with no CPU backend. Disk
/// uses <see cref="DriveInfo"/> and memory uses
/// <see cref="GC.GetGCMemoryInfo(GCKind)"/> — both portable. CPU load has no
/// portable BCL surface (each OS needs its own P/Invoke), so this reader does
/// <b>not</b> observe it: <see cref="ObservesCpu"/> is <c>false</c> and the
/// CpuLoad field is a placeholder <c>0</c>, not a reading.
/// <see cref="BackPressureMonitor"/> consults <see cref="ObservesCpu"/> and drops
/// the CPU term rather than letting that <c>0</c> silently defeat
/// <c>max_cpu_load</c>; disk and memory carry the back-pressure signal here.
/// <para>Linux, macOS, and Windows instead get <see cref="SystemLoadReader"/>
/// (<see cref="ISystemLoadReader.ObservesCpu"/> <c>true</c>), which reads real
/// host CPU utilization; see <see cref="SystemLoadReader.ForCurrentPlatform"/>.</para>
/// </summary>
public sealed class PortableSystemLoadReader(string workRoot) : ISystemLoadReader
{
    public bool ObservesCpu => false;

    public SystemLoad Read() => new(CpuLoad: 0.0, MemoryLoad: ReadMemory(), DiskUsage: ReadDisk(workRoot));

    // Memory and disk are portable and platform-independent, so the real per-OS
    // reader (SystemLoadReader) reuses them verbatim and layers only a genuine CPU
    // read on top. They live here as the single home for the portable primitives.
    internal static double ReadMemory()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0
            ? Math.Clamp((double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes, 0, 1)
            : 0;
    }

    internal static double ReadDisk(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return 0;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
                return 0;
            return Math.Clamp(1.0 - ((double)drive.AvailableFreeSpace / drive.TotalSize), 0, 1);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>The evaluated back-pressure state: the load reading plus the verdict.</summary>
public readonly record struct BackPressureReading(SystemLoad Load, bool UnderPressure);

/// <summary>
/// §10 concurrency and back-pressure. Machines declare no concurrency limit;
/// instead landbridged observes its own load and <b>stops accepting dispatch when
/// under pressure</b>, resuming when it clears. This breaks the requeue
/// feedback loop — a saturated machine keeps what it holds and shows as
/// <c>saturated</c> rather than thrashing.
/// </summary>
public sealed class BackPressureMonitor(ISystemLoadReader reader, BackPressureThresholds thresholds)
{
    /// <summary>
    /// Whether the wired reader actually measures CPU. When false the CPU term is
    /// excluded from <see cref="Evaluate"/> and <c>max_cpu_load</c> is inert — an
    /// honest no-op the daemon surfaces at startup rather than a silent bypass.
    /// </summary>
    public bool ObservesCpu => reader.ObservesCpu;

    public BackPressureReading Evaluate()
    {
        var load = reader.Read();
        // CPU only participates when the reader observes it; a placeholder reading
        // (PortableSystemLoadReader today) must not be treated as a real 0% busy
        // that silently defeats max_cpu_load — memory and disk still gate.
        var cpuUnderPressure = reader.ObservesCpu && load.CpuLoad > thresholds.MaxCpuLoad;
        var under = cpuUnderPressure
            || load.MemoryLoad > thresholds.MaxMemoryLoad
            || load.DiskUsage > thresholds.MaxDiskUsage;
        return new BackPressureReading(load, under);
    }

    public bool UnderPressure() => Evaluate().UnderPressure;
}
