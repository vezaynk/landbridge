using System.Text.RegularExpressions;

namespace Landbridge.Core;

/// <summary>
/// What the plane does with a permission request before it becomes a Lead/human
/// wait. Protocol and worker-runtime tools are how a session runs, not user
/// tools — a Lead should never have to click them. Credential and $HOME writes
/// are refused. Everything else still goes to the bridge.
/// </summary>
public enum PermissionDisposition
{
    AutoAllow,
    Ask,
    AutoDeny,
}

public static class PermissionPolicy
{
    public static PermissionDisposition Classify(string tool, string proposedInput)
    {
        if (IsProtocolOrRuntimeTool(tool) || NamesProtocolTool(proposedInput))
            return PermissionDisposition.AutoAllow;

        if (LooksLikeCredentialOrPrivilege(tool, proposedInput)
            || LooksLikeHomeOrSystemWrite(proposedInput))
            return PermissionDisposition.AutoDeny;

        return PermissionDisposition.Ask;
    }

    public static string AutoDenyMessage(string tool) =>
        $"Landbridge refused '{tool}' without asking a Lead: it looks like a credential, "
        + "privilege, or $HOME/system write. Stay in this session's directory; report "
        + "structured auth facts instead of reaching for keys.";

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

    private static bool LooksLikeCredentialOrPrivilege(string tool, string input)
    {
        var hay = tool + "\n" + input;
        return Privilege.IsMatch(hay);
    }

    private static bool LooksLikeHomeOrSystemWrite(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;
        return HomeOrSystem.IsMatch(input);
    }

    private static readonly Regex Privilege = new(
        @"sudo\b|doas\b|security\s+find-|ssh-add\b|ssh-keygen\b|/etc/shadow|\.aws/|\.gnupg/|\.ssh/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HomeOrSystem = new(
        @"[""']?(?:~|/Users/|/home/|/root/|/etc/|/private/etc/)[^""'\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
