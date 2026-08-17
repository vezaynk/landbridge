namespace Docket.Relay;

/// <summary>Relay host options (config section <c>Relay</c>).</summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>
    /// How long the first tunnel of a forward id waits for its pair to arrive
    /// before closing cleanly (spec §8.3). Bounded so an unpaired side never
    /// hangs and its forward-id entry can never leak.
    /// </summary>
    public TimeSpan PairWaitTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
