namespace Landbridge.HarnessBump;

/// <summary>
/// One BYO-harness CLI: the npm package ci.yml installs, and the short name used
/// in bump-branch markers (see <see cref="BumpBranch"/>) and log lines.
/// </summary>
/// <param name="NpmName">The registry name, exactly as it appears after <c>npm install -g</c>.</param>
/// <param name="Short">Marker/log name. Must be stable — it is part of branch names the
/// dedup check reads back, so renaming one silently un-dedups every past bump.</param>
public sealed record HarnessPackage(string NpmName, string Short);

/// <summary>The three harnesses ci.yml installs. Adding a fourth is an entry here plus a pin in ci.yml.</summary>
public static class HarnessPackages
{
    public static readonly HarnessPackage Claude = new("@anthropic-ai/claude-code", "claude");
    public static readonly HarnessPackage Codex = new("@openai/codex", "codex");
    public static readonly HarnessPackage OpenCode = new("opencode-ai", "opencode");

    public static readonly IReadOnlyList<HarnessPackage> All = [Claude, Codex, OpenCode];

    public static HarnessPackage? ByNpmName(string npmName) =>
        All.FirstOrDefault(p => p.NpmName == npmName);
}
