namespace Docket.Runner.Tests;

/// <summary>
/// Back-pressure gating, spec §10 concurrency. Focus: the CPU signal only
/// participates when the reader actually observes it, so a reader that cannot
/// (<see cref="PortableSystemLoadReader"/> today) never lets its placeholder
/// reading silently defeat — or phantom-trip — <c>max_cpu_load</c>.
/// </summary>
public class BackPressureMonitorTests
{
    [Fact]
    public void Cpu_is_ignored_when_the_reader_does_not_observe_it()
    {
        // CpuLoad well over the threshold, but the reader does not observe CPU —
        // so the placeholder must not trip back-pressure, and memory/disk (both
        // clear here) are all that gate.
        var reader = new FakeLoadReader
        {
            ObservesCpu = false,
            Load = new SystemLoad(CpuLoad: 0.99, MemoryLoad: 0.0, DiskUsage: 0.0),
        };
        var monitor = new BackPressureMonitor(reader, BackPressureThresholds.Default);

        Assert.False(monitor.ObservesCpu);
        Assert.False(monitor.UnderPressure());
    }

    [Fact]
    public void Cpu_trips_when_the_reader_observes_it()
    {
        // Proves the CPU term is live for a reader that does observe CPU — the
        // seam a real per-OS reader plugs into.
        var reader = new FakeLoadReader
        {
            ObservesCpu = true,
            Load = new SystemLoad(CpuLoad: 0.95, MemoryLoad: 0.0, DiskUsage: 0.0),
        };
        var monitor = new BackPressureMonitor(reader, BackPressureThresholds.Default);

        Assert.True(monitor.ObservesCpu);
        Assert.True(monitor.UnderPressure());
    }

    [Fact]
    public void Portable_reader_does_not_observe_cpu_and_reports_placeholder_zero()
    {
        var reader = new PortableSystemLoadReader(Path.GetTempPath());

        Assert.False(reader.ObservesCpu);
        Assert.Equal(0.0, reader.Read().CpuLoad);
    }
}
