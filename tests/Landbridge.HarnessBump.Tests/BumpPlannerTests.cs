namespace Landbridge.HarnessBump.Tests;

/// <summary>
/// The whole decision procedure. Every one of these is a rule about not spending money twice, so
/// each is asserted directly rather than inferred from a live run.
/// </summary>
public class BumpPlannerTests
{
    private const string ClaudePackage = "@anthropic-ai/claude-code";
    private const string CodexPackage = "@openai/codex";
    private const string OpenCodePackage = "opencode-ai";

    private static Dictionary<string, string?> Pinned(
        string? claude = "2.1.229", string? codex = "0.147.0", string? opencode = "1.18.18") =>
        new() { [ClaudePackage] = claude, [CodexPackage] = codex, [OpenCodePackage] = opencode };

    private static Dictionary<string, string> Latest(
        string claude = "2.1.229", string codex = "0.147.0", string opencode = "1.18.18") =>
        new() { [ClaudePackage] = claude, [CodexPackage] = codex, [OpenCodePackage] = opencode };

    /// <summary>An open bump PR whose branch name is built the way the bot builds them.</summary>
    private static OpenBumpPr OpenPr(string branch, int number = 42) =>
        new(number, $"https://github.com/vezaynk/landbridge/pull/{number}", branch, BumpBranch.TargetsIn(branch));

    private static PackageVerdict VerdictFor(BumpPlan plan, HarnessPackage package) =>
        Assert.Single(plan.Verdicts, v => v.Package == package);

    // ── nothing open ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_open_pr_and_everything_current_is_idle()
    {
        var plan = BumpPlanner.Plan(Pinned(), Latest(), null);
        Assert.Equal(BumpAction.Idle, plan.Action);
        Assert.Empty(plan.Bumps);
    }

    [Fact]
    public void No_open_pr_and_something_newer_opens_one()
    {
        var plan = BumpPlanner.Plan(Pinned(), Latest(claude: "2.1.232"), null);
        Assert.Equal(BumpAction.OpenNew, plan.Action);
        var bump = Assert.Single(plan.Bumps);
        Assert.Equal(HarnessPackages.Claude, bump.Package);
        Assert.Equal("2.1.229", bump.From);
        Assert.Equal("2.1.232", bump.To);
        Assert.Equal("harness-bump/claude-2.1.232", plan.BranchName);
    }

    [Fact]
    public void Three_available_bumps_become_ONE_combined_pr_and_therefore_one_dispatch()
    {
        var plan = BumpPlanner.Plan(
            Pinned(), Latest(claude: "2.1.232", codex: "0.148.0", opencode: "1.18.19"), null);
        Assert.Equal(BumpAction.OpenNew, plan.Action);
        Assert.Equal(3, plan.Bumps.Count);
        Assert.Equal("harness-bump/claude-2.1.232_codex-0.148.0_opencode-1.18.19", plan.BranchName);
    }

    // ── one open PR: the entire state ────────────────────────────────────────────────────

    [Fact]
    public void An_open_pr_that_already_targets_latest_is_left_alone()
    {
        var plan = BumpPlanner.Plan(
            Pinned(), Latest(claude: "2.1.232"), OpenPr("harness-bump/claude-2.1.232"));
        Assert.Equal(BumpAction.Idle, plan.Action);
    }

    [Fact]
    public void A_failed_open_pr_costs_nothing_on_every_later_run()
    {
        // The cost guard, stated as the behaviour that replaces a known-bad list. The bump to
        // 2.1.232 got exactly one e2e when its PR was opened; while that PR stays open — red or
        // not, the bot cannot tell and does not ask — every subsequent daily run idles.
        var open = OpenPr("harness-bump/claude-2.1.232");
        for (var day = 0; day < 30; day++)
        {
            var plan = BumpPlanner.Plan(Pinned(), Latest(claude: "2.1.232"), open);
            Assert.Equal(BumpAction.Idle, plan.Action);
        }
    }

