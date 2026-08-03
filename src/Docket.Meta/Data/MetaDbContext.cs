using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Data;

/// <summary>
/// docket-meta's own store (spec §3: "Meta keeps its own Postgres"). Entirely
/// separate from the plane's database — different credential class, different
/// network. Snake-case naming + Npgsql, mirroring the plane's conventions so the
/// hand-authored migration reads the same way, but sharing no schema.
/// </summary>
public sealed class MetaDbContext(DbContextOptions<MetaDbContext> options) : DbContext(options)
{
    public DbSet<HostRow> Hosts => Set<HostRow>();
    public DbSet<InstanceRow> Instances => Set<InstanceRow>();
    public DbSet<InstanceStepRow> InstanceSteps => Set<InstanceStepRow>();

    /// <summary>The runtime + design-time options: Npgsql + snake_case, matching the plane (Program.cs).</summary>
    public static DbContextOptions<MetaDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<MetaDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<HostRow>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.EndpointUri).IsRequired();
            e.Property(x => x.EndpointKind).HasConversion<string>();
            e.HasIndex(x => x.Name).IsUnique();
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
            e.Property(x => x.PassphraseHash).IsRequired();
            e.Property(x => x.DbPassword).IsRequired();
            e.Property(x => x.RelayBearer).IsRequired();
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
