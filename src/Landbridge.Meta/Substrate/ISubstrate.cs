namespace Landbridge.Meta.Substrate;

/// <summary>
/// The seam between the provisioning saga and a single Docker host (design note §3).
/// Every method is idempotent by name+label so the saga can re-run any step after a
/// crash and converge rather than duplicate. Implemented for real by
/// <see cref="DockerSubstrate"/> over Docker.DotNet, and by an in-memory fake in the
/// tests — which is what lets the saga, recovery, and credential-bootstrap logic be
/// unit-tested with zero Docker (design note §7).
/// </summary>
public interface ISubstrate
{
    /// <summary>Liveness probe for the host's Docker Engine (used to gate the Docker E2E and host-registry health).</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Ensures an image is present on the host (pull if absent). A no-op when already local.</summary>
    Task EnsureImageAsync(string image, CancellationToken ct);

    /// <summary>Creates the named network if absent (adopts it if present). Idempotent.</summary>
    Task EnsureNetworkAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct);

    /// <summary>Creates the named volume if absent (adopts it if present). Idempotent.</summary>
    Task EnsureVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct);

    /// <summary>
    /// Converges the named container on <paramref name="spec"/>: creates and starts it when
    /// absent, adopts and starts an existing one already running the spec's image, and
    /// <b>replaces</b> (removes, then recreates) one running a different image. Returns the
    /// container id. Idempotent — safe to call on every saga run.
    ///
    /// <para>That last case is what an image upgrade <em>is</em> here: the saga re-runs its
    /// ordinary start steps against a recipe whose tag changed, so "ensure" has to mean the
    /// container this spec describes and not merely a container by that name. Keeping the
    /// replace inside the substrate is what lets provision, resume, and upgrade be one
    /// checkpointed saga instead of three sequences.</para>
    /// </summary>
    Task<string> EnsureContainerAsync(ContainerSpec spec, CancellationToken ct);

    /// <summary>Inspects a container by name or id. <see cref="ContainerStatus.Exists"/> is false when absent.</summary>
    Task<ContainerStatus> InspectContainerAsync(string nameOrId, CancellationToken ct);

    /// <summary>Starts an existing (stopped) container. No-op if already running or absent.</summary>
    Task StartContainerAsync(string nameOrId, CancellationToken ct);

    /// <summary>Stops a running container. No-op if already stopped or absent (idempotent suspend/teardown).</summary>
    Task StopContainerAsync(string nameOrId, CancellationToken ct);

    /// <summary>Removes a container (force), leaving its volumes intact. No-op if absent.</summary>
    Task RemoveContainerAsync(string nameOrId, CancellationToken ct);

    /// <summary>Removes a network by name. No-op if absent.</summary>
    Task RemoveNetworkAsync(string name, CancellationToken ct);

    /// <summary>Removes a named volume (destroys its data). No-op if absent.</summary>
    Task RemoveVolumeAsync(string name, CancellationToken ct);
}

/// <summary>Health rollup from a container's inspect state.</summary>
public enum HealthState
{
    /// <summary>No HEALTHCHECK defined — liveness is judged by <see cref="ContainerStatus.Running"/> alone.</summary>
    None,
    Starting,
    Healthy,
    Unhealthy,
}

/// <summary>The subset of a container's inspect result the saga and health probe need.</summary>
public sealed record ContainerStatus(
    bool Exists,
    bool Running,
    HealthState Health,
    int? PublishedPort);

/// <summary>A volume mount request: a named volume at a container path.</summary>
public sealed record MountSpec(string VolumeName, string ContainerPath);

/// <summary>
/// A declarative container recipe the substrate realizes. Env is a map (rendered to
/// <c>KEY=VALUE</c>) so tests can assert on exactly what credentials meta injects —
/// the security-sensitive surface (design note §5/§7).
/// </summary>
public sealed record ContainerSpec
{
    public required string Name { get; init; }
    public required string Image { get; init; }
    public required string NetworkName { get; init; }
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<MountSpec> Mounts { get; init; } = Array.Empty<MountSpec>();

    /// <summary>The container port to publish to a host port, or null to publish nothing (e.g. Postgres stays private).</summary>
    public int? PublishContainerPort { get; init; }

    /// <summary>The requested host port for the published container port (0 = let Docker choose; the saga reads it back).</summary>
    public int PublishHostPort { get; init; }

    /// <summary>An optional container-level HEALTHCHECK command (used for Postgres' <c>pg_isready</c>).</summary>
    public IReadOnlyList<string>? HealthCmd { get; init; }
}

/// <summary>Builds an <see cref="ISubstrate"/> for a given host record (design note §3 per-host client).</summary>
public interface ISubstrateFactory
{
    ISubstrate For(Data.HostRow host);
}