    [Fact]
    public void A_higher_latest_supersedes_the_open_pr()
    {
        var open = OpenPr("harness-bump/claude-2.1.232", number: 7);
        var plan = BumpPlanner.Plan(Pinned(), Latest(claude: "2.1.233"), open);

        Assert.Equal(BumpAction.SupersedeOpen, plan.Action);
        Assert.Same(open, plan.Open);
        Assert.Equal(7, plan.Open!.Number);
        // The fresh PR is computed from the PIN, not from the superseded PR's target.
        var bump = Assert.Single(plan.Bumps);
        Assert.Equal("2.1.229", bump.From);
        Assert.Equal("2.1.233", bump.To);
        Assert.Equal("harness-bump/claude-2.1.233", plan.BranchName);
    }

    [Fact]
    public void A_version_that_failed_gets_retried_once_a_higher_one_ships()
    {
        // The point of keeping no failure record: 2.1.232 may have failed, but 2.1.233 is a
        // genuinely different candidate and a suppression list would have refused to try it.
        var plan = BumpPlanner.Plan(
            Pinned(), Latest(claude: "2.1.233"), OpenPr("harness-bump/claude-2.1.232"));
        Assert.Equal(BumpAction.SupersedeOpen, plan.Action);
        Assert.Single(plan.Bumps, b => b.To == "2.1.233");
    }

    [Fact]
    public void A_package_missing_from_the_open_pr_that_becomes_bumpable_also_supersedes()
    {
        // The open PR bumps claude only. opencode then publishes a newer latest. A PR that does
        // not mention opencode effectively leaves it at its pin, so latest HAS climbed past what
        // this PR targets — and the replacement carries both bumps, keeping "one combined PR".
        var plan = BumpPlanner.Plan(
            Pinned(), Latest(claude: "2.1.232", opencode: "1.18.19"),
            OpenPr("harness-bump/claude-2.1.232"));

        Assert.Equal(BumpAction.SupersedeOpen, plan.Action);
        Assert.Equal(2, plan.Bumps.Count);
        Assert.Equal("harness-bump/claude-2.1.232_opencode-1.18.19", plan.BranchName);
    }

    [Fact]
    public void An_open_combined_pr_still_at_latest_on_both_packages_is_idle()
    {
        var plan = BumpPlanner.Plan(
            Pinned(), Latest(claude: "2.1.232", opencode: "1.18.19"),
            OpenPr("harness-bump/claude-2.1.232_opencode-1.18.19"));
        Assert.Equal(BumpAction.Idle, plan.Action);
    }

    [Fact]
    public void An_open_pr_targeting_a_version_that_is_no_longer_latest_downward_is_left_alone()
    {
        // `latest` rolled back (an unpublish, or a bad release pulled). Superseding is defined on
        // latest being HIGHER, so this idles — the bot never walks a pin backwards, and never
        // closes a PR just because the registry moved down.
        var plan = BumpPlanner.Plan(
            Pinned(), Latest(claude: "2.1.229"), OpenPr("harness-bump/claude-2.1.232"));
        Assert.Equal(BumpAction.Idle, plan.Action);
    }

    [Fact]
    public void A_bump_branch_naming_nothing_recognisable_blocks_rather_than_guesses()
    {
        // Superseding on a misread branch name would close a PR a human may have opened; opening a
        // second PR would break the single-open-PR invariant that IS the cost guard. So neither.
        var open = new OpenBumpPr(9, "u", "harness-bump/hand-edited-by-a-human", new Dictionary<string, string>());
        var plan = BumpPlanner.Plan(Pinned(), Latest(claude: "2.1.232"), open);
        Assert.Equal(BumpAction.Blocked, plan.Action);
        Assert.Contains("harness-bump/hand-edited-by-a-human", plan.Summary);
    }

    // ── what never becomes a bump ────────────────────────────────────────────────────────

