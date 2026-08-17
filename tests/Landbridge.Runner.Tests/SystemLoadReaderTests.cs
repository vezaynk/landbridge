namespace Docket.Runner.Tests;

/// <summary>
/// The real CPU-observing reader (§10). Covers the shared delta math, the
/// stateless sample-window read over a scripted sampler, the per-OS platform
/// selection, the Linux <c>/proc/stat</c> parse, and — gated to macOS — a live
/// host_statistics read. macOS/Windows syscalls are proven through the shared
/// <see cref="SystemLoadReader.ComputeBusyFraction"/> plus, on macOS, a real read.
/// </summary>
public class SystemLoadReaderTests
{
    [Fact]
    public void ComputeBusyFraction_is_the_busy_over_total_delta()
    {
        // busyDelta = 900-100 = 800, totalDelta = 2000-1000 = 1000 => 0.8.
        var previous = new CpuSample(Busy: 100, Total: 1000);
        var current = new CpuSample(Busy: 900, Total: 2000);

        Assert.Equal(0.8, SystemLoadReader.ComputeBusyFraction(previous, current), precision: 10);
    }

    [Fact]
    public void ComputeBusyFraction_clamps_into_the_unit_interval()
    {
        // A busy delta exceeding the total delta (skew/rounding at the counter
        // source) must not escape [0,1].
        var previous = new CpuSample(Busy: 0, Total: 0);
        var current = new CpuSample(Busy: 1500, Total: 1000);

        Assert.Equal(1.0, SystemLoadReader.ComputeBusyFraction(previous, current));
    }

    [Fact]
    public void ComputeBusyFraction_yields_zero_when_no_time_elapsed()
    {
        // Identical snapshots (totalDelta == 0) — and a wrapped/reset counter
        // (negative delta) — are a non-reading, not a divide-by-zero or a bogus load.
        var same = new CpuSample(Busy: 500, Total: 1000);
        Assert.Equal(0.0, SystemLoadReader.ComputeBusyFraction(same, same));

        var wrapped = new CpuSample(Busy: 100, Total: 200);
        Assert.Equal(0.0, SystemLoadReader.ComputeBusyFraction(same, wrapped));
    }

    [Fact]
    public void Reader_reports_the_fraction_between_its_two_samples_and_observes_cpu()
    {
        // Two snapshots per Read(): 80% busy over the window.
        var sampler = new FakeCpuSampler(
            new CpuSample(Busy: 100, Total: 1000),
            new CpuSample(Busy: 900, Total: 2000));
        var reader = new SystemLoadReader(sampler, Path.GetTempPath(), TimeSpan.Zero);

        Assert.True(reader.ObservesCpu);
        Assert.Equal(0.8, reader.Read().CpuLoad, precision: 10);
    }

    [Fact]
    public void Reader_reports_zero_cpu_when_a_sample_fails()
    {
        // Only one snapshot available; the second TrySample fails, so CPU is a
        // clean 0 for this cycle rather than a fabricated number.
        var sampler = new FakeCpuSampler(new CpuSample(Busy: 100, Total: 1000));
        var reader = new SystemLoadReader(sampler, Path.GetTempPath(), TimeSpan.Zero);

        Assert.Equal(0.0, reader.Read().CpuLoad);
    }

    [Fact]
    public void ForCurrentPlatform_observes_cpu_on_the_supported_matrix()
    {
        var reader = SystemLoadReader.ForCurrentPlatform(Path.GetTempPath());

        // Linux/macOS/Windows get a real reader; anything else the portable fallback.
        var supported = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();
        Assert.Equal(supported, reader.ObservesCpu);
        if (supported)
            Assert.IsType<SystemLoadReader>(reader);
        else
            Assert.IsType<PortableSystemLoadReader>(reader);
    }

