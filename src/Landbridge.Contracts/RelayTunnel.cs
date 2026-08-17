namespace Docket.Contracts;

/// <summary>
/// The relay tunnel's wire vocabulary (spec §8.3): the request-header names both
/// tunnel ends present when they upgrade, and the two role strings. Lives in
/// <c>Docket.Contracts</c> so the relay (<c>Docket.Relay.TunnelEndpoint</c>) and
/// the runner's tunnel client (<c>Docket.Runner.RelayForwarder</c>) agree on one
/// set of constants rather than each redeclaring them — the same reason the §10
/// command/event vocabulary lives here. The runner never references
/// <c>Docket.Relay</c>; this shared surface is how the two stay in lockstep.
/// </summary>
public static class RelayTunnel
{
    /// <summary>The forward id that pairs the two ends of a tunnel.</summary>
    public const string ForwardIdHeader = "X-Docket-Forward-Id";

    /// <summary>The opaque connection-establishment grant (spec §8.3).</summary>
    public const string GrantHeader = "X-Docket-Grant";

    /// <summary><c>consumer</c> or <c>producer</c>.</summary>
    public const string RoleHeader = "X-Docket-Role";

    /// <summary>The consumer end: binds a loopback listener and dials the relay per accepted connection.</summary>
    public const string ConsumerRole = "consumer";

    /// <summary>The producer end: dials the registered local service and opens its outbound tunnel.</summary>
    public const string ProducerRole = "producer";
}
