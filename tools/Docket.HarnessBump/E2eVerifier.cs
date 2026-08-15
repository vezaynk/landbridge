using System.Text.Json.Serialization;

namespace Docket.HarnessBump;

public sealed record GhRun(
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion,
    [property: JsonPropertyName("url")] string Url);

public sealed record GhJob(
    [property: JsonPropertyName("databaseId")] long DatabaseId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("conclusion")] string? Conclusion);

public sealed record GhRunJobs([property: JsonPropertyName("jobs")] List<GhJob> Jobs);

/// <summary>The verdict on one dispatched CI run.</summary>
public sealed record E2eOutcome(bool Green, string Summary, IReadOnlyList<string> Details);

/// <summary>
/// Dispatches ci.yml on the bump branch, waits for the run, and decides whether the bump is
/// verified.
/// </summary>
/// <remarks>
/// WHY POLL-THEN-MERGE RATHER THAN GITHUB'S NATIVE AUTO-MERGE. Auto-merge lands a PR when its
/// required status checks pass — and the real-harness jobs can never be among them: they are
/// <c>if: github.event_name == 'workflow_dispatch'</c>, so on a <c>pull_request</c> event they
/// do not run at all. Enabling auto-merge would therefore merge the bump the moment
/// <c>build-test</c> and <c>chaos</c> went green, which is precisely the check that says nothing
/// about whether the new CLI works. Waiting on the dispatch here is the only mechanism that
/// gates on the evidence we actually care about.
/// </remarks>
public sealed class E2eVerifier(Cli cli, string repository, Action<string> log)
{
    /// <summary>The dispatch runs the whole workflow, so these must all be green.</summary>
    public static readonly IReadOnlyList<string> RealHarnessJobs =
        ["real-claude-e2e", "real-codex-e2e", "real-opencode-e2e", "real-grok-e2e"];

