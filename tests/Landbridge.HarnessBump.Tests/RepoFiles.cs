namespace Landbridge.HarnessBump.Tests;

/// <summary>
/// Locates the repository's own files so a few tests can assert against the REAL workflows rather
/// than only fixtures. Those tests are the ones that catch a human edit the bot cannot cope with —
/// an unpinned install, a split claude pin, a renamed job the merge gate waits on.
/// </summary>
internal static class RepoFiles
{
    /// <summary>Walks up from the test binary until it finds the solution file.</summary>
    public static string Root { get; } = FindRoot();

    public static string CiYaml => File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));

    public static string BumpWorkflowYaml =>
        File.ReadAllText(Path.Combine(Root, ".github", "workflows", "harness-bump.yml"));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "landbridge.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            $"no landbridge.slnx above {AppContext.BaseDirectory} — cannot locate the repository root.");
    }
}
