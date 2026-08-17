namespace Docket.Core.Tests;

/// <summary>
/// §10 in-band worker report: the optional <c>report</c> on report_result is
/// size-capped at the engine (a length gate, not content interpretation), so detail
/// belongs in the workspace behind the result reference rather than in the plane.
/// </summary>
public class ReportCapTests
{
    private static TransitionResult Report(string? report)
    {
        var task = Given.Session(SessionState.Working);
        return SessionStateMachine.Apply(task, new ReportResult(Given.IncumbentOf(task), "git:ref", report));
    }

    [Fact]
    public void A_null_report_is_accepted() =>
        Expect.Transitioned(Report(null), SessionState.Verifying); // back-compat: report is optional

    [Fact]
    public void A_report_at_the_cap_is_accepted() =>
        Expect.Transitioned(Report(new string('x', ReportResult.MaxReportBytes)), SessionState.Verifying);

    [Fact]
    public void A_report_one_byte_over_the_cap_is_rejected() =>
        Expect.Rejected(Report(new string('x', ReportResult.MaxReportBytes + 1)), Rule.ReportWithinSizeCap);

    [Fact]
    public void The_cap_counts_utf8_bytes_not_chars()
    {
        // '€' (U+20AC) is 3 UTF-8 bytes. MaxReportBytes of them is exactly the byte
        // cap in char count but 3× over in bytes — proving the gate measures bytes.
        Expect.Rejected(Report(new string('€', ReportResult.MaxReportBytes)), Rule.ReportWithinSizeCap);
    }
}
