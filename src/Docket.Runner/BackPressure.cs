using Docket.Contracts;

namespace Docket.Runner;

/// <summary>Reads current system load. Abstracted so tests drive pressure deterministically.</summary>
public interface ISystemLoadReader
{
    SystemLoad Read();
}

/// <summary>
/// A cross-platform best-effort reader. Disk uses <see cref="DriveInfo"/> and
/// memory uses <see cref="GC.GetGCMemoryInfo(GCKind)"/> — both portable. CPU
/// load has no portable BCL surface (getloadavg on POSIX, PDH on Windows are
/// the platform hooks), so it reports 0 and is a documented follow-up; disk
/// and memory carry the portable back-pressure signal in the meantime (§10).
/// </summary>
public sealed class PortableSystemLoadReader(string workRoot) : ISystemLoadReader
{
    public SystemLoad Read() => new(CpuLoad: 0.0, MemoryLoad: ReadMemory(), DiskUsage: ReadDisk(workRoot));

    private static double ReadMemory()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0
            ? Math.Clamp((double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes, 0, 1)
            : 0;
    }

    private static double ReadDisk(string path)
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
/// instead docketd observes its own load and <b>stops accepting dispatch when
/// under pressure</b>, resuming when it clears. This breaks the requeue
/// feedback loop — a saturated machine keeps what it holds and shows as
/// <c>saturated</c> rather than thrashing.
/// </summary>
public sealed class BackPressureMonitor(ISystemLoadReader reader, BackPressureThresholds thresholds)
{
    public BackPressureReading Evaluate()
    {
        var load = reader.Read();
        var under = load.CpuLoad > thresholds.MaxCpuLoad
            || load.MemoryLoad > thresholds.MaxMemoryLoad
            || load.DiskUsage > thresholds.MaxDiskUsage;
        return new BackPressureReading(load, under);
    }

    public bool UnderPressure() => Evaluate().UnderPressure;
}
