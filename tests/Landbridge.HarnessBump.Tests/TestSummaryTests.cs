namespace Landbridge.HarnessBump.Tests;

/// <summary>
/// The guard against a vacuous green. A real-harness job whose facts all SKIP — because the
/// API-key secret is absent — still concludes <c>success</c>, so job conclusions alone would
/// let the bot merge a bump nothing had verified.
/// </summary>
public class TestSummaryTests
{
    [Fact]
    public void A_real_pass_is_verified()
    {
        const string log = "Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 1 m 2 s";
        var summary = TestSummary.Parse(log);
        Assert.NotNull(summary);
        Assert.Equal(new TestSummary(0, 4, 0, 4), summary);
        Assert.True(summary.Verified);
        Assert.False(summary.Vacuous);
    }

    [Fact]
    public void A_run_where_every_fact_skipped_is_green_but_vacuous()
    {
        // This is what a dispatch without ANTHROPIC_KEY looks like: the job goes green, spends
        // nothing, and proves nothing. It must NOT clear the merge gate.
        const string log = "Passed!  - Failed:     0, Passed:     0, Skipped:     4, Total:     4";
        var summary = TestSummary.Parse(log);
        Assert.NotNull(summary);
        Assert.True(summary.Vacuous);
        Assert.False(summary.Verified);
    }

    [Fact]
    public void A_partial_skip_still_counts_as_verified()
    {
        // Some facts legitimately skip per-platform; as long as something really passed and
        // nothing failed, the tier ran.
        const string log = "Passed!  - Failed:     0, Passed:     3, Skipped:     1, Total:     4";
        var summary = TestSummary.Parse(log)!;
        Assert.True(summary.Verified);
        Assert.False(summary.Vacuous);
    }

    [Fact]
    public void A_failure_is_neither_verified_nor_vacuous()
    {
        // The shape of the red claude run: 7 passed, 2 failed on identical test code.
        const string log = "Failed!  - Failed:     2, Passed:     7, Skipped:     0, Total:     9";
        var summary = TestSummary.Parse(log)!;
        Assert.False(summary.Verified);
        Assert.False(summary.Vacuous);
        Assert.Equal(2, summary.Failed);
    }

    [Fact]
    public void Actions_timestamp_prefixes_do_not_hide_the_summary()
    {
        // Job logs fetched from the API carry a leading ISO timestamp on every line.
        const string log = """
            2026-08-13T06:21:44.1234567Z   Determining projects to restore...
            2026-08-13T06:31:02.7654321Z Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4
            """;
        Assert.True(TestSummary.Parse(log)!.Verified);
    }

    [Fact]
    public void Several_summary_lines_in_one_log_are_summed()
    {
        // Defensive: if a job ever runs more than one `dotnet test`, a pass in the first must not
        // mask a failure in the second.
        const string log = """
            Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4
            Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3
            """;
        var summary = TestSummary.Parse(log)!;
        Assert.Equal(new TestSummary(1, 6, 0, 7), summary);
        Assert.False(summary.Verified);
    }

    [Fact]
    public void A_log_with_no_summary_reads_as_unknown()
    {
        // The test step never got far enough to report — a build break, a wedged runner. Null
        // makes the caller treat it as unverified rather than as a pass.
        Assert.Null(TestSummary.Parse("2026-08-13T06:21:44Z error NETSDK1004: Assets file not found"));
        Assert.Null(TestSummary.Parse(""));
    }
}
