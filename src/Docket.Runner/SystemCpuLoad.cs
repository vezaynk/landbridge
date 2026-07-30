using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Docket.Contracts;

namespace Docket.Runner;

/// <summary>
/// A point-in-time snapshot of the host's cumulative CPU-time counters, in
/// whatever monotonic unit the platform counts in (jiffies on Linux, Mach ticks
/// on macOS, 100 ns FILETIME units on Windows). Only <b>deltas</b> between two
/// snapshots are meaningful — the absolute magnitude and the unit differ per OS,
/// but <c>busyDelta / totalDelta</c> is a unit-free utilization fraction on all
/// three (see <see cref="SystemLoadReader.ComputeBusyFraction"/>).
/// </summary>
internal readonly record struct CpuSample(double Busy, double Total);

/// <summary>
/// Takes one <see cref="CpuSample"/> from the host. Per-OS behind a P/Invoke.
/// Returns <c>false</c> when the platform is wrong or the syscall fails, so the
/// reader degrades to "no reading this cycle" rather than fabricating a number.
/// </summary>
internal interface ICpuSampler
{
    bool TrySample(out CpuSample sample);
}

/// <summary>
/// §10 back-pressure: the real, cross-platform system-CPU-load reader.
/// <see cref="ISystemLoadReader.ObservesCpu"/> is <c>true</c>, so
/// <see cref="BackPressureMonitor"/> lets the CPU term participate and
/// <c>max_cpu_load</c> actually trips.
///
/// <para>It measures <b>host</b> CPU utilization, not docketd's own process CPU:
/// docketd spawns each worker as a separate process, so its own process CPU is
/// the wrong signal — a busy machine with idle-looking docketd would sail past
/// the gate. Utilization is a delta of the OS's cumulative busy/total CPU-time
/// counters across a short window (<see cref="ReadCpu"/> samples twice around a
/// brief sleep), which yields instantaneous utilization the same way on all
/// three platforms.</para>
///
/// <para>Memory and disk are portable, so they are reused verbatim from
/// <see cref="PortableSystemLoadReader"/>; only CPU is platform-specific here.</para>
/// </summary>
public sealed class SystemLoadReader : ISystemLoadReader
{
    // A short window is enough for a stable utilization fraction and keeps Read()
    // cheap on the heartbeat/dispatch paths that call it.
    private static readonly TimeSpan DefaultSampleWindow = TimeSpan.FromMilliseconds(100);

    private readonly ICpuSampler _sampler;
    private readonly string _workRoot;
    private readonly TimeSpan _sampleWindow;

    internal SystemLoadReader(ICpuSampler sampler, string workRoot, TimeSpan sampleWindow)
    {
        _sampler = sampler;
        _workRoot = workRoot;
        _sampleWindow = sampleWindow;
    }

    /// <summary>
    /// The real CPU-observing reader for Linux, macOS, and Windows; the
    /// <see cref="PortableSystemLoadReader"/> fallback (<see cref="ObservesCpu"/>
    /// <c>false</c>) on any other platform. Mirrors
    /// <see cref="ProcessInventory.ForCurrentPlatform"/>: known OSes get a real
    /// backend, everything else a documented no-op that keeps memory/disk gating.
    /// </summary>
    public static ISystemLoadReader ForCurrentPlatform(string workRoot)
    {
        ICpuSampler? sampler =
            OperatingSystem.IsLinux() ? new LinuxProcStatCpuSampler()
            : OperatingSystem.IsMacOS() ? new MacOsHostStatisticsCpuSampler()
            : OperatingSystem.IsWindows() ? new WindowsSystemTimesCpuSampler()
            : null;

        return sampler is null
            ? new PortableSystemLoadReader(workRoot)
            : new SystemLoadReader(sampler, workRoot, DefaultSampleWindow);
    }

    public bool ObservesCpu => true;

    public SystemLoad Read() => new(
        CpuLoad: ReadCpu(),
        MemoryLoad: PortableSystemLoadReader.ReadMemory(),
        DiskUsage: PortableSystemLoadReader.ReadDisk(_workRoot));

    /// <summary>
    /// Host CPU utilization over <see cref="_sampleWindow"/>: two cumulative
    /// snapshots bracketing a brief sleep, then the busy fraction between them.
    /// A failed sample (transient syscall error) yields <c>0</c> for this cycle —
    /// a single non-reading, never a fabricated load and never a stuck one.
    /// </summary>
    private double ReadCpu()
    {
        if (!_sampler.TrySample(out var first))
            return 0.0;
        if (_sampleWindow > TimeSpan.Zero)
            Thread.Sleep(_sampleWindow);
        if (!_sampler.TrySample(out var second))
            return 0.0;
        return ComputeBusyFraction(first, second);
    }

    /// <summary>
    /// Busy fraction over the interval between two cumulative snapshots, clamped
    /// to [0,1]. A non-positive total delta — no time elapsed, or a counter reset
    /// / wrap — yields <c>0</c>: no signal this cycle rather than a bogus reading.
    /// </summary>
    internal static double ComputeBusyFraction(CpuSample previous, CpuSample current)
    {
        var totalDelta = current.Total - previous.Total;
        if (totalDelta <= 0)
            return 0.0;
        var busyDelta = current.Busy - previous.Busy;
        return Math.Clamp(busyDelta / totalDelta, 0.0, 1.0);
    }
}

