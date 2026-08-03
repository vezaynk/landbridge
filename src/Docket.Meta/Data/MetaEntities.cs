namespace Docket.Meta.Data;

/// <summary>
/// The lifecycle state of an Instance (spec §3, docket-meta). The saga drives
/// <see cref="Provisioning"/> → <see cref="Ready"/>; an operator suspends/resumes
/// and destroys. <see cref="Failed"/> is a saga stall recorded with the step that
/// stalled (<see cref="InstanceRow.FailedStep"/>) — recoverable by re-running the
/// saga, which resumes from that step. Destroyed ≠ suspended: a destroyed record
/// is a tombstone, a suspended one keeps its volume + secrets for resume.
/// </summary>
public enum InstanceState
{
    Provisioning,
    Ready,
    Suspended,
    Destroyed,
    Failed,
}

/// <summary>How meta reaches a host's Docker Engine API (spec §3 host pool).</summary>
public enum HostEndpointKind
{
    /// <summary>Local unix socket — the pool-of-one (<c>unix:///var/run/docker.sock</c>).</summary>
    UnixSocket,

    /// <summary>Remote Docker Engine over TCP with mutual TLS (host record carries the certs).</summary>
    TcpMtls,
}

/// <summary>
/// The ordered, idempotent, resumable steps of the provisioning saga (design note
/// §2). Each is a checkpoint recorded in <see cref="InstanceStepRow"/>; a meta
/// restart mid-provision resumes from the first not-<see cref="StepStatus.Done"/>
/// step. Secret generation is NOT a step — it happens once, before any side
/// effect, when the <see cref="InstanceRow"/> is first inserted (the create
/// endpoint), so recovery never regenerates credentials and orphans a container.
/// </summary>
public enum ProvisionStep
{
    PullImages,
    CreateNetwork,
    CreateVolume,
    StartPostgres,
    StartMcp,
    StartRelay,
    AddRoutes,
    VerifyReady,
}

/// <summary>Progress of a single saga step (design note §2 — doubles as the detail-page event log).</summary>
public enum StepStatus
{
    Pending,
    Done,
    Failed,
}

/// <summary>
/// A Docker host in the pool (spec §3). One row = one Docker Engine API endpoint.
/// The mTLS material is stored for remote (<see cref="HostEndpointKind.TcpMtls"/>)
/// hosts; note (design note deviation #6 / task #79) these are plaintext secrets
/// at rest in meta's DB until the encryption follow-up lands.
/// </summary>
public sealed class HostRow
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    /// <summary>The Docker Engine API URI — <c>unix:///var/run/docker.sock</c> or <c>https://host:2376</c>.</summary>
    public required string EndpointUri { get; set; }
    public HostEndpointKind EndpointKind { get; set; }

    /// <summary>mTLS PEMs for a remote host (null for the local socket).</summary>
    public string? TlsCaPem { get; set; }
    public string? TlsClientCertPem { get; set; }
    public string? TlsClientKeyPem { get; set; }

    /// <summary>
    /// The address the edge Caddy reverse-proxies published ports on for this host
    /// (design note §4). Defaults to <c>127.0.0.1</c> for the local pool-of-one.
    /// </summary>
    public string PublishedHost { get; set; } = "127.0.0.1";

    /// <summary>Host-port allocation window for published container ports (design note §3).</summary>
    public int PortRangeLow { get; set; } = 42000;
    public int PortRangeHigh { get; set; } = 42999;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }

    public List<InstanceRow> Instances { get; set; } = new();
}

/// <summary>
/// An Instance record: the desired state, its placement, its Docker object ids, its
/// published ports, and its per-instance secrets. Secrets other than the operator
/// passphrase are retained (design note §5) because meta must re-inject them to
/// recreate containers idempotently on resume/upgrade; only the passphrase is
/// shown-once and never persisted in plaintext — only its SHA-256 hash lands here.
/// </summary>
public sealed class InstanceRow
{
    public Guid Id { get; set; }

    /// <summary>DNS-safe instance name; the subdomain label under <c>docket.&lt;domain&gt;</c>.</summary>
    public required string Name { get; set; }

    /// <summary>A human label grouping instances (spec §3 Accounts — name only, no billing).</summary>
    public string? AccountLabel { get; set; }

    public Guid HostId { get; set; }
    public HostRow? Host { get; set; }

    /// <summary>The pinned docket image tag this instance runs (spec §3 image rollout).</summary>
    public required string ImageTag { get; set; }

    public InstanceState State { get; set; } = InstanceState.Provisioning;

    /// <summary>The step that stalled when <see cref="State"/> is <see cref="InstanceState.Failed"/>.</summary>
    public ProvisionStep? FailedStep { get; set; }

    // ── Docker object identity (recorded per step for cheap adoption + precise teardown) ──
    public string? NetworkName { get; set; }
    public string? VolumeName { get; set; }
    public string? PgContainerId { get; set; }
    public string? McpContainerId { get; set; }
    public string? RelayContainerId { get; set; }
    public int? McpPublishedPort { get; set; }
    public int? RelayPublishedPort { get; set; }

    /// <summary>The instance's public MCP URL (also its OAuth issuer/resource id, spec §5).</summary>
    public string? PublicUrl { get; set; }

    // ── Per-instance secrets (design note §5) ──
    /// <summary>SHA-256 hex of the operator passphrase. The plaintext is NEVER stored (shown once at create).</summary>
    public required string PassphraseHash { get; set; }
    /// <summary>Postgres password — retained to rebuild the connection string on recreate.</summary>
    public required string DbPassword { get; set; }
    /// <summary>Shared bearer between the mcp and relay containers (grant validation, §8.3).</summary>
    public required string RelayBearer { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public DateTimeOffset? DestroyedAt { get; set; }

    public List<InstanceStepRow> Steps { get; set; } = new();
}

/// <summary>
/// One saga step's persisted progress for one instance. The reconciler reads these
/// to resume; the detail page renders them as the event log (design note §1/§2).
/// </summary>
public sealed class InstanceStepRow
{
    public Guid Id { get; set; }
    public Guid InstanceId { get; set; }
    public InstanceRow? Instance { get; set; }

    public ProvisionStep Step { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;

    /// <summary>An external reference the step produced (network/volume name, container id) — aids adoption + audit.</summary>
    public string? ExternalRef { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
