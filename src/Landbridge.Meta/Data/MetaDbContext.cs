using Landbridge.Meta.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Landbridge.Meta.Data;

/// <summary>
/// landbridge-meta's own store (spec §3: "Meta keeps its own Postgres"). Entirely
/// separate from the plane's database — different credential class, different
/// network. Snake-case naming + Npgsql, mirroring the plane's conventions so the
/// hand-authored migration reads the same way, but sharing no schema.
///
/// <para>The <see cref="MetaSecretProtector"/> is a required dependency, not an
/// optional one: the retained per-instance secrets are encrypted by value converter
/// (task #79), and a context that could be built without a protector would be a
/// context that silently writes them in plaintext.</para>
/// </summary>
public sealed class MetaDbContext(DbContextOptions<MetaDbContext> options, MetaSecretProtector protector)
    : DbContext(options)
{
    public DbSet<HostRow> Hosts => Set<HostRow>();
    public DbSet<InstanceRow> Instances => Set<InstanceRow>();
    public DbSet<InstanceStepRow> InstanceSteps => Set<InstanceStepRow>();

    /// <summary>The protector sealing this context's secret columns (exposed for the rewrap sweep).</summary>
    public MetaSecretProtector Protector => protector;

    /// <summary>Part of the model cache key — see <see cref="MetaModelCacheKeyFactory"/>.</summary>
    public string KeySetId => protector.KeySetId;

    /// <summary>The runtime + design-time options: Npgsql + snake_case, matching the plane (Program.cs).</summary>
    public static DbContextOptions<MetaDbContext> BuildOptions(string connectionString)
    {
        var b = new DbContextOptionsBuilder<MetaDbContext>().UseNpgsql(connectionString);
        Configure(b);
        return b.Options;
    }

    /// <summary>
    /// The options every construction path must share: snake_case naming and the
    /// key-aware model cache key. Applied by <see cref="BuildOptions"/>, by the host's
    /// <c>AddDbContext</c>, and by the tests' in-memory builders.
    /// </summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder b) =>
        b.UseSnakeCaseNamingConvention()
         .ReplaceService<IModelCacheKeyFactory, MetaModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<HostRow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.EndpointUri).IsRequired();
            e.Property(x => x.EndpointKind).HasConversion<string>();
            e.HasIndex(x => x.Name).IsUnique();
            // The mTLS client KEY is a secret — it plus the cert is root-equivalent
            // control of a Docker host, the highest-value credential in this store.
            // The CA and client certificate are public material and stay readable so
            // an operator can audit which host a row points at without the key.
            e.Property(x => x.TlsClientKeyPem)
                .HasConversion(new EncryptedNullableStringConverter(protector, "host.tls_client_key_pem"));
        });

        b.Entity<InstanceRow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            // One live instance per name; a destroyed tombstone keeps its name, so
            // the uniqueness is scoped to non-destroyed rows (partial index).
            e.HasIndex(x => x.Name)
                .IsUnique()
                .HasFilter("destroyed_at IS NULL");
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.FailedStep).HasConversion<string?>();
            e.Property(x => x.ImageTag).IsRequired();
            // The passphrase HASH is not encrypted: it is already a one-way digest of
            // a value meta never retains, so sealing it would protect nothing.
            e.Property(x => x.PassphraseHash).IsRequired();
            // These two ARE live secrets — meta retains them only because it must
            // re-inject them to recreate containers on resume/upgrade (§3).
            e.Property(x => x.DbPassword).IsRequired()
                .HasConversion(new EncryptedStringConverter(protector, "instance.db_password"));
            e.Property(x => x.RelayBearer).IsRequired()
                .HasConversion(new EncryptedStringConverter(protector, "instance.relay_bearer"));
            e.HasOne(x => x.Host).WithMany(h => h.Instances)
                .HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<InstanceStepRow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Step).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            // One row per (instance, step): the saga upserts progress, never
            // appends duplicates — the natural key of the checkpoint.
            e.HasIndex(x => new { x.InstanceId, x.Step }).IsUnique();
            e.HasOne(x => x.Instance).WithMany(i => i.Steps)
                .HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
