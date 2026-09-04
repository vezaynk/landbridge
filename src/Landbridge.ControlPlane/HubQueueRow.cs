namespace Landbridge.ControlPlane;

/// <summary>
/// Transactional outbox for the hub. Inserted in
/// <see cref="SessionStore"/>'s commit, same transaction as the session write
/// and <c>pg_notify</c>. The notify is a doorbell; this table is what the hub
/// tails with <c>id > after</c> so a hub restart does not drop wakes.
/// Sweeper deletes rows older than the retain window. Not the session source
/// of truth — that stays <see cref="SessionRow"/>.
/// </summary>
public sealed class HubQueueRow
{
    public const string SessionTopic = "session";
    public const string SessionsTopic = "sessions";
    public const string EventsTopic = "events";
    public const string ExchangeTopic = "exchange";
    public const string ServicesTopic = "services";
    public const string ForwardsTopic = "forwards";
    public const string PreviewsTopic = "previews";
    public const string MachinesTopic = "machines";
    public const string ProcessesTopic = "processes";

    public long Id { get; set; }
    public string Topic { get; set; } = "";
    public Guid? EntityId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
