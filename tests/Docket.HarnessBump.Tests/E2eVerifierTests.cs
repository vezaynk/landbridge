using System.Text.RegularExpressions;

namespace Docket.HarnessBump.Tests;

/// <summary>
/// Guards the coupling between the bot's merge gate and ci.yml's job names. The gate is
/// fail-closed, so a renamed job does not merge anything unsafe — it makes the bot report
/// "did not run" forever and quietly stop landing bumps, which is the kind of breakage nobody
/// notices for a month.
/// </summary>
public class E2eVerifierTests
{
    [Fact]
    public void Every_job_the_gate_waits_on_really_exists_in_ci_yml()
    {
        var jobs = TopLevelJobNames(RepoFiles.CiYaml);
        foreach (var name in E2eVerifier.RealHarnessJobs)
            Assert.Contains(name, jobs);
    }

    [Fact]
    public void The_gate_covers_every_real_harness_job_ci_yml_defines()
    {
        // The other direction: adding a fourth real-* tier to ci.yml without adding it here would
        // let the bot merge a bump that the new tier had never approved.
        var realJobs = TopLevelJobNames(RepoFiles.CiYaml)
            .Where(j => j.StartsWith("real-", StringComparison.Ordinal));
        foreach (var job in realJobs)
            Assert.Contains(job, E2eVerifier.RealHarnessJobs);
    }

    [Fact]
    public void The_real_tiers_are_still_workflow_dispatch_gated()
    {
        // This is the fact that makes poll-then-merge necessary instead of GitHub's auto-merge:
        // dispatch-gated jobs never appear as checks on a pull_request event, so auto-merge would
        // land the bump on build-test + chaos alone. If this ever stops being true, revisit the
        // choice — see E2eVerifier's remarks.
        Assert.Contains("if: github.event_name == 'workflow_dispatch'", RepoFiles.CiYaml);
    }

    /// <summary>Keys indented exactly two spaces under <c>jobs:</c> — good enough, and no YAML dependency.</summary>
    private static List<string> TopLevelJobNames(string yaml)
    {
        var names = new List<string>();
        var inJobs = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("jobs:", StringComparison.Ordinal))
            {
                inJobs = true;
                continue;
            }
            if (!inJobs)
                continue;
            // A non-indented, non-blank, non-comment line ends the jobs block.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
                break;
            var match = Regex.Match(line, @"^  (?<name>[A-Za-z0-9_-]+):\s*$");
            if (match.Success)
                names.Add(match.Groups["name"].Value);
        }
        Assert.NotEmpty(names);
        return names;
    }
}
