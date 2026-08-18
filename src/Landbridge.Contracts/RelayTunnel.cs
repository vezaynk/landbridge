namespace Landbridge.Contracts;

/// <summary>
/// The relay tunnel's wire vocabulary (spec §8.3): the request-header names both
/// tunnel ends present when they upgrade, and the two role strings. Lives in
/// <c>Landbridge.Contracts</c> so the relay (<c>Landbridge.Relay.TunnelEndpoint</c>) and
/// the runner's tunnel client (<c>Landbridge.Runner.RelayForwarder</c>) agree on one
/// set of constants rather than each redeclaring them — the same reason the §10
/// command/event vocabulary lives here. The runner never references
/// <c>Landbridge.Relay</c>; this shared surface is how the two stay in lockstep.
/// </summary>
public static class RelayTunnel
{
    /// <summary>The forward id that pairs the two ends of a tunnel.</summary>
    public const string ForwardIdHeader = "X-Landbridge-Forward-Id";

    /// <summary>The opaque connection-establishment grant (spec §8.3).</summary>
    public const string GrantHeader = "X-Landbridge-Grant";

    /// <summary><c>consumer</c> or <c>producer</c>.</summary>
    public const string RoleHeader = "X-Landbridge-Role";

    /// <summary>The consumer end: binds a loopback listener and dials the relay per accepted connection.</summary>
    public const string ConsumerRole = "consumer";

    /// <summary>The producer end: dials the registered local service and opens its outbound tunnel.</summary>
    public const string ProducerRole = "producer";
}
