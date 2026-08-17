using System.Globalization;
using System.Text.RegularExpressions;

namespace Docket.HarnessBump;

/// <summary>
/// Aggregated <c>dotnet test</c> counts scraped from a job log.
/// </summary>
/// <remarks>
/// Reading these at all is what closes the hole that would otherwise make the merge gate
/// meaningless. A job conclusion of <c>success</c> is NOT evidence the new CLI works: if
/// <c>ANTHROPIC_KEY</c> is unset the real-harness facts are <c>SkippableFact</c>s that skip and
/// the job goes green anyway — by design, so fork PRs pass. Gating the merge on job conclusions
/// alone would therefore happily land an unverified bump on the one run where verification was
/// impossible. So the bot reads the counts and requires at least one fact to have actually PASSED.
/// </remarks>
public sealed record TestSummary(int Failed, int Passed, int Skipped, int Total)
{
    /// <summary>Green, and something actually executed.</summary>
    public bool Verified => Failed == 0 && Passed > 0;

    /// <summary>Green because nothing ran — see the type's remarks.</summary>
    public bool Vacuous => Failed == 0 && Passed == 0 && Skipped > 0;

    public override string ToString() => $"{Passed} passed, {Failed} failed, {Skipped} skipped";

    private static readonly Regex SummaryLine = new(
        @"Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Sums every summary line in the log, or null when the log has none — which is itself a
    /// failure signal (the test step never got far enough to report).
    /// </summary>
    public static TestSummary? Parse(string log)
    {
        var matches = SummaryLine.Matches(log);
        if (matches.Count == 0)
            return null;

        int failed = 0, passed = 0, skipped = 0, total = 0;
        // Explicitly typed: MatchCollection implements the non-generic ICollection too, so `var`
        // binds to object here.
        foreach (Match match in matches)
        {
            failed += Number(match, "failed");
            passed += Number(match, "passed");
            skipped += Number(match, "skipped");
            total += Number(match, "total");
        }
        return new TestSummary(failed, passed, skipped, total);

        static int Number(Match match, string group) =>
            int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    }
}
