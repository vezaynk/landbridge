using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// Lead inbox snapshot: outstanding facts on this Team (or selected sessions).
/// Team-wide reads are identifiers only. A per-session read carries bodies and
/// marks unread report mail as delivered.
/// </summary>
public sealed record LeadInboxView(IReadOnlyList<LeadInboxItem> Items);

/// <param name="MessageId">
/// The live envelope id when one is open. Null on a failed session that has
/// no leftover message, and on unread report mail (not an envelope).
/// </param>
public sealed record LeadInboxItem(
    Guid SessionId,
    LeadInboxKind Kind,
    Guid? MessageId,
    string Namespace,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResultReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Report = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Question = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Answer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? InputKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PermissionTool = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<PermissionOption>? PermissionOptions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EscalationReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? InfrastructureRequeues = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? InfrastructureRequeueLimit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    LivenessLossReason? LastRequeueReason = null);

/// <summary>
/// One outstanding fact. A session may contribute more than one (mechanical
/// <see cref="Failed"/> plus a leftover envelope, or unread report mail plus
/// a later question).
/// </summary>
[JsonConverter(typeof(LeadInboxKindJsonConverter))]
public enum LeadInboxKind
{
    Failed,
    Permission,
    Question,
    SpawnRequest,
    AuthHelp,
    EndpointWait,
    Unreachable,
    Report,
    Pull,
}

public static class LeadInboxKindMapping
{
    /// <summary>
    /// Every outstanding fact on the row. Failed does not hide a leftover
    /// envelope; unread report mail does not hide a later wait;
    /// <see cref="MessageState.AwaitingPull"/> is included.
    /// </summary>
    public static IEnumerable<LeadInboxItem> ItemsFor(
        Guid sessionId,
        string ns,
        SessionHealth health,
        MessageState message,
        InputRequestKind? inputKind,
        Guid? messageId,
        bool reportUnread)
    {
        if (health == SessionHealth.Failed)
            yield return new LeadInboxItem(sessionId, LeadInboxKind.Failed, messageId, ns);

        if (reportUnread)
            yield return new LeadInboxItem(sessionId, LeadInboxKind.Report, null, ns);

        var live = message switch
        {
            MessageState.AwaitingPermission => LeadInboxKind.Permission,
            MessageState.AwaitingReport => LeadInboxKind.Report,
            MessageState.AwaitingPull => LeadInboxKind.Pull,
            MessageState.AwaitingLead => inputKind switch
            {
                InputRequestKind.SpawnRequest => LeadInboxKind.SpawnRequest,
                InputRequestKind.AuthHelp => LeadInboxKind.AuthHelp,
                InputRequestKind.EndpointWait => LeadInboxKind.EndpointWait,
                InputRequestKind.Unreachable => LeadInboxKind.Unreachable,
                InputRequestKind.Permission => LeadInboxKind.Permission,
                _ => LeadInboxKind.Question,
            },
            _ => (LeadInboxKind?)null,
        };
        if (live is { } kind && !(kind == LeadInboxKind.Report && reportUnread))
            yield return new LeadInboxItem(sessionId, kind, messageId, ns);
    }

    /// <summary>
    /// Triage order: mechanical failure, live permission, report, Lead-owed
    /// asks, then a worker-owed pull.
    /// </summary>
    public static int Rank(LeadInboxKind kind) => kind switch
    {
        LeadInboxKind.Failed => 0,
        LeadInboxKind.Permission => 1,
        LeadInboxKind.Report => 2,
        LeadInboxKind.Pull => 4,
        _ => 3,
    };
}

public sealed class LeadInboxKindJsonConverter()
    : JsonStringEnumConverter<LeadInboxKind>(JsonNamingPolicy.CamelCase);
