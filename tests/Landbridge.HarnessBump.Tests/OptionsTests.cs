namespace Docket.HarnessBump.Tests;

public class OptionsTests
{
    [Theory]
    // dryRun, inActions, allowLocalWrites, expected
    [InlineData(false, true, false, true)]    // the cron job: mutating is the whole point
    [InlineData(false, false, false, false)]  // the accident this guard exists for
    [InlineData(false, false, true, true)]    // a human who genuinely means to spend it
    [InlineData(true, true, false, false)]    // --dry-run wins even in Actions
    [InlineData(true, false, true, false)]    // …and even over an explicit opt-in
    public void Mutations_need_either_Actions_or_an_explicit_opt_in(
        bool dryRun, bool inActions, bool allowLocalWrites, bool expected)
    {
        // Someone debugging the bot locally who forgets --dry-run would otherwise push a branch
        // and burn a real-harness dispatch. The read-only half needs no flag, so inspecting what
        // the bot would do stays frictionless.
        Assert.Equal(expected, Options.MutationsAllowed(dryRun, inActions, allowLocalWrites));
    }

    [Fact]
    public void Defaults_point_at_this_repository_and_its_ci_workflow()
    {
        var options = Options.Parse([]);
        Assert.Equal("ci.yml", options.Workflow);
        Assert.Equal(".github/workflows/ci.yml", options.CiPath);
        Assert.Equal("master", options.BaseBranch);
    }

    [Fact]
    public void Model_overrides_are_empty_by_default_so_ci_yml_stays_the_source_of_truth()
    {
        // Duplicating ci.yml's dispatch-input defaults here would mean a server-side slug
        // retirement had to be fixed in two files. Passing nothing lets the workflow's own
        // default apply.
        var options = Options.Parse([]);
        Assert.Null(options.CodexModel);
        Assert.Null(options.OpenCodeModel);

        var overridden = Options.Parse(["--codex-model", "gpt-5.4-codex"]);
        Assert.Equal("gpt-5.4-codex", overridden.CodexModel);
    }

    [Fact]
    public void An_unknown_flag_is_an_error_rather_than_a_silent_no_op()
    {
        // A typo'd --dry-run must never be read as "go ahead and spend".
        Assert.Throws<ArgumentException>(() => Options.Parse(["--dryrun"]));
        Assert.Throws<ArgumentException>(() => Options.Parse(["--repo"]));
    }
}
