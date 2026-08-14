namespace Docket.HarnessBump;

/// <summary>A single package moving from one pinned version to another.</summary>
public sealed record Bump(HarnessPackage Package, string From, string To)
{
    public override string ToString() => $"{Package.Short} {From} → {To}";
}

/// <summary>
/// Encodes a bump set into a branch name, and reads it back out.
/// </summary>
/// <remarks>
/// The branch name is how the bot knows what its ONE open PR targets, without having to fetch and
/// parse that PR's ci.yml. It is a label on live state, not a history: nothing reads the names of
/// closed or merged bump branches, because the bot keeps no record of what it has already tried.
/// </remarks>
public static class BumpBranch
{
    public const string Prefix = "harness-bump/";

    /// <summary>
    /// <c>harness-bump/claude-2.1.232_opencode-1.18.19</c>. Sorted by short name so the same bump
    /// set always produces the same branch name regardless of enumeration order.
    /// </summary>
    public static string Name(IEnumerable<Bump> bumps)
    {
        var markers = bumps
            .Select(b => Marker(b.Package, b.To))
            .OrderBy(m => m, StringComparer.Ordinal);
        return Prefix + string.Join('_', markers);
    }

    public static string Marker(HarnessPackage package, string version) => $"{package.Short}-{version}";

    /// <summary>True for any branch the bot would have created.</summary>
    public static bool IsBumpBranch(string branchName) => Strip(branchName).StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The versions a bump branch name says it targets, as npm package name → version.
    /// </summary>
    /// <remarks>
    /// Segment-exact by construction: splitting on <c>_</c> and matching a known short name plus a
    /// parseable version is what stops a hand-edited or unrelated branch name from being read as a
    /// target set. Markers that do not resolve are dropped, and a branch that yields nothing makes
    /// the planner refuse to act rather than guess.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> TargetsIn(string branchName)
    {
        var targets = new Dictionary<string, string>();
        var name = Strip(branchName);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal))
            return targets;

        foreach (var marker in name[Prefix.Length..].Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = marker.IndexOf('-');
            if (dash <= 0)
                continue;
            var package = HarnessPackages.All.FirstOrDefault(p => p.Short == marker[..dash]);
            var version = marker[(dash + 1)..];
            if (package is null || !SemVer.TryParse(version, out _))
                continue;
            targets[package.NpmName] = version;
        }
        return targets;
    }

    /// <summary>Tolerates the fully-qualified forms git and the API hand back.</summary>
    private static string Strip(string branchName)
    {
        var name = branchName.Trim();
        string[] refPrefixes = ["refs/heads/", "origin/"];
        foreach (var refPrefix in refPrefixes)
            if (name.StartsWith(refPrefix, StringComparison.Ordinal))
                name = name[refPrefix.Length..];
        return name;
    }
}
