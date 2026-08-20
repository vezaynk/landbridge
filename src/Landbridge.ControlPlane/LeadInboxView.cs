using System.Text.Json;
using System.Text.Json.Serialization;
using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// Structure-only Lead inbox: every outstanding fact on this Team (or one
/// session). Identifiers and kind, never prose — the Lead fetches question
/// and report text with the existing per-session tools.
/// </summary>
public sealed record LeadInboxView(IReadOnlyList<LeadInboxItem> Items);

/// <param name="MessageId">
/// The live envelope id when one is open. Null on a failed session that has
/// no leftover message. Not the session id.
/// </param>
public sealed record LeadInboxItem(
    Guid SessionId,
    LeadInboxKind Kind,
    Guid? MessageId,
    string Namespace);

/// <summary>
/// One outstanding fact. A session may contribute more than one (mechanical
/// <see cref="Failed"/> plus a leftover envelope).
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
    /// envelope; <see cref="MessageState.AwaitingPull"/> is included.
    /// </summary>
    public static IEnumerable<LeadInboxItem> ItemsFor(
        Guid sessionId,
        string ns,
        SessionHealth health,
        MessageState message,
        InputRequestKind? inputKind,
        Guid? messageId)
    {
        if (health == SessionHealth.Failed)
            yield return new LeadInboxItem(sessionId, LeadInboxKind.Failed, messageId, ns);

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
        if (live is { } kind)
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
