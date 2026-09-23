namespace Landbridge.ControlPlane;

/// <summary>
/// Accepted session mutation waiting for Core to <c>Apply</c>. The façade
/// authenticates and inserts <see cref="Queued"/>; Core drains with
/// SKIP LOCKED. Idempotent on (actor, idempotency key).
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
    public string IdempotencyKey { get; set; } = "";
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
