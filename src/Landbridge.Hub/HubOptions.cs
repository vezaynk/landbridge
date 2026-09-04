namespace Landbridge.Hub;

/// <summary>Hub-only knobs. Retain is the queue TTL; ping is the SSE keepalive.</summary>
public sealed class HubOptions
{
    public const string SectionName = "Hub";

    public TimeSpan Retain { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(15);
}
