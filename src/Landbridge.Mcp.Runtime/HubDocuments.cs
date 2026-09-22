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
    Guid TeamId,
    string? TeamSlug,
    string Namespace,
    string? Profile,
    SessionState State,
    Occupancy OccupancyDesired,
    Occupancy OccupancyObserved,
    SessionHealth Health,
    bool Hidden,
    MessageState MessageState,
    bool ReportUnread,
    InputRequestKind? InputKind,
    string? InputQuestion,
    string? InputAnswer,
    string? PermissionTool,
    string? WorkerReport,
    LivenessLossReason? LastRequeueReason,
    int Attempt,
    Guid? CurrentInstanceId,
    Guid? PreferredMachine,
    Guid? ParkMachine,
    DateTimeOffset? BlockedAt,
    DateTimeOffset? MessageOpenedAt,
    IReadOnlyList<HubInstanceDocument> Instances,
    IReadOnlyList<HubUsageDocument> Usage);

public sealed record HubUsageDocument(
    Guid SessionId,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long? ReasoningOutputTokens,
    decimal? CostUsd,
    DateTimeOffset ReportedAt);

public sealed record HubInstanceDocument(
    Guid Id,
    Guid SessionId,
    bool Revoked,
    DateTimeOffset CreatedAt,
    Guid? MachineId);

public sealed record HubMachine(
    Guid Id,
    string Slug,
    string Name,
    string Os,
    DateTimeOffset EnrolledAt,
    bool Revoked,
    DateTimeOffset? LastSpokeAt,
    bool Ready,
    bool UnderBackPressure,
    bool Live,
    string[] Profiles,
    Guid? BoundHumanId,
    DateTimeOffset? BoundAt,
    IReadOnlyList<HubProcessDocument> Processes);

public sealed record HubServiceDocument(
    long Seq,
    Guid SessionId,
    Guid TeamId,
    string Name,
    int Port,
    DateTimeOffset CreatedAt);

public sealed record HubPreviewDocument(
    Guid Id,
    string Label,
    Guid TeamId,
    Guid SessionId,
    string ServiceName,
    PreviewAuthPolicy AuthPolicy,
    TimeSpan Ttl,
    DateTimeOffset ExpiresAt);

public sealed record HubForwardDocument(
    Guid ForwardId,
    Guid TeamId,
    Guid ProducerSessionId,
    Guid? ConsumerSessionId,
    string ServiceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedByConsumerAt,
    DateTimeOffset? UsedByProducerAt,
    bool Revoked,
    Guid? ConsumerMachine,
    int? ConsumerPort);

public sealed record HubTeamDocument(
    Guid TeamId,
    string Slug,
    Guid LeadCredentialId,
    DateTimeOffset CreatedAt,
    long ForwardedBytes,
    DateTimeOffset? BytesReportedAt);

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
