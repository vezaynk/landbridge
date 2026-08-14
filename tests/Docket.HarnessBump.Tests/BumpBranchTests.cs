namespace Docket.HarnessBump.Tests;

public class BumpBranchTests
{
    private static Bump Claude(string to) => new(HarnessPackages.Claude, "2.1.229", to);
    private static Bump OpenCode(string to) => new(HarnessPackages.OpenCode, "1.18.18", to);
    private static Bump Codex(string to) => new(HarnessPackages.Codex, "0.147.0", to);

    [Fact]
    public void One_bump_becomes_one_marker()
    {
        Assert.Equal("harness-bump/claude-2.1.232", BumpBranch.Name([Claude("2.1.232")]));
    }

    [Fact]
    public void The_same_bump_set_always_produces_the_same_branch_name()
    {
        // The name is how the next run learns what the open PR targets, so it has to be a function
        // of the set, not of the order the packages happened to be enumerated in.
        var forwards = BumpBranch.Name([Claude("2.1.232"), Codex("0.148.0"), OpenCode("1.18.19")]);
        var backwards = BumpBranch.Name([OpenCode("1.18.19"), Claude("2.1.232"), Codex("0.148.0")]);
        Assert.Equal(forwards, backwards);
        Assert.Equal("harness-bump/claude-2.1.232_codex-0.148.0_opencode-1.18.19", forwards);
    }

    [Fact]
    public void Targets_survive_the_round_trip()
    {
        var name = BumpBranch.Name([Claude("2.1.232"), OpenCode("1.18.19")]);
        var targets = BumpBranch.TargetsIn(name);
        Assert.Equal("2.1.232", targets["@anthropic-ai/claude-code"]);
        Assert.Equal("1.18.19", targets["opencode-ai"]);
        Assert.False(targets.ContainsKey("@openai/codex"));
    }

    [Fact]
    public void A_marker_resolves_only_on_a_whole_segment()
    {
        // Substring matching would let `claude-2.1.23` read as a target of 2.1.231 or 2.1.232 and
        // silently misjudge whether latest has climbed.
        var targets = BumpBranch.TargetsIn("harness-bump/claude-2.1.23");
        Assert.Equal("2.1.23", targets["@anthropic-ai/claude-code"]);
        Assert.Single(targets);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("pin-claude-cli")]
    [InlineData("feature/harness-bump/claude-2.1.232")]
    public void Branches_that_are_not_bump_branches_are_not_treated_as_the_bots(string branch)
    {
        Assert.False(BumpBranch.IsBumpBranch(branch));
        Assert.Empty(BumpBranch.TargetsIn(branch));
    }

    [Theory]
    [InlineData("harness-bump/")]
    [InlineData("harness-bump/hand-edited")]
    [InlineData("harness-bump/notapackage-1.0.0")]
    [InlineData("harness-bump/claude-notaversion")]
    public void A_bump_branch_naming_nothing_recognisable_yields_no_targets(string branch)
    {
        // The planner turns this into Blocked rather than guessing — asserted there.
        Assert.True(BumpBranch.IsBumpBranch(branch));
        Assert.Empty(BumpBranch.TargetsIn(branch));
    }

    [Fact]
    public void An_unrecognisable_marker_alongside_a_good_one_does_not_poison_it()
    {
        var targets = BumpBranch.TargetsIn("harness-bump/claude-2.1.232_bogus-thing");
        Assert.Equal("2.1.232", targets["@anthropic-ai/claude-code"]);
        Assert.Single(targets);
    }

    [Theory]
    [InlineData("refs/heads/harness-bump/claude-2.1.232")]
    [InlineData("origin/harness-bump/claude-2.1.232")]
    [InlineData("harness-bump/claude-2.1.232")]
    [InlineData("  harness-bump/claude-2.1.232  ")]
    public void The_qualified_forms_git_and_the_api_return_all_read(string branch)
    {
        Assert.True(BumpBranch.IsBumpBranch(branch));
        Assert.Equal("2.1.232", BumpBranch.TargetsIn(branch)["@anthropic-ai/claude-code"]);
    }
}
