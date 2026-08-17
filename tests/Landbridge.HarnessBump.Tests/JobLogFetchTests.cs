namespace Landbridge.HarnessBump.Tests;

/// <summary>
/// The log fetch, which is the one step the original fixture tests could not see: they fed log TEXT
/// to <see cref="TestSummary"/> and so never exercised the <c>gh</c> invocation that produces it. A
/// runner's gh then refused the response ("the response contains terminal escape sequences; pass
/// --allow-escape-sequences to output it anyway"), the bot could not confirm a genuinely green 9/9
/// run, and it correctly fail-closed and left the PR unmerged. These are the guards that keep the
/// live-fetch requirements from regressing.
/// </summary>
public class JobLogFetchTests
{
    private const string Repo = "vezaynk/landbridge-mcp";
    private const long JobId = 94861882031;

    [Fact]
    public void The_log_fetch_argv_passes_allow_escape_sequences()
    {
        // The requirement the first real run established. Job logs carry ESC bytes in their coloured
        // test lines, and newer gh refuses to emit them without this flag.
        var args = E2eVerifier.JobLogArgs(Repo, JobId, allowEscapeSequences: true);
        Assert.Contains("--allow-escape-sequences", args);
        Assert.Equal("api", args[0]);
        Assert.Equal($"/repos/{Repo}/actions/jobs/{JobId}/logs", args[1]);
    }

    [Fact]
    public void The_flag_can_be_left_off_for_a_gh_that_does_not_know_it()
    {
        // Not a nicety: gh 2.92.0 rejects the flag outright with "unknown flag", while newer gh
        // requires it. Pinning either way breaks the other, and because the merge gate fails closed,
        // the wrong choice makes the bot silently stop merging rather than fail loudly.
        var args = E2eVerifier.JobLogArgs(Repo, JobId, allowEscapeSequences: false);
        Assert.DoesNotContain("--allow-escape-sequences", args);
        Assert.Equal(2, args.Count);
    }

    [Fact]
    public void Both_forms_hit_the_same_endpoint_so_the_retry_cannot_drift()
    {
        var withFlag = E2eVerifier.JobLogArgs(Repo, JobId, allowEscapeSequences: true);
        var without = E2eVerifier.JobLogArgs(Repo, JobId, allowEscapeSequences: false);
        Assert.Equal(without, withFlag.Take(without.Count));
    }

    [Theory]
    [InlineData("unknown flag: --allow-escape-sequences", true)]
    [InlineData("Unknown flag: --allow-escape-sequences\n\nUsage:  gh api <endpoint> [flags]", true)]
    [InlineData("the response contains terminal escape sequences; pass --allow-escape-sequences to output it anyway", false)]
    [InlineData("gh: Not Found (HTTP 404)", false)]
    [InlineData("", false)]
    public void Only_an_unknown_flag_rejection_triggers_the_retry(string stdErr, bool expected)
    {
        // The escape-sequence message must NOT trigger the fallback — retrying without the flag is
        // exactly what cannot work in that case.
        Assert.Equal(expected, E2eVerifier.RejectsUnknownFlag(stdErr));
    }

    [Fact]
    public void The_summary_still_parses_out_of_a_log_carrying_escape_sequences()
    {
        // The verbatim summary line from run 31829608810's real-claude job — the run the lead
        // hand-verified as 9/9 — wrapped in the kind of coloured lines that carry the ESC bytes. The
        // point is that the escapes are in the surrounding output, never in the summary itself, so
        // once the fetch is allowed to return the bytes the parser needs nothing else.
        var log = string.Join('\n',
            "2026-08-14T18:44:33.1000000Z \u001b[32mPassed\u001b[0m Landbridge.MultiMachine.Tests.RealClaude...",
            "2026-08-14T18:44:35.2000000Z \u001b[1;31mfail\u001b[0m: something noisy \u001b[0m",
            "2026-08-14T18:44:37.3275571Z Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 4 m 13 s - Landbridge.MultiMachine.Tests.dll (net10.0)");

        Assert.Contains('\u001b', log);
        var summary = TestSummary.Parse(log);
        Assert.NotNull(summary);
        Assert.Equal(new TestSummary(0, 9, 0, 9), summary);
        Assert.True(summary.Verified);
        Assert.False(summary.Vacuous);
    }

    [Fact]
    public void A_vacuous_summary_survives_escapes_too_and_still_blocks_the_merge()
    {
        // Guards the other half: the escape handling must not accidentally turn a skipped tier into
        // a pass. This is what a dispatch with no ANTHROPIC_KEY looks like.
        var log = "2026-08-14T18:44:37.3Z \u001b[33mskip\u001b[0m\n" +
                  "2026-08-14T18:44:38.0Z Passed!  - Failed:     0, Passed:     0, Skipped:     9, Total:     9";
        var summary = TestSummary.Parse(log);
        Assert.NotNull(summary);
        Assert.True(summary.Vacuous);
        Assert.False(summary.Verified);
    }
}
