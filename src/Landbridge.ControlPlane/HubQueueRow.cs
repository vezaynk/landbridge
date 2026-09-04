namespace Landbridge.ControlPlane;

/// <summary>
/// Durable wake log for the hub (<c>ideas/hub-and-write-queue.md</c>). The hub
/// LISTENs on <see cref="LandbridgeDbContext.EventChannel"/> and inserts a row
/// naming the topic + entity to refetch. SSE clients catch up with
/// <c>id > after</c> and follow; a sweeper deletes rows older than the
/// retain window. Not the session source of truth — that stays <see cref="SessionRow"/>.

/// </summary>
public sealed class HubQueueRow
{
    public const string SessionTopic = "session";
    public const string SessionsTopic = "sessions";

    public long Id { get; set; }
    public string Topic { get; set; } = "";
    public Guid? EntityId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
