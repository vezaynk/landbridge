using Landbridge.Meta.Data;
using Landbridge.Meta.Substrate;

namespace Landbridge.Meta.Provisioning;

/// <summary>
/// Turns an <see cref="InstanceRow"/> + its host + config into the three container
/// recipes of the per-host Instance (design note §3): a private Postgres, the
/// landbridge-mcp plane, and the landbridge-relay. This is the single place credentials are
/// wired into container env — the security-sensitive surface (design note §5) — so
/// the saga tests assert against exactly this output.
///
/// <para>Invariants enforced here, not by convention: the mcp gets
/// <c>Landbridge:MigrateOnStartup=true</c> (deviation #4, so a fresh instance self-migrates
/// and upgrades re-migrate), the passphrase HASH (never a plaintext), the shared
/// relay bearer on both mcp and relay, and — critically — the dev-only gates
/// (<c>Landbridge:DevSeed:TokenDir</c>, <c>Landbridge:Oauth:AllowInsecureClientMetadata</c>)
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
        ["landbridge.managed"] = "meta",
        ["landbridge.instance"] = i.Id.ToString(),
        ["landbridge.role"] = role,
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
            ["POSTGRES_USER"] = "landbridge",
            ["POSTGRES_PASSWORD"] = i.DbPassword,
            ["POSTGRES_DB"] = "landbridge",
            // Keep the data in a subdir of the mount so the volume root can hold the
            // lost+found some drivers create without upsetting initdb.
            ["PGDATA"] = PgDataPath + "/pgdata",
        },
        Mounts = new[] { new MountSpec(InstanceNaming.VolumeName(i.Name), PgDataPath) },
        HealthCmd = new[] { "CMD-SHELL", "pg_isready -U landbridge -d landbridge" },
    };

    /// <summary>The landbridge-mcp plane — the security-sensitive env; published to a host port for the edge route.</summary>
    public ContainerSpec Mcp(InstanceRow i) => new()
    {
        Name = InstanceNaming.McpContainer(i.Name),
        Image = McpImage(i),
        NetworkName = InstanceNaming.NetworkName(i.Name),
        Labels = Labels(i, "mcp"),
        Env = new Dictionary<string, string>
        {
            // The plane reads double-underscore env as config keys.
            ["ConnectionStrings__Landbridge"] =
                $"Host={InstanceNaming.PgContainer(i.Name)};Port=5432;Database=landbridge;Username=landbridge;Password={i.DbPassword}",
            ["Landbridge__PublicMcpUrl"] = InstanceNaming.McpPublicUrl(i.Name, options.Domain),
            ["Landbridge__Operator__PassphraseHash"] = i.PassphraseHash,
            ["Landbridge__RelayUrl"] = InstanceNaming.RelayPublicUrl(i.Name, options.Domain),
            ["Landbridge__RelayValidation__Bearer"] = i.RelayBearer,
            // Deviation #4: meta owns migration for instances it provisions.
            ["Landbridge__MigrateOnStartup"] = "true",
            ["ASPNETCORE_URLS"] = $"http://+:{InstanceNaming.ContainerHttpPort}",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
        },
        PublishContainerPort = InstanceNaming.ContainerHttpPort,
        PublishHostPort = i.McpPublishedPort ?? 0,
    };

    /// <summary>The landbridge-relay — validates grants against the mcp over the private network; published for landbridged to dial.</summary>
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
