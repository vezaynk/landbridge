namespace Docket.HarnessBump.Tests;

public class CiWorkflowPinsTests
{
    /// <summary>
    /// A miniature of ci.yml's real shape: the claude install repeated across three jobs (which
    /// ci.yml requires stay in lockstep), and — critically — comment lines that TALK about the
    /// install commands. ci.yml really contains `# WHY \`npm install -g opencode-ai\` AND NOT …`,
    /// so a parser that matched on the command text alone would "bump" prose.
    /// </summary>
    private const string Fixture = """
        jobs:
          real-claude-e2e:
            steps:
              # Deliberately pinned; see the note above.
              - name: Install claude CLI
                run: npm install -g @anthropic-ai/claude-code@2.1.229

          real-codex-e2e:
            steps:
              # WHY `npm install -g opencode-ai` AND NOT THE VENDOR'S INSTALLER: prose, not a step.
              #        run: npm install -g @openai/codex@9.9.9
              - name: Install codex CLI
                run: npm install -g @openai/codex@0.147.0
              - name: Install claude CLI
                run: npm install -g @anthropic-ai/claude-code@2.1.229

          real-opencode-e2e:
            steps:
              - name: Install opencode CLI
                run: npm install -g opencode-ai@1.18.17
              - name: Install claude CLI
                run: npm install -g @anthropic-ai/claude-code@2.1.229
              - name: Something else entirely
                run: npm install -g typescript@5.0.0
        """;

    [Fact]
    public void Finds_every_harness_install_and_ignores_prose_and_other_packages()
    {
        var installs = CiWorkflowPins.Parse(Fixture);

        Assert.Equal(3, installs.Count(i => i.Package == "@anthropic-ai/claude-code"));
        Assert.Single(installs, i => i.Package == "@openai/codex");
        Assert.Single(installs, i => i.Package == "opencode-ai");
        // typescript is a real install line but not a harness, so the bot has no opinion on it.
        Assert.DoesNotContain(installs, i => i.Package == "typescript");
        // The two commented-out/quoted commands must not have been counted.
        Assert.Equal(5, installs.Count);
    }

    [Fact]
    public void Reports_line_numbers_that_point_at_the_real_lines()
    {
        var installs = CiWorkflowPins.Parse(Fixture);
        var lines = Fixture.Split('\n');
        foreach (var install in installs)
            Assert.Contains(install.Package, lines[install.LineNumber - 1]);
    }

    [Fact]
    public void Splits_scoped_names_from_versions()
    {
        var pins = CiWorkflowPins.PinnedVersions(CiWorkflowPins.Parse(Fixture));
        Assert.Equal("2.1.229", pins["@anthropic-ai/claude-code"]);
        Assert.Equal("0.147.0", pins["@openai/codex"]);
        Assert.Equal("1.18.17", pins["opencode-ai"]);
    }

    [Theory]
    [InlineData("        run: npm install -g @openai/codex", "@openai/codex")]
    [InlineData("        run: npm install -g opencode-ai", "opencode-ai")]
    public void An_unpinned_install_reads_as_unpinned_not_as_a_version(string line, string package)
    {
        // A scoped name's leading @ is the scope sigil, not a version separator — getting this
        // wrong would read `@openai/codex` as package "" at version "openai/codex".
        var installs = CiWorkflowPins.Parse(line);
        var install = Assert.Single(installs);
        Assert.Equal(package, install.Package);
        Assert.Null(install.Version);
    }

    [Fact]
    public void One_package_pinned_two_ways_is_an_error_rather_than_a_choice()
    {
        // ci.yml's three claude installs must stay in lockstep: a mixed-fleet fact running a
        // different claude than the claude tier would make one tier's green say nothing about
        // the other's. So a split pin is surfaced, never silently reconciled.
        const string split = """
                  run: npm install -g @anthropic-ai/claude-code@2.1.229
                  run: npm install -g @anthropic-ai/claude-code@2.1.231
            """;
        var error = Assert.Throws<InvalidOperationException>(
            () => CiWorkflowPins.PinnedVersions(CiWorkflowPins.Parse(split)));
        Assert.Contains("lockstep", error.Message);
    }