    [Fact]
    public void An_unpinned_install_is_reported_rather_than_bumped()
    {
        var plan = BumpPlanner.Plan(Pinned(codex: null), Latest(codex: "0.148.0"), null);
        Assert.Equal(BumpAction.Idle, plan.Action);
        Assert.Contains("UNPINNED", VerdictFor(plan, HarnessPackages.Codex).Reason);
    }

    [Fact]
    public void A_prerelease_latest_is_refused_even_though_it_is_numerically_newer()
    {
        // Unreachable while we read dist-tags.latest — the belt-and-braces layer for the day a
        // publisher points `latest` at an alpha. codex really does publish 0.148.0-alpha.12.
        var plan = BumpPlanner.Plan(Pinned(), Latest(codex: "0.148.0-alpha.12"), null);
        Assert.Equal(BumpAction.Idle, plan.Action);
        Assert.Contains("prerelease", VerdictFor(plan, HarnessPackages.Codex).Reason);
    }

    [Fact]
    public void A_latest_older_than_the_pin_leaves_the_pin_alone()
    {
        var plan = BumpPlanner.Plan(Pinned(claude: "2.1.232"), Latest(claude: "2.1.229"), null);
        Assert.Equal(BumpAction.Idle, plan.Action);
        Assert.Contains("OLDER", VerdictFor(plan, HarnessPackages.Claude).Reason);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1.18")]
    public void An_unparseable_version_on_either_side_is_refused_not_guessed(string bad)
    {
        Assert.Empty(BumpPlanner.Plan(Pinned(opencode: bad), Latest(opencode: "1.18.19"), null).Bumps);
        Assert.Empty(BumpPlanner.Plan(Pinned(), Latest(opencode: bad), null).Bumps);
    }

    [Fact]
    public void A_package_ci_yml_does_not_install_at_all_is_reported()
    {
        var pinned = new Dictionary<string, string?> { [OpenCodePackage] = "1.18.18" };
        var plan = BumpPlanner.Plan(pinned, Latest(), null);
        Assert.Contains("no install line", VerdictFor(plan, HarnessPackages.Claude).Reason);
    }

    [Fact]
    public void Every_package_gets_a_verdict_every_run_whether_or_not_it_moves()
    {
        // The run log has to say why each package was left alone, or a bot that quietly stopped
        // proposing anything would look identical to a bot with nothing to propose.
        var plan = BumpPlanner.Plan(Pinned(), Latest(claude: "2.1.232"), null);
        Assert.Equal(HarnessPackages.All.Count, plan.Verdicts.Count);
        Assert.All(plan.Verdicts, v => Assert.False(string.IsNullOrWhiteSpace(v.Reason)));
        Assert.False(string.IsNullOrWhiteSpace(plan.Summary));
    }

    // ── the live state, end to end ───────────────────────────────────────────────────────

    [Fact]
    public void The_real_state_on_master_opens_exactly_one_claude_pr()
    {
        // master pins claude 2.1.229 / codex 0.147.0 / opencode 1.18.18; latest is 2.1.232 /
        // 0.147.0 / 1.18.18. So the first scheduled run opens ONE PR for ONE package. That its
        // e2e may go red is expected and is not the bot's problem to predict: it will sit open,
        // costing nothing, until a human resolves it or a newer claude ships.
        var plan = BumpPlanner.Plan(
            Pinned(claude: "2.1.229", codex: "0.147.0", opencode: "1.18.18"),
            Latest(claude: "2.1.232", codex: "0.147.0", opencode: "1.18.18"),
            null);

        Assert.Equal(BumpAction.OpenNew, plan.Action);
        var bump = Assert.Single(plan.Bumps);
        Assert.Equal(HarnessPackages.Claude, bump.Package);
        Assert.Equal("2.1.232", bump.To);
        Assert.Equal("harness-bump/claude-2.1.232", plan.BranchName);
    }
}
