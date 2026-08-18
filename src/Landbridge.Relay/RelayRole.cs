namespace Landbridge.Relay;

/// <summary>
/// Which end of a forward a tunnel represents (spec §8.3). The relay pairs
/// exactly one <see cref="Consumer"/> with one <see cref="Producer"/> per
/// forward id and splices their byte streams together.
/// </summary>
public enum RelayRole
{
    Consumer,
    Producer,
}
