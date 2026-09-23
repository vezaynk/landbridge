namespace Landbridge.ControlPlane;

/// <summary>
/// Accepted session mutation waiting for Core to <c>Apply</c>. The façade
/// authenticates and inserts <see cref="Queued"/>; Core drains with SKIP LOCKED.
///
/// <para>Deduplicated on (actor, <see cref="IdempotencyKey"/>) when — and only when —
/// a caller supplied one. See that member for why it is not derived.</para>
/// </summary>
public sealed class CommandRow
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Applied = "applied";
    public const string Rejected = "rejected";

    public const string CreateSession = "create_session";
    public const string StopSession = "stop_session";
    public const string ParkSession = "park_session";
    public const string InputResponse = "input_response";
    public const string InputRequest = "input_request";
    public const string Permission = "permission";
    public const string Report = "report";
    public const string Ask = "ask";
    public const string RegisterService = "register_service";
    public const string PullReceipt = "pull_receipt";

    public const string LeadActor = "lead";
    public const string WorkerActor = "worker";
    public const string HumanActor = "human";

    public Guid Id { get; set; }
    /// <summary>
    /// The caller's name for this attempt, or null. A retry presents the same key and
    /// attaches to the command already accepted; two separate requests that happen to
    /// carry identical payloads are two commands.
    ///
    /// <para>Null means no deduplication, which is the right default for the in-process
    /// façade path: that insert either succeeds or throws, so there is no retry to
    /// collapse, and a Lead answering "yes" to one session today and "yes" again
    /// tomorrow is two answers.</para>
    ///
    /// <para><b>Not derived from the payload.</b> Hashing the content makes the key a
    /// function of what was said rather than of who said it when, so a legitimate repeat
    /// is indistinguishable from a retry and is swallowed — permanently, since there is
    /// no window. That is a silent loss: the caller is handed the earlier command's
    /// outcome and nothing reports that its request was discarded.</para>
    /// </summary>
    public string? IdempotencyKey { get; set; }
    public string ActorKind { get; set; } = "";
    public Guid ActorId { get; set; }
    public Guid SessionId { get; set; }
    public Guid TeamId { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string Status { get; set; } = Queued;
    public string? Rule { get; set; }
    public string? Reason { get; set; }
    public string? Slug { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}
