using Landbridge.Core;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// The fixed dummy-task set a new profile is asked to complete. The plane never
/// judges the answers (§2 principle 1 / §7); these texts exist so a real worker
/// following the worker skill can finish without a human, and so the progress
/// view can label each task by kind. Kind is stored on the task's
/// opaque <c>workspace</c> as <c>conformance/{kind}</c> — context, not
/// something dispatch interprets.
/// </summary>
internal static class ConformanceCatalog
{
    public const string WorkspacePrefix = "conformance/";

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

    public static string WorkspaceOf(string kind) => WorkspacePrefix + kind;

    public static string? KindOf(string? workspace) =>
        workspace is not null
        && workspace.StartsWith(WorkspacePrefix, StringComparison.Ordinal)
            ? workspace[WorkspacePrefix.Length..]
            : null;

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
