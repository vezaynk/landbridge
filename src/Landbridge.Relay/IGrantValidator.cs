namespace Landbridge.Relay;

/// <summary>
/// Validates the opaque connection-establishment grant a tunnel presents
/// (spec §8.3). Checked once, at tunnel-open, BEFORE the tunnel is paired or
/// spliced; an invalid grant is refused and no splice is formed.
///
/// <para>Both ends authenticate to the relay independently by presenting a
/// grant — neither end authenticates to the other. A grant is a connection
/// credential only: once a splice is established it persists until a side goes
/// away, and is never re-validated mid-flight (no renewal path).</para>
///
/// <para>The real implementation is <see cref="ControlPlaneGrantValidator"/> — an
/// HTTP call to the control plane, which checks the grant against
/// <c>{consumer, service, expiry}</c> — and it is what runs whenever
/// <c>Relay:ControlPlane:Url</c> is configured. With no plane URL the fail-closed
/// <see cref="StaticSecretGrantValidator"/> stands in for local dev. The seam stays
/// injectable so a test can substitute its own.</para>
/// </summary>
public interface IGrantValidator
{
    /// <summary>
    /// Returns true if <paramref name="grant"/> authorizes a tunnel for the
    /// given <paramref name="forwardId"/> and <paramref name="role"/>.
    /// </summary>
    Task<bool> ValidateAsync(string grant, string forwardId, RelayRole role, CancellationToken ct);
}
