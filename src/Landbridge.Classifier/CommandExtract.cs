using System.Text.Json;

namespace Landbridge.Classifier;

public static class CommandExtract
{
    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "shell", "execute", "run_shell_command", "terminal", "cmd",
        "powershell", "sh", "zsh", "local_shell", "shell_command", "bash_tool",
        "run_command", "execute_command",
    };

    private static readonly HashSet<string> BareCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "pwd", "git", "cat", "head", "tail", "wc", "echo", "date",
        "whoami", "uname", "which", "true", "false", "env", "id", "hostname",
    };

    public static string LastSegment(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return "";
        var s = tool.Trim();
        var dunder = s.LastIndexOf("__", StringComparison.Ordinal);
        if (dunder >= 0)
            s = s[(dunder + 2)..];
        else
        {
            var slash = s.LastIndexOf('/');
            if (slash >= 0)
                s = s[(slash + 1)..];
        }
        return s.Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
    }

    public static bool IsNamedShell(string? tool) => ShellTools.Contains(LastSegment(tool));

    public static string? Resolve(string? tool, JsonElement? input)
    {
        return FromInput(input) ?? FromTitle(tool);
    }

    public static string? FromInput(JsonElement? input)
    {
        if (input is not { } el)
            return null;
        el = Unwrap(el);
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        if (el.TryGetProperty("command", out var cmd) || el.TryGetProperty("cmd", out cmd)
            || el.TryGetProperty("argv", out cmd))
        {
            if (cmd.ValueKind == JsonValueKind.String)
                return cmd.GetString();
            if (cmd.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in cmd.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        return null;
                    parts.Add(ShellQuote(item.GetString()!));
                }
                return parts.Count == 0 ? null : string.Join(' ', parts);
            }
        }
        return null;
    }

    public static string? FromTitle(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return null;
        var s = tool.Trim();
        var wrapped = System.Text.RegularExpressions.Regex.Match(
            s, @"^(?:execute|run|shell)\s+`([\s\S]+)`\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (wrapped.Success)
            s = wrapped.Groups[1].Value.Trim();
        if (s.Length == 0)
            return null;
        if (ShellTools.Contains(LastSegment(s)))
            return null;
        if (s.Contains(' ') || s.Contains('\t'))
            return s;
        return BareCommands.Contains(s) ? s : null;
    }

    public static bool IsEmptyInput(JsonElement? input)
    {
        if (input is not { } el)
            return true;
        el = Unwrap(el);
        return el.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => true,
            JsonValueKind.Object => !el.EnumerateObject().Any(),
            JsonValueKind.Array => el.GetArrayLength() == 0,
            JsonValueKind.String => string.IsNullOrWhiteSpace(el.GetString())
                || el.GetString() is "{}" or "null",
            _ => false,
        };
    }

    private static JsonElement Unwrap(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.String)
            return el;
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return el;
        try
        {
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return el;
        }
    }

    private static string ShellQuote(string token)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^[A-Za-z0-9_./:@%+=,-]+$"))
            return token;
        return "'" + token.Replace("'", "'\\''") + "'";
    }
}
