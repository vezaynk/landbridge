using System.Text.RegularExpressions;

namespace Landbridge.Classifier;

/// <summary>
/// Ask (never Deny) on a small list of discard/destroy commands. A match skips
/// the LLM so a model outage cannot wave them through.
/// </summary>
public static class DestroyGuard
{
    private static readonly Regex[] DestructiveGit =
    [
        new(@"\bgit\s+reset\s+--hard\b", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bgit\s+checkout\s+--\s+\.", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bgit\s+clean\s+-[a-zA-Z]*f", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bgit\s+stash\s+drop\b", RegexOptions.CultureInvariant | RegexOptions.Compiled),
    ];

    private static readonly Regex GitAmend =
        new(@"\bgit\s+commit\s+--amend\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex[] IacDestroy =
    [
        new(@"\bterraform\s+destroy\b", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bpulumi\s+destroy\b", RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"\bcdk\s+destroy\b", RegexOptions.CultureInvariant | RegexOptions.Compiled),
    ];

    private static readonly Regex ShellC =
        new(@"(?:^|\s)(?:bash|sh|zsh|fish|dash|ksh)\s+-[a-zA-Z]*c\s+(?:""([^""]*)""|'([^']*)')",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static (bool Blocked, string Reason) Match(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return (false, "");

        var expanded = command + " " + ShellC.Replace(command, m =>
            " " + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));

        foreach (var pattern in DestructiveGit)
        {
            var m = pattern.Match(expanded);
            if (m.Success)
                return (true, $"Destructive git command: \"{m.Value}\".");
        }

        if (GitAmend.IsMatch(expanded))
            return (true, "Blocked \"git commit --amend\" (not known to be this session's commit).");

        foreach (var pattern in IacDestroy)
        {
            if (pattern.IsMatch(expanded))
            {
                var tool = pattern.Match(command).Value.Split(' ', 2)[0];
                return (true, $"Infrastructure destroy command: \"{tool} destroy\".");
            }
        }

        return (false, "");
    }
}
