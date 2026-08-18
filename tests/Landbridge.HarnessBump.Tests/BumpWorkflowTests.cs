using System.Text.RegularExpressions;

namespace Landbridge.HarnessBump.Tests;

/// <summary>
/// Guards the shape of the cron workflow itself. The repo-wide rule is that tooling is C# and
/// external processes are argv spawns — never shell (spec §10) — and a bump bot is exactly the kind
/// of job that invites "just one line of bash" to creep back into a <c>run:</c> block over time.
/// This makes that creep fail a test rather than pass review.
/// </summary>
public class BumpWorkflowTests
{
    [Fact]
    public void The_only_command_the_workflow_runs_is_the_dotnet_tool()
    {
        var commands = RunSteps(RepoFiles.BumpWorkflowYaml);
        var command = Assert.Single(commands);
        Assert.Equal("dotnet run --project tools/Landbridge.HarnessBump -c Release", command);
    }

    [Fact]
    public void No_run_step_contains_shell_control_flow_or_a_block_scalar()
    {
        // A block scalar is where logic hides. Nothing in this workflow should need one. Checked
        // against step lines only — the file's own header comment discusses `run:` blocks in prose.
        var yaml = RepoFiles.BumpWorkflowYaml;
        Assert.DoesNotContain("|", string.Join('\n', RunSteps(yaml)));
        foreach (var command in RunSteps(yaml))
            foreach (var shellism in (string[])["&&", "||", ";", "$(", "`", "if ", "for ", "|"])
                Assert.False(command.Contains(shellism, StringComparison.Ordinal),
                    $"`{command}` contains shell construct `{shellism}` — put the logic in the C# tool.");
    }

    [Fact]
    public void It_runs_daily_and_can_also_be_dispatched_by_hand()
    {
        var yaml = RepoFiles.BumpWorkflowYaml;
        Assert.Contains("schedule:", yaml);
        Assert.Matches(new Regex(@"- cron: ""[\d*/ ,-]+"""), yaml);
        Assert.Contains("workflow_dispatch:", yaml);
        // The manual escape hatch that makes a run cost nothing.
        Assert.Contains("dry_run", yaml);
    }

    [Fact]
    public void The_dry_run_switch_reaches_the_tool_as_the_env_var_it_reads()
    {
        // The workflow deliberately passes no arguments, so this env var is the only wiring — if it
        // is renamed on one side only, a dispatched "dry run" silently spends money.
        Assert.Contains("LANDBRIDGE_BUMP_DRY_RUN:", RepoFiles.BumpWorkflowYaml);
        Assert.True(Options.Parse([]) is { DryRun: false });
        Environment.SetEnvironmentVariable("LANDBRIDGE_BUMP_DRY_RUN", "true");
        try
        {
            Assert.True(Options.Parse([]).DryRun);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LANDBRIDGE_BUMP_DRY_RUN", null);
        }
    }

    [Fact]
    public void It_uses_a_pat_secret_rather_than_the_default_github_token()
    {
        // GITHUB_TOKEN cannot open the PR on this repo: `can_approve_pull_request_reviews` is off
        // and `default_workflow_permissions` is read. Falling back to it would fail every run.
        var yaml = RepoFiles.BumpWorkflowYaml;
        Assert.Contains("secrets.HARNESS_BUMP_TOKEN", yaml);
        Assert.DoesNotContain("secrets.GITHUB_TOKEN", yaml);
    }

    /// <summary>The scalar of every <c>run:</c> step in the file.</summary>
    private static List<string> RunSteps(string yaml) =>
        [.. yaml.Split('\n')
            .Select(line => line.TrimEnd('\r').TrimStart())
            .Where(line => line.StartsWith("run:", StringComparison.Ordinal) ||
                           line.StartsWith("- run:", StringComparison.Ordinal))
            .Select(line => line[(line.IndexOf("run:", StringComparison.Ordinal) + 4)..].Trim())];
}
