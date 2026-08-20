using Landbridge.Core;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// The fixed dummy-task set a new profile is asked to complete. The plane never
/// judges the answers (§2 principle 1 / §7); these texts exist so a real worker
/// following the worker skill can finish without a human, and so the progress
/// view can label each task by kind. Kind is named in the description
/// as <c>(kind: identity)</c> — context, not something dispatch interprets.
/// </summary>
internal static class ConformanceCatalog
{
    public static readonly IReadOnlyList<string> Kinds = ["identity", "write", "shell"];

    public static IReadOnlyList<ConformanceSessionSpec> For(Guid runId)
    {
        var nonce = "lbr-smoke-" + runId.ToString("N")[..8];
        return
        [
            new("identity",
                "This is a Landbridge enrollment check (kind: identity). " +
                "Report this machine's hostname, the process current working directory, " +
                "and the first 8 hex characters of the LANDBRIDGE_SESSION_ID environment variable. " +
                "Do not restate this ask. Call report_result with a reference that includes those three facts."),
            new("write",
                "This is a Landbridge enrollment check (kind: write). " +
                "In this session's working directory, write a file named smoke.txt containing only this machine's hostname " +
                "(one line, no extra text). Call report_result with a path to that file."),
            new("shell",
                "This is a Landbridge enrollment check (kind: shell). " +
                $"Run `echo {nonce}` in a shell and call report_result with exactly that command's output (trimmed)."),
        ];
    }

    public static string? KindOf(string? description)
    {
        if (description is null)
            return null;
        const string marker = "(kind: ";
        var start = description.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = description.IndexOf(')', start);
        if (end < 0)
            return null;
        var kind = description[start..end].Trim();
        return kind.Length == 0 ? null : kind;
    }

    public static string Bucket(SessionState state, MessageState message = MessageState.Idle) =>
        state switch
        {
            SessionState.Completed => "completed",
            SessionState.Rejected or SessionState.Canceled or SessionState.Failed => "failed",
            _ when message == MessageState.AwaitingReport => "reported",
            _ => "pending",
        };
}

internal readonly record struct ConformanceSessionSpec(
    string Kind, string Description);