/// <summary>
/// Linux CPU sampler: the aggregate <c>cpu</c> line of <c>/proc/stat</c>, whose
/// fields are cumulative jiffies per state. Total is their sum; idle is
/// <c>idle + iowait</c>; busy is the rest. Pure parsing (<see cref="TryParseAggregate"/>)
/// is split from the file read so it is testable off-Linux.
/// </summary>
internal sealed class LinuxProcStatCpuSampler : ICpuSampler
{
    public bool TrySample(out CpuSample sample)
    {
        sample = default;
        if (!OperatingSystem.IsLinux())
            return false;

        string? firstLine;
        try
        {
            using var reader = new StreamReader("/proc/stat");
            firstLine = reader.ReadLine();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return firstLine is not null && TryParseAggregate(firstLine, out sample);
    }

    /// <summary>
    /// Parses the aggregate line: <c>cpu user nice system idle iowait irq softirq
    /// steal guest guest_nice</c>. The leading token must be exactly <c>cpu</c> —
    /// per-core lines (<c>cpu0</c>, <c>cpu1</c>, …) and any other line are rejected.
    /// </summary>
    internal static bool TryParseAggregate(string line, out CpuSample sample)
    {
        sample = default;
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Need at least "cpu" + through idle+iowait to form a reading.
        if (tokens.Length < 6 || tokens[0] != "cpu")
            return false;

        double total = 0;
        double idle = 0;
        for (var i = 1; i < tokens.Length; i++)
        {
            if (!ulong.TryParse(tokens[i], out var value))
                return false;
            total += value;
            // tokens[4] = idle, tokens[5] = iowait; both count as idle time.
            if (i is 4 or 5)
                idle += value;
        }

        sample = new CpuSample(Busy: total - idle, Total: total);
        return true;
    }
}

/// <summary>
/// macOS CPU sampler: <c>host_statistics(HOST_CPU_LOAD_INFO)</c>, which returns
/// the host's cumulative CPU ticks split into user/system/idle/nice — a true
/// aggregate-utilization counter, so a delta of two snapshots is instantaneous
/// utilization (chosen over <c>getloadavg</c>, which is run-queue length, not
/// utilization, and can exceed 1). All unprivileged; no root, no entitlement.
/// </summary>
internal sealed partial class MacOsHostStatisticsCpuSampler : ICpuSampler
{
    private const int HostCpuLoadInfo = 3; // HOST_CPU_LOAD_INFO
    private const int CpuStateMax = 4;     // CPU_STATE_MAX: user, system, idle, nice
    private const int CpuStateUser = 0;
    private const int CpuStateSystem = 1;
    private const int CpuStateIdle = 2;
    private const int CpuStateNice = 3;

    public bool TrySample(out CpuSample sample)
    {
        sample = default;
        if (!OperatingSystem.IsMacOS())
            return false;

        return SampleHostTicks(out sample);
    }

    [SupportedOSPlatform("macos")]
    private static bool SampleHostTicks(out CpuSample sample)
    {
        sample = default;

        Span<uint> ticks = stackalloc uint[CpuStateMax];
        uint count = CpuStateMax; // in units of natural_t, in/out
        int rc;
        unsafe
        {
            // mach_host_self() returns the host-self send right; it need not be
            // released (it's the process's cached host port, not a fresh ref).
            fixed (uint* ticksPtr = ticks)
                rc = host_statistics(mach_host_self(), HostCpuLoadInfo, ticksPtr, &count);
        }
        if (rc != 0 || count < CpuStateMax) // KERN_SUCCESS == 0
            return false;

        double idle = ticks[CpuStateIdle];
        double total = (double)ticks[CpuStateUser]
            + ticks[CpuStateSystem]
            + ticks[CpuStateIdle]
            + ticks[CpuStateNice];
        if (total <= 0)
            return false;

        sample = new CpuSample(Busy: total - idle, Total: total);
        return true;
    }

    // mach_host_self / host_statistics live in libSystem, which libc re-exports —
    // the same library the sysctl P/Invokes in MacOsProcessInventory resolve against.
    [LibraryImport("libc")]
    private static partial uint mach_host_self();

    [LibraryImport("libc")]
    private static unsafe partial int host_statistics(uint hostPriv, int flavor, uint* info, uint* count);
}

/// <summary>
/// Windows CPU sampler: <c>GetSystemTimes</c>, which returns cumulative idle,
/// kernel, and user 100 ns FILETIME units since boot. Kernel time <b>includes</b>
/// idle, so total is <c>kernel + user</c> and busy is <c>total - idle</c>; the
/// delta of two snapshots is host utilization.
/// </summary>
internal sealed partial class WindowsSystemTimesCpuSampler : ICpuSampler
{
    public bool TrySample(out CpuSample sample)
    {
        sample = default;
        if (!OperatingSystem.IsWindows())
            return false;

        return SampleSystemTimes(out sample);
    }

    [SupportedOSPlatform("windows")]
    private static bool SampleSystemTimes(out CpuSample sample)
    {
        sample = default;
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return false;

        // kernel includes idle: total = kernel + user, busy = total - idle.
        double total = (double)kernel + user;
        if (total <= 0)
            return false;

        sample = new CpuSample(Busy: total - idle, Total: total);
        return true;
    }

    // FILETIME (two DWORDs) marshals cleanly as a 64-bit integer.
    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
