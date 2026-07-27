using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane;

public sealed class DocketDbContext(DbContextOptions<DocketDbContext> options) : DbContext(options)
{
    public DbSet<TaskRow> Tasks => Set<TaskRow>();
    public DbSet<WorkerInstanceRow> WorkerInstances => Set<WorkerInstanceRow>();
    public DbSet<RegisteredServiceRow> RegisteredServices => Set<RegisteredServiceRow>();
    public DbSet<TaskEventRow> TaskEvents => Set<TaskEventRow>();

    /// <summary>The channel dispatch/transition NOTIFYs land on (§3.1 LISTEN/NOTIFY).</summary>
    public const string EventChannel = "docket_task_events";

    /// <summary>
    /// The one place the store's Postgres options are configured — Npgsql plus
    /// snake_case naming, so hand-written SQL (the dispatch transaction, the
    /// partial index filter) reads unquoted lowercase identifiers. Every caller
    /// (host, tests, design-time factory) goes through here.
    /// </summary>
    public static DbContextOptions<DocketDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<DocketDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TaskRow>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Namespace).IsUnique();
            // Partial index over the dispatch hot path (§3.1: split hot from cold).
            e.HasIndex(t => new { t.State, t.Profile }).HasFilter("state = 'Submitted'");
            e.Property(t => t.State).HasConversion<string>();
            e.Property(t => t.CompletionMode).HasConversion<string>();
            e.Property(t => t.Version).IsRowVersion(); // maps to Postgres xmin
        });

        b.Entity<WorkerInstanceRow>(e =>
        {
            e.ToTable("worker_instances");
            e.HasKey(w => w.Id);
            e.HasIndex(w => w.TaskId);
        });

        b.Entity<RegisteredServiceRow>(e =>
        {
            e.ToTable("registered_services");
            e.HasKey(s => s.Seq);
            e.Property(s => s.Seq).UseIdentityAlwaysColumn();
            e.HasIndex(s => s.TaskId);
            e.HasIndex(s => new { s.TeamId, s.Name });
        });

        b.Entity<TaskEventRow>(e =>
        {
            e.ToTable("task_events");
            e.HasKey(ev => ev.Seq);
            e.Property(ev => ev.Seq).UseIdentityAlwaysColumn();
            e.HasIndex(ev => ev.TaskId);
            e.Property(ev => ev.FromState).HasConversion<string>();
            e.Property(ev => ev.ToState).HasConversion<string>();
        });
    }
}
