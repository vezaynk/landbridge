using Docket.Meta.Data;
using Docket.Meta.Substrate;

namespace Docket.Meta.Provisioning;

/// <summary>
/// Turns an <see cref="InstanceRow"/> + its host + config into the three container
/// recipes of the per-host Instance (design note §3): a private Postgres, the
/// docket-mcp plane, and the docket-relay. This is the single place credentials are
/// wired into container env — the security-sensitive surface (design note §5) — so
/// the saga tests assert against exactly this output.
///
/// <para>Invariants enforced here, not by convention: the mcp gets
/// <c>Docket:MigrateOnStartup=true</c> (deviation #4, so a fresh instance self-migrates
/// and upgrades re-migrate), the passphrase HASH (never a plaintext), the shared
/// relay bearer on both mcp and relay, and — critically — the dev-only gates
/// (<c>Docket:DevSeed:TokenFile</c>, <c>Docket:Oauth:AllowInsecureClientMetadata</c>)
/// are NEVER set. A production instance is passphrase-gated with real OAuth and no
/// seeded identities.</para>
/// </summary>
public sealed class InstanceRecipe(MetaOptions options)
{
    private const string PgDataPath = "/var/lib/postgresql/data";

    public string McpImage(InstanceRow i) => $"{options.McpImageRepo}:{i.ImageTag}";
    public string RelayImage(InstanceRow i) => $"{options.RelayImageRepo}:{i.ImageTag}";
    public string PostgresImage => options.PostgresImage;

    public static IReadOnlyDictionary<string, string> Labels(InstanceRow i, string role) => new Dictionary<string, string>
    {
        ["docket.managed"] = "meta",
        ["docket.instance"] = i.Id.ToString(),
        ["docket.role"] = role,
    };

    /// <summary>The private Postgres — no published port, healthchecked via <c>pg_isready</c>.</summary>
    public ContainerSpec Postgres(InstanceRow i) => new()
    {
        Name = InstanceNaming.PgContainer(i.Name),
        Image = PostgresImage,
        NetworkName = InstanceNaming.NetworkName(i.Name),
        Labels = Labels(i, "postgres"),
        Env = new Dictionary<string, string>
        {
            ["POSTGRES_USER"] = "docket",
            ["POSTGRES_PASSWORD"] = i.DbPassword,
            ["POSTGRES_DB"] = "docket",
            // Keep the data in a subdir of the mount so the volume root can hold the
            // lost+found some drivers create without upsetting initdb.
            ["PGDATA"] = PgDataPath + "/pgdata",
        },
        Mounts = new[] { new MountSpec(InstanceNaming.VolumeName(i.Name), PgDataPath) },
        HealthCmd = new[] { "CMD-SHELL", "pg_isready -U docket -d docket" },
    };

    /// <summary>The docket-mcp plane — the security-sensitive env; published to a host port for the edge route.</summary>
    public ContainerSpec Mcp(InstanceRow i) => new()
    {
        Name = InstanceNaming.McpContainer(i.Name),
        Image = McpImage(i),
        NetworkName = InstanceNaming.NetworkName(i.Name),
        Labels = Labels(i, "mcp"),
        Env = new Dictionary<string, string>
        {
            // The plane reads double-underscore env as config keys.
            ["ConnectionStrings__Docket"] =
                $"Host={InstanceNaming.PgContainer(i.Name)};Port=5432;Database=docket;Username=docket;Password={i.DbPassword}",
            ["Docket__PublicMcpUrl"] = InstanceNaming.McpPublicUrl(i.Name, options.Domain),
            ["Docket__Operator__PassphraseHash"] = i.PassphraseHash,
            ["Docket__RelayUrl"] = InstanceNaming.RelayPublicUrl(i.Name, options.Domain),
            ["Docket__RelayValidation__Bearer"] = i.RelayBearer,
            // Deviation #4: meta owns migration for instances it provisions.
            ["Docket__MigrateOnStartup"] = "true",
            ["ASPNETCORE_URLS"] = $"http://+:{InstanceNaming.ContainerHttpPort}",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
        },
        PublishContainerPort = InstanceNaming.ContainerHttpPort,
        PublishHostPort = i.McpPublishedPort ?? 0,
    };

    /// <summary>The docket-relay — validates grants against the mcp over the private network; published for docketd to dial.</summary>
    public ContainerSpec Relay(InstanceRow i) => new()
    {
        Name = InstanceNaming.RelayContainer(i.Name),
        Image = RelayImage(i),
        NetworkName = InstanceNaming.NetworkName(i.Name),
        Labels = Labels(i, "relay"),
        Env = new Dictionary<string, string>
        {
            ["Relay__ControlPlane__Url"] =
                $"http://{InstanceNaming.McpContainer(i.Name)}:{InstanceNaming.ContainerHttpPort}",
            ["Relay__ControlPlane__Bearer"] = i.RelayBearer,
            ["ASPNETCORE_URLS"] = $"http://+:{InstanceNaming.ContainerHttpPort}",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
        },
        PublishContainerPort = InstanceNaming.ContainerHttpPort,
        PublishHostPort = i.RelayPublishedPort ?? 0,
    };
}
