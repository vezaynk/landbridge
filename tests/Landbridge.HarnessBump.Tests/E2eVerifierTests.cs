using System.Text.RegularExpressions;

namespace Landbridge.HarnessBump.Tests;

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
        // Display names are `real-${{ matrix.harness }}-e2e`. The merge gate matches those
        // names on the GitHub Jobs API, so each waited-on job must have a matrix row.
        var yaml = RepoFiles.CiYaml;
        Assert.Contains("name: real-${{ matrix.harness }}-e2e", yaml);
        foreach (var name in E2eVerifier.RealHarnessJobs)
        {
            Assert.True(name.StartsWith("real-", StringComparison.Ordinal) && name.EndsWith("-e2e", StringComparison.Ordinal),
                $"RealHarnessJobs entry '{name}' is not real-<harness>-e2e");
            var harness = name["real-".Length..^"-e2e".Length];
            Assert.Contains($"harness: {harness}", yaml);
        }
    }

    [Fact]
    public void The_gate_covers_every_real_harness_job_ci_yml_defines()
    {
        // The other direction: adding a matrix cell without adding it here would let the
        // bot merge a bump that the new cell had never approved.
        foreach (var harness in MatrixHarnessNames(RepoFiles.CiYaml))
            Assert.Contains($"real-{harness}-e2e", E2eVerifier.RealHarnessJobs);
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

    /// <summary>
    /// <c>harness: &lt;name&gt;</c> rows in the real-e2e matrix include. Comments that
    /// mention the word are skipped. Good enough, and no YAML dependency.
    /// </summary>
    private static List<string> MatrixHarnessNames(string yaml)
    {
        var names = new List<string>();
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
                continue;
            var match = Regex.Match(trimmed, @"^(?:-\s+)?harness:\s+(?<name>[a-z]+)\s*$");
            if (match.Success)
                names.Add(match.Groups["name"].Value);
        }
        Assert.NotEmpty(names);
        return names;
    }
}
