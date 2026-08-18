namespace Landbridge.HarnessBump;

/// <summary>One <c>run: npm install -g &lt;pkg&gt;[@&lt;ver&gt;]</c> line found in a workflow file.</summary>
/// <param name="Version">Null when the line installs the package unpinned.</param>
/// <param name="LineNumber">1-based, so it can be quoted straight into a log line.</param>
public sealed record PinnedInstall(string Package, string? Version, int LineNumber);

/// <summary>
/// Reads and rewrites the harness-CLI pins in <c>.github/workflows/ci.yml</c>.
/// </summary>
/// <remarks>
/// Deliberately line-oriented rather than a YAML round-trip: the pins live in
/// <c>run:</c> scalars surrounded by ~80 lines of load-bearing comments (the whole
/// argument for each pin is written there), and every YAML serializer this repo could
/// take a dependency on would reflow or drop them. Editing the one token that changes,
/// in place, keeps the reasoning intact and keeps the diff reviewable.
/// </remarks>
public static class CiWorkflowPins
{
    private const string InstallPrefix = "npm install -g ";

    /// <summary>Every harness install line, in file order, including unpinned ones.</summary>
    public static IReadOnlyList<PinnedInstall> Parse(string yaml)
    {
        var found = new List<PinnedInstall>();
        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var install = ParseInstallLine(line);
            if (install is null)
                continue;
            var (package, version) = install.Value;
            // Only the three harnesses are the bot's business; ci.yml is free to
            // `npm install -g` anything else without this tool having an opinion.
            if (HarnessPackages.ByNpmName(package) is null)
                continue;
            found.Add(new PinnedInstall(package, version, i + 1));
        }
        return found;
    }

    /// <summary>
    /// The single pinned version per package. Throws when one package is pinned to two
    /// different versions across its install lines. ci.yml now has one claude <c>run:</c>
    /// shared by mixed-fleet cells via <c>if:</c>, so lockstep is structural; a split pin
    /// is still a bug to surface if a second line ever appears.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> PinnedVersions(IReadOnlyList<PinnedInstall> installs)
    {
        var pins = new Dictionary<string, string?>();
        foreach (var group in installs.GroupBy(i => i.Package))
        {
            var versions = group.Select(i => i.Version).Distinct().ToList();
            if (versions.Count > 1)
                throw new InvalidOperationException(
                    $"{group.Key} is installed at more than one version in ci.yml " +
                    $"({string.Join(", ", versions.Select(v => v ?? "<unpinned>"))}, " +
                    $"lines {string.Join("/", group.Select(i => i.LineNumber))}). " +
                    "These installs must stay in lockstep — fix ci.yml before bumping.");
            pins[group.Key] = versions[0];
        }
        return pins;
    }

    /// <summary>
    /// Rewrites every install line for each package in <paramref name="bumps"/> to the new
    /// version, preserving indentation and the rest of the line.
    /// </summary>
    /// <param name="bumps">npm package name → version to pin.</param>
    /// <exception cref="InvalidOperationException">A package in <paramref name="bumps"/> has
    /// no install line in the file — silently writing nothing would hand the caller a
    /// no-op commit and an e2e run that verified the OLD version.</exception>
    public static string Rewrite(string yaml, IReadOnlyDictionary<string, string> bumps)
    {
        // Preserve the file's own line endings: splitting on '\n' and rejoining with '\n'
        // keeps any '\r' as part of the line content, so CRLF survives untouched.
        var lines = yaml.Split('\n');
        var rewritten = bumps.Keys.ToDictionary(k => k, _ => 0);

        for (var i = 0; i < lines.Length; i++)
        {
            var carriageReturn = lines[i].EndsWith('\r');
            var line = carriageReturn ? lines[i][..^1] : lines[i];

            var install = ParseInstallLine(line);
            if (install is null)
                continue;
            var (package, _) = install.Value;
            if (!bumps.TryGetValue(package, out var newVersion))
                continue;

            var at = line.IndexOf(InstallPrefix, StringComparison.Ordinal);
            var head = line[..(at + InstallPrefix.Length)];
            lines[i] = $"{head}{package}@{newVersion}" + (carriageReturn ? "\r" : "");
            rewritten[package]++;
        }

        var missing = rewritten.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"no `{InstallPrefix}` line found for {string.Join(", ", missing)} — " +
                "ci.yml does not install it, so there is nothing to bump.");

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Splits <c>@anthropic-ai/claude-code@2.1.229</c> into package and version.
    /// </summary>
    /// <remarks>
    /// The split is on the LAST <c>@</c>, and only when it is not at index 0 — which is what
    /// makes scoped names work: <c>@openai/codex</c> reads as unpinned (its only <c>@</c> is the
    /// scope sigil) while <c>@openai/codex@0.147.0</c> reads as pinned.
    /// </remarks>
    private static (string Package, string? Version)? ParseInstallLine(string line)
    {
        var trimmed = line.Trim();
        // Comment lines discuss these very commands (`# WHY \`npm install -g opencode-ai\` ...`),
        // so a prefix check alone would match prose and "bump" a comment.
        if (trimmed.StartsWith('#'))
            return null;
        if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            return null;

        var command = trimmed["run:".Length..].Trim();
        if (!command.StartsWith(InstallPrefix, StringComparison.Ordinal))
            return null;

        var spec = command[InstallPrefix.Length..].Trim();
        if (spec.Length == 0 || spec.Contains(' '))
            return null;

        var at = spec.LastIndexOf('@');
        return at > 0
            ? (spec[..at], spec[(at + 1)..])
            : (spec, null);
    }
}
