using System.Text;
using System.Text.Json.Serialization;

namespace Docket.HarnessBump;

/// <summary>
/// Daily harness-CLI bump-and-verify bot. Reads the three BYO-harness pins out of ci.yml,
/// compares them against each package's <c>dist-tags.latest</c>, and keeps at most ONE open bump
/// PR that tracks latest — dispatching the real harness e2e against it and merging only if that
/// dispatch really passed.
/// </summary>
/// <remarks>
/// Exit codes: 0 nothing to do, or bumped and merged; 1 a bump was proposed but did not verify
/// (PR left open for a human); 2 the tool could not run, or refused to act.
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine($"harness-bump: {e.Message}");
            Usage();
            return 2;
        }

        try
        {
            return await RunAsync(options, CancellationToken.None);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or HttpRequestException)
        {
            Console.Error.WriteLine($"harness-bump: {e.Message}");
            return 2;
        }
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: dotnet run --project tools/Docket.HarnessBump -- [options]");
        Console.Error.WriteLine("  --dry-run                report the plan and stop (spends nothing)");
        Console.Error.WriteLine("  --allow-local-writes     permit push/PR/dispatch/merge outside GitHub Actions");
        Console.Error.WriteLine("  --repo <owner/name>      default: $GITHUB_REPOSITORY");
        Console.Error.WriteLine("  --repo-root <path>       default: cwd");
        Console.Error.WriteLine("  --workflow <file>        workflow to bump + dispatch (default: ci.yml)");
        Console.Error.WriteLine("  --ci-path <path>         default: .github/workflows/ci.yml");
        Console.Error.WriteLine("  --base <branch>          PR base (default: master)");
        Console.Error.WriteLine("  --codex-model <slug>     dispatch input override; omitted = ci.yml's own default");
        Console.Error.WriteLine("  --opencode-model <slug>  dispatch input override; omitted = ci.yml's own default");
        Console.Error.WriteLine("  --run-timeout-minutes <n>  how long to wait for the e2e (default: 60)");
        Console.Error.WriteLine("env: DOCKET_BUMP_DRY_RUN=true, GH_TOKEN (needs contents+PRs+actions write)");
    }

    private static void Log(string message) => Console.WriteLine($"harness-bump: {message}");

    private static async Task<int> RunAsync(Options options, CancellationToken cancellationToken)
    {
        var cli = new Cli(Log);

        // ── 1. what ci.yml pins right now ──────────────────────────────────────────────
        if (!File.Exists(options.CiFullPath))
            throw new FileNotFoundException($"no workflow at {options.CiFullPath}");
        var yaml = await File.ReadAllTextAsync(options.CiFullPath, cancellationToken);
        var installs = CiWorkflowPins.Parse(yaml);
        if (installs.Count == 0)
            throw new InvalidOperationException(
                $"{options.CiPath} has no `npm install -g` line for any of " +
                $"{string.Join(", ", HarnessPackages.All.Select(p => p.NpmName))}.");
        var pinned = CiWorkflowPins.PinnedVersions(installs);
        foreach (var install in installs)
            Log($"{options.CiPath}:{install.LineNumber} installs {install.Package}@{install.Version ?? "<unpinned>"}");

        // ── 2. what the registry calls `latest` ────────────────────────────────────────
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var registry = new NpmRegistry(http);
        var latest = new Dictionary<string, string>();
        foreach (var package in HarnessPackages.All)
        {
            var version = await registry.GetLatestAsync(package.NpmName, cancellationToken);
            latest[package.NpmName] = version;
            Log($"{package.NpmName} dist-tags.latest = {version}");
        }

        // ── 3. the one piece of state: is a bump PR open? ──────────────────────────────
        var openPrs = await OpenBumpPrsAsync(cli, options, cancellationToken);
        if (openPrs.Count > 1)
        {
            // The single-open-PR invariant IS the cost guard. Two open bump PRs means something
            // outside the bot created one, so acting could close a human's work or double-spend.
            Console.Error.WriteLine(
                "harness-bump: " + openPrs.Count + " bump PRs are open at once (" +
                string.Join(", ", openPrs.Select(p => $"#{p.Number} {p.Branch}")) +
                "). The bot maintains exactly one; refusing to act until a human resolves this.");
            return 2;
        }
        var open = openPrs.SingleOrDefault();
        Log(open is null
            ? "no bump PR is open."
            : $"open bump PR #{open.Number} ({open.Branch}) targets " +
              string.Join(", ", open.Targets.Select(t => $"{t.Key}@{t.Value}")));

        // ── 4. decide ──────────────────────────────────────────────────────────────────
        var plan = BumpPlanner.Plan(pinned, latest, open);
        foreach (var verdict in plan.Verdicts)
            Log($"{(verdict.Bump ? "BUMP" : "skip")} {verdict.Package.Short} " +
                $"{verdict.Pinned} → {verdict.Latest}: {verdict.Reason}");
        Log(plan.Summary);

        switch (plan.Action)
        {
            case BumpAction.Blocked:
                Console.Error.WriteLine($"harness-bump: {plan.Summary}");
                return 2;
            case BumpAction.Idle:
                Log("nothing to do — today costs nothing.");
                return 0;
        }

        Log($"target branch {plan.BranchName}");
        if (options.DryRun)
        {
            Log("--dry-run: stopping before anything is created or closed.");
            return 0;
        }
        if (!Options.MutationsAllowed(options.DryRun, Options.InActions, options.AllowLocalWrites))
        {
            Console.Error.WriteLine(
                "harness-bump: refusing to open a PR and dispatch a real-harness run from outside " +
                "GitHub Actions. That dispatch spends real API tokens. Re-run with --dry-run to see " +
                "the plan, or --allow-local-writes if you genuinely mean to spend it.");
            return 2;
        }

        // ── 5. retire the superseded PR first ──────────────────────────────────────────
        // Before the new one exists, so the single-open-PR invariant is never briefly violated —
        // if this run dies between the two steps, the next run finds no open PR and simply opens
        // the right one.
        if (plan.Action == BumpAction.SupersedeOpen && plan.Open is not null)
        {
            var superseded = plan.Open;
            await cli.CheckedAsync("gh",
                ["pr", "comment", superseded.Number.ToString(), "--repo", options.Repository,
                 "--body",
                 $"Superseded: `dist-tags.latest` has moved past what this PR targets, so it is " +
                 $"being replaced by a fresh PR at {string.Join(", ", plan.Bumps.Select(b => $"`{b.Package.Short} {b.To}`"))}.\n\n" +
                 "Closing rather than force-pushing, so each version keeps its own PR and its own " +
                 "e2e record. Nothing about this version is remembered — if it becomes `latest` " +
                 "again it will be proposed again."],
                options.RepoRoot, cancellationToken);
            await cli.CheckedAsync("gh",
                ["pr", "close", superseded.Number.ToString(), "--repo", options.Repository, "--delete-branch"],
                options.RepoRoot, cancellationToken);
            Log($"closed superseded PR #{superseded.Number}");
        }

        // ── 6. branch + commit + push ──────────────────────────────────────────────────
        var bumps = plan.Bumps.ToDictionary(b => b.Package.NpmName, b => b.To);
        var updated = CiWorkflowPins.Rewrite(yaml, bumps);
        if (updated == yaml)
            throw new InvalidOperationException("rewriting ci.yml changed nothing — refusing to open an empty PR.");

        await cli.CheckedAsync("git", ["checkout", "-B", plan.BranchName], options.RepoRoot, cancellationToken);
        await File.WriteAllTextAsync(options.CiFullPath, updated, cancellationToken);
        await cli.CheckedAsync("git", ["add", options.CiPath], options.RepoRoot, cancellationToken);
        // Identity passed per-invocation rather than written into the repo config: the checkout is
        // shared with every other step in the job.
        await cli.CheckedAsync("git",
            ["-c", $"user.name={options.CommitterName}",
             "-c", $"user.email={options.CommitterEmail}",
             "commit", "-m", CommitMessage(plan)],
            options.RepoRoot, cancellationToken);
        // --force because a branch of this name can survive a previous cycle (a closed PR whose
        // branch deletion failed). There is no open PR on it — that was just established — so
        // there is nothing to clobber.
        await cli.CheckedAsync("git",
            ["push", "--force", "origin", $"HEAD:refs/heads/{plan.BranchName}"],
            options.RepoRoot, cancellationToken);

        // ── 7. PR ──────────────────────────────────────────────────────────────────────
        var prUrl = (await cli.CheckedAsync("gh",
            ["pr", "create", "--repo", options.Repository, "--base", options.BaseBranch,
             "--head", plan.BranchName, "--title", PrTitle(plan), "--body", PrBody(plan, options)],
            options.RepoRoot, cancellationToken)).Trim();
        Log($"opened {prUrl}");

        // ── 8. the real e2e, and only now ──────────────────────────────────────────────
        var verifier = new E2eVerifier(cli, options.Repository, Log);
        var inputs = new Dictionary<string, string>();
        if (options.CodexModel is { Length: > 0 })
            inputs["codex_model"] = options.CodexModel;
        if (options.OpenCodeModel is { Length: > 0 })
            inputs["opencode_model"] = options.OpenCodeModel;

        var run = await verifier.DispatchAsync(
            options.Workflow, plan.BranchName, inputs, TimeSpan.FromMinutes(3), cancellationToken);
        await verifier.WaitForCompletionAsync(
            run.DatabaseId, options.RunTimeout, options.PollInterval, cancellationToken);
        var outcome = await verifier.EvaluateAsync(run.DatabaseId, cancellationToken);

        // ── 9. merge, or hand it to a human ────────────────────────────────────────────
        var report = new StringBuilder()
            .AppendLine($"Real-harness dispatch: {run.Url}")
            .AppendLine()
            .AppendLine(outcome.Green ? "**Verified.**" : "**Not verified.**")
            .AppendLine()
            .AppendLine(string.Join('\n', outcome.Details))
            .ToString();

        if (!outcome.Green)
        {
            await cli.CheckedAsync("gh",
                ["pr", "comment", prUrl, "--repo", options.Repository, "--body",
                 report + "\nLeaving this PR **open** for a human to investigate. The bot does not " +
                 "bisect and does not retry: each attempt is real API spend. It also records nothing " +
                 "about this version — while this PR stays open the bot idles, and it will only act " +
                 "again when `dist-tags.latest` moves higher, at which point it closes this PR as " +
                 "superseded and tries the newer version. If the fix is on our side, fix it and " +
                 "re-run this PR's dispatch by hand."],
                options.RepoRoot, cancellationToken);
            Log($"e2e red — PR left open: {prUrl}");
            return 1;
        }

        await cli.CheckedAsync("gh",
            ["pr", "comment", prUrl, "--repo", options.Repository, "--body", report + "\nMerging."],
            options.RepoRoot, cancellationToken);
        await cli.CheckedAsync("gh",
            ["pr", "merge", prUrl, "--repo", options.Repository, "--squash", "--delete-branch"],
            options.RepoRoot, cancellationToken);
        Log($"merged {prUrl}");
        return 0;
    }

    /// <summary>
    /// The bot's open PRs, identified by their branch prefix. Only OPEN ones are looked at — the
    /// bot keeps no memory of closed or merged bumps by design.
    /// </summary>
    private static async Task<List<OpenBumpPr>> OpenBumpPrsAsync(
        Cli cli, Options options, CancellationToken cancellationToken)
    {
        var prs = await cli.GhJsonAsync<List<PrSummary>>(
            ["pr", "list", "--repo", options.Repository, "--state", "open", "--limit", "100",
             "--json", "number,url,headRefName"],
            options.RepoRoot, cancellationToken);
        return prs
            .Where(p => BumpBranch.IsBumpBranch(p.HeadRefName))
            .Select(p => new OpenBumpPr(p.Number, p.Url, p.HeadRefName, BumpBranch.TargetsIn(p.HeadRefName)))
            .ToList();
    }

    private sealed record PrSummary(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("headRefName")] string HeadRefName);

    private static string PrTitle(BumpPlan plan) =>
        plan.Bumps.Count == 1
            ? $"Bump the {plan.Bumps[0].Package.Short} CLI pin to {plan.Bumps[0].To}"
            : $"Bump the pinned harness CLIs: {string.Join(", ", plan.Bumps.Select(b => $"{b.Package.Short} {b.To}"))}";

    private static string CommitMessage(BumpPlan plan)
    {
        var body = new StringBuilder(PrTitle(plan)).AppendLine().AppendLine();
        foreach (var bump in plan.Bumps)
            body.AppendLine($"{bump.Package.NpmName}: {bump.From} → {bump.To} (dist-tags.latest)");
        return body.ToString();
    }

    private static string PrBody(BumpPlan plan, Options options)
    {
        var body = new StringBuilder();
        body.AppendLine("Opened by the daily harness-CLI bump bot (`tools/Docket.HarnessBump`).");
        body.AppendLine();
        body.AppendLine("| package | pinned | → | `dist-tags.latest` |");
        body.AppendLine("|---|---|---|---|");
        foreach (var bump in plan.Bumps)
            body.AppendLine($"| `{bump.Package.NpmName}` | `{bump.From}` | → | `{bump.To}` |");
        body.AppendLine();
        body.AppendLine("Each version is the package's **`dist-tags.latest`**, not the newest by publish time —");
        body.AppendLine("`@openai/codex` and `opencode-ai` both publish prereleases and snapshots that are newer");
        body.AppendLine("by timestamp and must never be installed.");
        if (plan.Action == BumpAction.SupersedeOpen && plan.Open is not null)
        {
            body.AppendLine();
            body.AppendLine($"Supersedes #{plan.Open.Number}, which targeted an older `latest`.");
        }
        body.AppendLine();
        body.AppendLine("The bot is now dispatching `" + options.Workflow + "` on this branch and will wait for");
        body.AppendLine("`real-claude-e2e`, `real-codex-e2e`, `real-opencode-e2e` and `real-grok-e2e`. **It merges only if all four");
        body.AppendLine("ran and passed** — a job that goes green because its facts skipped for a missing API key");
        body.AppendLine("counts as not verified.");
        body.AppendLine();
        body.AppendLine("If anything is red the bot posts the outcome and leaves this PR open, and then **idles**:");
        body.AppendLine("it keeps at most one bump PR at a time and remembers nothing about failed versions, so");
        body.AppendLine("while this PR is open no further e2e is spent. It acts again only when `latest` moves");
        body.AppendLine("higher, closing this PR as superseded and trying the newer version.");
        if (plan.Verdicts.Any(v => !v.Bump))
        {
            body.AppendLine();
            body.AppendLine("Not bumped in this PR:");
            foreach (var verdict in plan.Verdicts.Where(v => !v.Bump))
                body.AppendLine($"- `{verdict.Package.NpmName}` (pinned `{verdict.Pinned}`, latest `{verdict.Latest}`): {verdict.Reason}");
        }
        return body.ToString();
    }
}