    [Fact]
    public void Rewriting_claude_moves_all_three_installs_together()
    {
        var updated = CiWorkflowPins.Rewrite(Fixture,
            new Dictionary<string, string> { ["@anthropic-ai/claude-code"] = "2.1.240" });

        Assert.Equal(3, Occurrences(updated, "@anthropic-ai/claude-code@2.1.240"));
        Assert.DoesNotContain("@anthropic-ai/claude-code@2.1.229", updated);
        // Untouched packages stay exactly as they were.
        Assert.Contains("@openai/codex@0.147.0", updated);
        Assert.Contains("opencode-ai@1.18.17", updated);
        Assert.Contains("typescript@5.0.0", updated);
    }

    [Fact]
    public void Rewriting_preserves_indentation_comments_and_the_rest_of_the_file()
    {
        var updated = CiWorkflowPins.Rewrite(Fixture,
            new Dictionary<string, string> { ["opencode-ai"] = "1.18.18" });

        Assert.Contains("      - name: Install opencode CLI\n        run: npm install -g opencode-ai@1.18.18", updated);
        Assert.Contains("# Deliberately pinned; see the note above.", updated);
        Assert.Contains("# WHY `npm install -g opencode-ai` AND NOT THE VENDOR'S INSTALLER", updated);
        // Only the one token changed.
        Assert.Equal(Fixture.Split('\n').Length, updated.Split('\n').Length);
    }

    [Fact]
    public void Rewriting_several_packages_at_once_produces_one_edit_per_line()
    {
        var updated = CiWorkflowPins.Rewrite(Fixture, new Dictionary<string, string>
        {
            ["@anthropic-ai/claude-code"] = "2.1.240",
            ["opencode-ai"] = "1.18.18",
        });
        Assert.Equal(3, Occurrences(updated, "@anthropic-ai/claude-code@2.1.240"));
        Assert.Contains("opencode-ai@1.18.18", updated);
        Assert.Contains("@openai/codex@0.147.0", updated);
    }

    [Fact]
    public void Rewriting_an_unpinned_install_pins_it()
    {
        var updated = CiWorkflowPins.Rewrite(
            "        run: npm install -g opencode-ai",
            new Dictionary<string, string> { ["opencode-ai"] = "1.18.18" });
        Assert.Equal("        run: npm install -g opencode-ai@1.18.18", updated);
    }

    [Fact]
    public void Rewriting_a_package_the_file_does_not_install_throws()
    {
        // Writing nothing would hand the caller a no-op commit and then an e2e run that
        // verified the OLD version — a false green on the most expensive check there is.
        var error = Assert.Throws<InvalidOperationException>(() => CiWorkflowPins.Rewrite(
            "        run: npm install -g opencode-ai@1.18.17",
            new Dictionary<string, string> { ["@openai/codex"] = "0.148.0" }));
        Assert.Contains("@openai/codex", error.Message);
    }

    [Fact]
    public void Crlf_line_endings_survive_a_rewrite()
    {
        var crlf = "        run: npm install -g opencode-ai@1.18.17\r\n        next: line\r\n";
        var updated = CiWorkflowPins.Rewrite(crlf,
            new Dictionary<string, string> { ["opencode-ai"] = "1.18.18" });
        Assert.Equal("        run: npm install -g opencode-ai@1.18.18\r\n        next: line\r\n", updated);
    }

    // ── against the real file ────────────────────────────────────────────────────────────

    [Fact]
    public void The_repositorys_own_ci_yml_pins_all_three_harnesses()
    {
        // The guard that matters most: it fails if someone unpins an install, adds a fourth
        // harness without a pin, or splits the claude lockstep — each of which would either
        // stop the bot dead or make it bump the wrong thing. Deliberately asserts SHAPE, not
        // versions, so the bot merging its own bumps never breaks this test.
        var installs = CiWorkflowPins.Parse(RepoFiles.CiYaml);
        var pins = CiWorkflowPins.PinnedVersions(installs);

        foreach (var package in HarnessPackages.All)
        {
            Assert.True(pins.ContainsKey(package.NpmName),
                $"ci.yml no longer installs {package.NpmName}");
            Assert.False(pins[package.NpmName] is null,
                $"ci.yml installs {package.NpmName} UNPINNED — the bump bot can only track pinned installs.");
            Assert.True(SemVer.TryParse(pins[package.NpmName], out _),
                $"{package.NpmName} is pinned to '{pins[package.NpmName]}', which is not a semver.");
        }

        // The mixed-fleet facts need claude in all three real jobs.
        Assert.Equal(3, installs.Count(i => i.Package == HarnessPackages.Claude.NpmName));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
