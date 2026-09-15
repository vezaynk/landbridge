using Landbridge.Core;

namespace Landbridge.Hub;

/// <summary>
/// JSON twins the hub serves on GET. Wakes are SSE; the body is this.
/// No credential hashes, grant hashes, or label hashes (§13).
/// </summary>
public sealed record SessionListItem(
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
    string? ParkMachine,
    Guid? CurrentInstanceId,
    DateTimeOffset? MessageOpenedAt,
    DateTimeOffset? LastMessageClosedAt);

public sealed record SessionDocument(
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
    MessageVerdict? MessageVerdict,
    bool ReportUnread,
    Guid? MessageId,
    Guid? LastMessageId,
    MessageTerminal? LastMessageTerminal,
    DateTimeOffset? MessageOpenedAt,
    DateTimeOffset? LastMessageClosedAt,
    PendingSpawn? PendingSpawn,
    string Description,
    string? ResultReference,
    string? WorkerReport,
    InputRequestKind? InputKind,
    string? InputQuestion,
    string? InputAnswer,
    string? PermissionTool,
    string? PermissionOptions,
    string? PermissionOptionId,
    PermissionVerdict? PermissionVerdict,
    DateTimeOffset? PermissionEscalatedAt,
    string? PermissionEscalationReason,
    int Attempt,
    int InfrastructureRequeues,
    int InfrastructureRequeueLimit,
    LivenessLossReason? LastRequeueReason,
    VerdictProvenance? CompletionProvenance,
    Guid? ContinuesSessionId,
    Guid? CurrentInstanceId,
    string? PreferredMachine,
    string? ParkMachine,
    DateTimeOffset? BlockedAt,
    IReadOnlyList<InstanceDocument> Instances,
    IReadOnlyList<UsageDocument> Usage);

public sealed record ExchangeDocument(
    Guid Id,
    string Slug,
    MessageState MessageState,
    bool ReportUnread,
    InputRequestKind? InputKind,
    string? InputQuestion,
    string? InputAnswer,
    string? ResultReference,
    string? WorkerReport,
    string? PermissionTool,
    string? PermissionOptions,
    string? PermissionOptionId,
    PermissionVerdict? PermissionVerdict);

public sealed record EventDocument(
    long Seq,
    DateTimeOffset OccurredAt,
    string Kind,
    SessionState? FromState,
    SessionState? ToState,
    string? Detail,
    Guid SessionId,
    Guid TeamId,
    InputRequestKind? InputKind,
    LivenessLossReason? LivenessReason,
    PermissionVerdict? PermissionVerdict,
    PermissionAnswerer? PermissionAnswerer,
    string? AuthOperation,
    string? AuthTarget,
    string? AuthErrorCode,
    string? AuthMissingScope,
    string? SubagentId,
    string? SubagentParentId);

public sealed record ServiceDocument(
    long Seq,
    Guid SessionId,
    Guid TeamId,
    string Name,
    int Port,
    DateTimeOffset CreatedAt);

public sealed record ForwardDocument(
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
    string? ConsumerMachine,
    int? ConsumerPort);

public sealed record PreviewDocument(
    Guid Id,
    string Label,
    Guid TeamId,
    Guid SessionId,
    string ServiceName,
    PreviewAuthPolicy AuthPolicy,
    TimeSpan Ttl,
    DateTimeOffset ExpiresAt);

public sealed record MachineDocument(
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
    IReadOnlyList<ProcessDocument> Processes);

public sealed record ProcessDocument(
    Guid Id,
    Guid MachineId,
    string Name,
    string State,
    Guid DeclaredBySession,
    DateTimeOffset? StartedAt,
    int? ExitCode,
    DateTimeOffset? ExitedAt,
    bool StdinOpen);

public sealed record TeamDocument(
    Guid TeamId,
    string Slug,
    Guid LeadCredentialId,
    DateTimeOffset CreatedAt,
    long ForwardedBytes,
    DateTimeOffset? BytesReportedAt);

public sealed record FrictionDocument(
    long Seq,
    DateTimeOffset At,
    string Role,
    Guid TeamId,
    Guid? SessionId,
    Guid? HumanId,
    string Message);

public sealed record LeadEventDocument(
    long Seq,
    Guid TeamId,
    string Kind,
    Guid? HumanId,
    Guid? PriorHumanId,
    DateTimeOffset OccurredAt);

public sealed record InstanceDocument(
    Guid Id,
    Guid SessionId,
    bool Revoked,
    DateTimeOffset CreatedAt,
    string? MachineId);

public sealed record UsageDocument(
    Guid SessionId,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long? ReasoningOutputTokens,
    decimal? CostUsd,
    DateTimeOffset ReportedAt);