    [Fact]
    public void Back_pressure_trips_when_a_real_reader_reports_high_cpu()
    {
        // End-to-end through BackPressureMonitor: a reader that observes CPU and
        // reports 95% busy trips max_cpu_load (default 0.90), with memory/disk clear.
        var hot = new SystemLoadReader(
            new FakeCpuSampler(new CpuSample(Busy: 0, Total: 0), new CpuSample(Busy: 95, Total: 100)),
            Path.GetTempPath(),
            TimeSpan.Zero);
        var monitor = new BackPressureMonitor(hot, BackPressureThresholds.Default);

        Assert.True(monitor.ObservesCpu);
        var reading = monitor.Evaluate();
        Assert.Equal(0.95, reading.Load.CpuLoad, precision: 10);
        Assert.True(reading.UnderPressure);
    }

    [Fact]
    public void Back_pressure_stays_clear_when_a_real_reader_reports_low_cpu()
    {
        // The same wiring must NOT trip when CPU is comfortably under the threshold.
        var cool = new SystemLoadReader(
            new FakeCpuSampler(new CpuSample(Busy: 0, Total: 0), new CpuSample(Busy: 10, Total: 100)),
            Path.GetTempPath(),
            TimeSpan.Zero);
        var monitor = new BackPressureMonitor(cool, BackPressureThresholds.Default);

        var reading = monitor.Evaluate();
        Assert.Equal(0.10, reading.Load.CpuLoad, precision: 10);
        Assert.False(reading.UnderPressure);
    }

    [Theory]
    // cpu user nice system idle iowait irq softirq steal ... — total 1000, idle+iowait 850.
    [InlineData("cpu  100 20 30 800 50 0 0 0 0 0", 150.0, 1000.0)]
    // Leading/trailing whitespace and only the required fields present.
    [InlineData("cpu 200 0 100 600 100", 300.0, 1000.0)]
    public void Linux_parses_the_aggregate_cpu_line(string line, double expectedBusy, double expectedTotal)
    {
        Assert.True(LinuxProcStatCpuSampler.TryParseAggregate(line, out var sample));
        Assert.Equal(expectedBusy, sample.Busy);
        Assert.Equal(expectedTotal, sample.Total);
    }

    [Theory]
    [InlineData("cpu0 100 20 30 800 50")]  // per-core line, not the aggregate
    [InlineData("intr 12345 0 0")]          // a different /proc/stat line
    [InlineData("cpu 100 20")]              // too few fields to reach idle+iowait
    [InlineData("cpu 100 x 30 800 50")]     // non-numeric field
    public void Linux_rejects_non_aggregate_or_malformed_lines(string line)
    {
        Assert.False(LinuxProcStatCpuSampler.TryParseAggregate(line, out _));
    }

    // Each live backend is exercised on its own OS leg of the matrix (a skip
    // elsewhere is a documented deferral, not a gap): the real syscall must
    // return a positive cumulative counter, and the reader over a real window
    // must land a fraction in [0,1].

    [SkippableFact]
    public void MacOs_host_statistics_returns_a_plausible_utilization_snapshot()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "macOS-only: reads host CPU ticks via host_statistics.");
        AssertLiveSampler(new MacOsHostStatisticsCpuSampler());
    }

    [SkippableFact]
    public void Linux_proc_stat_returns_a_plausible_utilization_snapshot()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-only: reads the aggregate cpu line of /proc/stat.");
        AssertLiveSampler(new LinuxProcStatCpuSampler());
    }

    [SkippableFact]
    public void Windows_system_times_returns_a_plausible_utilization_snapshot()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: reads idle/kernel/user via GetSystemTimes.");
        AssertLiveSampler(new WindowsSystemTimesCpuSampler());
    }

    private static void AssertLiveSampler(ICpuSampler sampler)
    {
        Assert.True(sampler.TrySample(out var sample), "the platform CPU syscall must succeed on its own OS");
        Assert.True(sample.Total > 0, "cumulative CPU counters must be positive");
        Assert.InRange(sample.Busy, 0.0, sample.Total);

        var reader = new SystemLoadReader(sampler, Path.GetTempPath(), TimeSpan.FromMilliseconds(50));
        Assert.InRange(reader.Read().CpuLoad, 0.0, 1.0);
    }
}
