using Landbridge.Core;

namespace Landbridge.Mcp;

/// <summary>
/// JSON twins Hub serves. Duplicated here so MCP hosts deserialize without
/// referencing the Hub assembly.
/// </summary>
public sealed record HubSessionListItem(
    Guid Id,
    string Slug,
    Guid TeamId,
    string? TeamSlug,
    string? Profile,
    SessionState State,
    Occupancy OccupancyDesired,
    Occupancy OccupancyObserved,
    SessionHealth Health,
    bool Hidden,
    MessageState MessageState,
    PendingSpawn? PendingSpawn,
    bool ReportUnread,
    Guid? MessageId,
    InputRequestKind? InputKind,
    DateTimeOffset? BlockedAt,
    Guid? ParkMachine,
    Guid? CurrentInstanceId,
    DateTimeOffset? MessageOpenedAt,
    DateTimeOffset? LastMessageClosedAt,
    string Namespace,
    int Attempt,
    bool HasReport,
    bool HasQuestion,
    Guid? ContinuesSessionId,
    VerdictProvenance? CompletionProvenance,
    int InfrastructureRequeues,
    LivenessLossReason? LastRequeueReason);

public sealed record HubSessionDocument(
    Guid Id,
    string Slug,
    Guid? CurrentInstanceId,
    IReadOnlyList<HubInstanceDocument> Instances);

public sealed record HubInstanceDocument(
    Guid Id,
    Guid SessionId,
    bool Revoked,
    DateTimeOffset CreatedAt,
    Guid? MachineId);

public sealed record HubProcessDocument(
    Guid Id,
    Guid MachineId,
    string Name,
    string State,
    Guid DeclaredBySession,
    DateTimeOffset? StartedAt,
    int? ExitCode,
    DateTimeOffset? ExitedAt,
    bool StdinOpen);
