namespace Landbridge.Classifier;

/// <summary>
/// Cheap first gate: a command is auto-allowed only when it is a simple argv
/// (no shell metacharacters) whose program is on a small read-only allowlist.
/// Anything clever — pipes, redirects, substitutions — falls through to the
/// destroy-guard and then the LLM.
/// </summary>
public static class ArgvAllowlist
{
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "cat", "head", "tail", "wc", "pwd", "echo", "which", "where",
        "whoami", "basename", "dirname", "printenv", "stat", "df", "du", "ps",
        "column", "cut", "date", "true", "false", "id", "hostname", "uname",
        "git", "grep",
    };

    private static readonly HashSet<string> GitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "blame", "grep", "ls-files",
        "rev-parse", "describe", "cat-file", "version", "help",
    };

    /// <summary>
    /// Characters that mean this is a shell expression, not a simple argv.
    /// </summary>
    internal static readonly char[] Meta =
        ['|', '&', ';', '`', '$', '(', ')', '{', '}', '<', '>', '\n', '\r',
         '*', '?', '[', ']', '!', '#', '~', '\'', '"', '\\'];

    public static bool IsSimpleAllowlisted(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;
        var trimmed = command.Trim();
        if (trimmed.IndexOfAny(Meta) >= 0)
            return false;

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        var program = Path.GetFileName(tokens[0]);
        if (string.IsNullOrEmpty(program) || !Programs.Contains(program))
            return false;

        if (!program.Equals("git", StringComparison.OrdinalIgnoreCase))
            return true;

        return GitReadOnly(tokens.AsSpan(1));
    }

    private static bool GitReadOnly(ReadOnlySpan<string> args)
    {
        if (args.Length == 0)
            return false;

        var i = 0;
        while (i < args.Length && args[i].StartsWith('-'))
            i++;

        if (i >= args.Length)
        {
            foreach (var flag in args)
            {
                if (flag is "--version" or "--help" or "-h")
                    return true;
            }
            return false;
        }

        return GitSubcommands.Contains(args[i]);
    }
}
