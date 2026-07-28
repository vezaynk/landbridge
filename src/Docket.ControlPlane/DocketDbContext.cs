using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.EntityFrameworkCore;

namespace Docket.ControlPlane;

public sealed class DocketDbContext(DbContextOptions<DocketDbContext> options) : DbContext(options)
{
    public DbSet<TaskRow> Tasks => Set<TaskRow>();
    public DbSet<WorkerInstanceRow> WorkerInstances => Set<WorkerInstanceRow>();
    public DbSet<RegisteredServiceRow> RegisteredServices => Set<RegisteredServiceRow>();
    public DbSet<TaskEventRow> TaskEvents => Set<TaskEventRow>();
    public DbSet<CredentialRow> Credentials => Set<CredentialRow>();
    public DbSet<MachineRow> Machines => Set<MachineRow>();
    public DbSet<LeadEventRow> LeadEvents => Set<LeadEventRow>();

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

        b.Entity<CredentialRow>(e =>
        {
            e.ToTable("credentials");
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.TokenHash).IsUnique();
            e.HasIndex(c => c.MachineId);
            e.Property(c => c.Kind).HasConversion<string>();
            // One live Lead per Team is the database's invariant, not a read's
            // (§9 check 6): a partial unique index makes two concurrent claims
            // impossible to both win, exactly as dispatch trusts the row lock
            // rather than a check-then-act read.
            e.HasIndex(c => c.TeamId).IsUnique()
                .HasFilter("kind = 'Lead' AND revoked = false")
                .HasDatabaseName("ix_credentials_one_live_lead_per_team");
        });

        b.Entity<MachineRow>(e =>
        {
            e.ToTable("machines");
            e.HasKey(m => m.Id);
        });

        b.Entity<LeadEventRow>(e =>
        {
            e.ToTable("lead_events");
            e.HasKey(ev => ev.Seq);
            e.Property(ev => ev.Seq).UseIdentityAlwaysColumn();
            e.HasIndex(ev => ev.TeamId);
            e.Property(ev => ev.Kind).HasConversion<string>();
        });
    }
}