    public async Task<GhRun> DispatchAsync(
        string workflow,
        string branch,
        IReadOnlyDictionary<string, string> inputs,
        TimeSpan appearTimeout,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["workflow", "run", workflow, "--repo", repository, "--ref", branch];
        foreach (var (key, value) in inputs)
        {
            arguments.Add("-f");
            arguments.Add($"{key}={value}");
        }
        await cli.CheckedAsync("gh", arguments, cancellationToken: cancellationToken);

        // The branch was created by this run moments ago, so it cannot carry an older
        // workflow_dispatch run — whatever appears under this filter is ours. That is why the
        // lookup needs no timestamp comparison, which is the part that would be racy.
        var deadline = DateTimeOffset.UtcNow + appearTimeout;
        while (true)
        {
            var runs = await cli.GhJsonAsync<List<GhRun>>(
                ["run", "list", "--repo", repository, "--workflow", workflow,
                 "--branch", branch, "--event", "workflow_dispatch",
                 "--json", "databaseId,status,conclusion,url"],
                cancellationToken: cancellationToken);
            if (runs.Count > 0)
            {
                var run = runs.OrderBy(r => r.DatabaseId).First();
                log($"dispatched run {run.DatabaseId}: {run.Url}");
                return run;
            }
            if (DateTimeOffset.UtcNow > deadline)
                throw new InvalidOperationException(
                    $"dispatched {workflow} on {branch} but no run appeared within {appearTimeout}.");
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    public async Task<GhRun> WaitForCompletionAsync(
        long runId,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var run = await cli.GhJsonAsync<GhRun>(
                ["run", "view", runId.ToString(), "--repo", repository,
                 "--json", "databaseId,status,conclusion,url"],
                cancellationToken: cancellationToken);
            if (run.Status == "completed")
            {
                log($"run {runId} completed: {run.Conclusion}");
                return run;
            }
            if (DateTimeOffset.UtcNow > deadline)
                throw new InvalidOperationException(
                    $"run {runId} still {run.Status} after {timeout} — giving up waiting. " +
                    "The PR is left open either way; check the run.");
            log($"run {runId} is {run.Status}; waiting…");
            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Green only when every job succeeded (or was legitimately skipped), every
    /// real-harness job ran, and each of those actually executed facts rather than skipping
    /// them for a missing key.
    /// </summary>
    public async Task<E2eOutcome> EvaluateAsync(long runId, CancellationToken cancellationToken)
    {
        var jobs = (await cli.GhJsonAsync<GhRunJobs>(
            ["run", "view", runId.ToString(), "--repo", repository, "--json", "jobs"],
            cancellationToken: cancellationToken)).Jobs;

        var details = new List<string>();
        var green = true;

        foreach (var job in jobs)
        {
            var conclusion = job.Conclusion ?? job.Status;
            details.Add($"- `{job.Name}`: {conclusion}");
            if (conclusion is not ("success" or "skipped"))
                green = false;
        }

        foreach (var name in RealHarnessJobs)
        {
            var job = jobs.FirstOrDefault(j => j.Name == name);
            if (job is null)
            {
                details.Add($"- `{name}`: **did not run** — the dispatch should have included it.");
                green = false;
                continue;
            }
            if (job.Conclusion != "success")
            {
                green = false;
                continue; // already reported above
            }

            // Green is not enough — see TestSummary's remarks.
            var jobLog = await ReadJobLogAsync(job.DatabaseId, cancellationToken);
            if (jobLog.Text is null)
            {
                // Distinct from "read it and found no summary": that is a broken test step, this is
                // a broken reader. Naming which one saves the next person the hand-audit of a run
                // that was, in the first occurrence, entirely green.
                details.Add($"- `{name}`: green, but its log could NOT BE READ, so the result is unconfirmed " +
                            $"rather than bad — `{jobLog.Error}`. Check the run yourself before merging.");
                green = false;
                continue;
            }

            var summary = TestSummary.Parse(jobLog.Text);
            if (summary is null)
            {
                details.Add($"- `{name}`: green, but no `dotnet test` summary found in the log — treating as unverified.");
                green = false;
            }
            else if (summary.Vacuous)
            {
                details.Add($"- `{name}`: green but VACUOUS ({summary}) — the facts skipped, most likely a missing " +
                            "API-key secret. Nothing about the new CLI was verified.");
                green = false;
            }
            else if (!summary.Verified)
            {
                details.Add($"- `{name}`: {summary} — not a clean pass.");
                green = false;
            }
            else
            {
                details.Add($"  ({name} really ran: {summary})");
            }
        }

        var headline = green
            ? "all four real-harness tiers ran and passed"
            : "the real-harness dispatch did not come back clean";
        return new E2eOutcome(green, headline, details);
    }

    /// <summary>Outcome of trying to read one job's log: the text, or why it could not be had.</summary>
    private sealed record JobLog(string? Text, string? Error);

    /// <summary>
    /// argv for fetching one job's raw log.
    /// </summary>
    /// <param name="allowEscapeSequences">
    /// Adds <c>--allow-escape-sequences</c>. REQUIRED on newer gh and REJECTED by older gh, which is
    /// why <see cref="ReadJobLogAsync"/> tries both — see its remarks.
    /// </param>
    public static IReadOnlyList<string> JobLogArgs(string repository, long jobId, bool allowEscapeSequences)
    {
        List<string> args = ["api", $"/repos/{repository}/actions/jobs/{jobId}/logs"];
        if (allowEscapeSequences)
            args.Add("--allow-escape-sequences");
        return args;
    }

    /// <summary>
    /// The plain-text log for one job.
    /// </summary>
    /// <remarks>
    /// THIS IS THE STEP THAT BROKE THE FIRST REAL RUN, and the failure was invisible to the fixture
    /// tests because they fed log TEXT to <see cref="TestSummary"/> and never exercised the fetch.
    /// A runner's gh refused the response: "the response contains terminal escape sequences; pass
    /// --allow-escape-sequences to output it anyway". Job logs really do carry them — the log for
    /// run 31829608810's real-claude job has 8 ESC bytes in its coloured test lines, though the
    /// xUnit summary line the parser wants is plain. So the bot could not confirm a genuinely green
    /// 9/9 run, correctly fail-closed, and left the PR unmerged.
    /// <para>
    /// The flag is passed FIRST but not unconditionally, because it is gh-version-coupled in both
    /// directions: gh 2.92.0 rejects it outright with "unknown flag", while newer gh requires it.
    /// Pinning either way would break the other, and because this gate fails closed, a gh the flag
    /// does not suit means the bot silently stops merging anything rather than erroring loudly. So
    /// it tries with the flag and retries without it on exactly that "unknown flag" rejection.
    /// </para>
    /// <para>
    /// Failure returns a reason rather than throwing: a log that has expired or is briefly
    /// unavailable must read as "unverified", not crash the run after a PR already exists. The
    /// reason is surfaced in the PR comment, so the next incident of this kind says what went wrong
    /// on the PR instead of requiring someone to read the run by hand.
    /// </para>
    /// </remarks>
    private async Task<JobLog> ReadJobLogAsync(long jobId, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(
            "gh", JobLogArgs(repository, jobId, allowEscapeSequences: true),
            cancellationToken: cancellationToken);

        if (!result.Ok && RejectsUnknownFlag(result.StdErr))
        {
            log($"this gh does not know --allow-escape-sequences; retrying the job {jobId} log without it.");
            result = await cli.RunAsync(
                "gh", JobLogArgs(repository, jobId, allowEscapeSequences: false),
                cancellationToken: cancellationToken);
        }

        if (result.Ok)
            return new JobLog(result.StdOut, null);

        var error = result.StdErr.Trim();
        log($"could not read the log for job {jobId}: {error}");
        return new JobLog(null, error.Length > 0 ? error : $"gh exited {result.ExitCode}");
    }

    /// <summary>True when gh rejected an argument it does not have, rather than failing the request.</summary>
    public static bool RejectsUnknownFlag(string stdErr) =>
        stdErr.Contains("unknown flag", StringComparison.OrdinalIgnoreCase);
}
