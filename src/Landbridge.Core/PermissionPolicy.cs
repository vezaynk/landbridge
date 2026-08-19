using System.Text.RegularExpressions;

namespace Landbridge.Core;

/// <summary>
/// What the plane does with a permission request before it becomes a Lead/human
/// wait. Protocol and worker-runtime tools are how a session runs, not user
/// tools — a Lead should never have to click them. Reads and writes that stay
/// inside this session's directory are the worker's own workspace. Everything
/// else goes to the bridge. The plane never auto-denies.
/// </summary>
public enum PermissionDisposition
{
    AutoAllow,
    Ask,
}

public static class PermissionPolicy
{
    public static PermissionDisposition Classify(string tool, string proposedInput) =>
        Classify(tool, proposedInput, session: default);

    public static PermissionDisposition Classify(string tool, string proposedInput, SessionId session)
    {
        if (IsProtocolOrRuntimeTool(tool) || NamesProtocolTool(proposedInput))
            return PermissionDisposition.AutoAllow;

        if (session.Value != Guid.Empty
            && IsFileReadOrWrite(tool)
            && AllPathsStayInSession(proposedInput, session))
            return PermissionDisposition.AutoAllow;

        return PermissionDisposition.Ask;
    }

    internal static bool IsProtocolOrRuntimeTool(string tool)
    {
        var n = LastSegment(tool);
        return n is "get_session" or "report_result" or "request_input"
            or "start_process" or "stop_process" or "list_processes" or "write_process"
            or "register_service" or "list_services" or "open_forward" or "open_preview"
            or "open_lead_forward";
    }

    internal static bool NamesProtocolTool(string proposedInput)
    {
        if (string.IsNullOrWhiteSpace(proposedInput))
            return false;
        foreach (var name in ProtocolHints)
        {
            if (proposedInput.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly string[] ProtocolHints =
    [
        "get_session", "report_result", "request_input",
        "mcp__landbridge__", "landbridge__get_session", "landbridge_get_session",
    ];

    private static bool IsFileReadOrWrite(string tool)
    {
        var n = LastSegment(tool);
        return n is "read" or "write" or "edit" or "notebookedit" or "notebook_edit"
            or "read_file" or "write_file" or "search_replace" or "strreplace"
            or "glob" or "grep" or "ls" or "list_dir";
    }

    private static bool AllPathsStayInSession(string proposedInput, SessionId session)
    {
        var paths = ExtractPaths(proposedInput);
        if (paths.Count == 0)
            return false;
        foreach (var path in paths)
        {
            if (!StaysInSession(path, session))
                return false;
        }
        return true;
    }

    private static List<string> ExtractPaths(string input)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return found;
        foreach (Match m in PathKeys.Matches(input))
        {
            var value = m.Groups[1].Value;
            if (value.Length > 0)
                found.Add(value);
        }
        return found;
    }

    private static readonly Regex PathKeys = new(
        "(?i)\"(?:path|file_path|filepath|filename|old_path|new_path|target_file|targetfile)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool StaysInSession(string path, SessionId session)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var p = path.Trim().Replace('\\', '/');
        if (p.StartsWith('~') || p.StartsWith("$HOME", StringComparison.OrdinalIgnoreCase))
            return false;
        if (HasDotDotSegment(p))
            return false;

        // landbridged starts the worker in {work_root}/{session_id}. A relative
        // path is that directory. An absolute path is only in-session when the
        // session id is a directory segment (N or D spelling).
        if (!p.StartsWith('/') && !(p.Length >= 3 && p[1] == ':' && p[2] == '/'))
            return true;

        var n = session.Value.ToString("N");
        var d = session.Value.ToString("D");
        return HasDirSegment(p, n) || HasDirSegment(p, d);
    }

    private static bool HasDotDotSegment(string path)
    {
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
                return true;
        }
        return false;
    }

    private static bool HasDirSegment(string path, string segment)
    {
        var needle = "/" + segment;
        var i = path.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var after = i + needle.Length;
            if (after == path.Length || path[after] == '/')
                return true;
            i = path.IndexOf(needle, after, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string LastSegment(string tool)
    {
        var s = tool.Trim();
        var dunder = s.LastIndexOf("__", StringComparison.Ordinal);
        if (dunder >= 0)
            s = s[(dunder + 2)..];
        else
        {
            var slash = s.LastIndexOf('/');
            if (slash >= 0)
                s = s[(slash + 1)..];
            else if (s.StartsWith("landbridge_", StringComparison.OrdinalIgnoreCase))
                s = s["landbridge_".Length..];
        }
        return s.Replace('-', '_').Replace(' ', '_').ToLowerInvariant();
    }
}
