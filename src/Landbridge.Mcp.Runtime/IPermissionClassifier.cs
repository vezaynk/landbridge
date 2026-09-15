using Landbridge.Core;

namespace Landbridge.Mcp;

/// <summary>
/// Optional plane-side classifier consulted after
/// <see cref="PermissionPolicy"/> returns Ask and before a Lead wait opens.
/// Implementations must never Deny: unknown, error, and timeout are Ask.
/// </summary>
public interface IPermissionClassifier
{
    Task<PermissionDisposition> ClassifyAsync(
        SessionId session, string tool, string proposedInput,
        IReadOnlyList<string> leadMessages, CancellationToken ct);
}

/// <summary>No classifier configured: every call Asks.</summary>
public sealed class NullPermissionClassifier : IPermissionClassifier
{
    public static NullPermissionClassifier Instance { get; } = new();

    public Task<PermissionDisposition> ClassifyAsync(
        SessionId session, string tool, string proposedInput,
        IReadOnlyList<string> leadMessages, CancellationToken ct)
        => Task.FromResult(PermissionDisposition.Ask);
}
