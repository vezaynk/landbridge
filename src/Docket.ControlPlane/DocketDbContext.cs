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
    public DbSet<LeadMachineBindingRow> LeadMachineBindings => Set<LeadMachineBindingRow>();
    public DbSet<RelayGrantRow> RelayGrants => Set<RelayGrantRow>();
    public DbSet<PreviewMappingRow> PreviewMappings => Set<PreviewMappingRow>();
    public DbSet<OAuthAuthorizationCodeRow> OAuthAuthorizationCodes => Set<OAuthAuthorizationCodeRow>();
    public DbSet<TeamBudgetRow> TeamBudgets => Set<TeamBudgetRow>();
    public DbSet<TeamForwardUsageRow> TeamForwardUsage => Set<TeamForwardUsageRow>();

    /// <summary>The channel dispatch/transition NOTIFYs land on (§3.1 LISTEN/NOTIFY).</summary>
    public const string EventChannel = "docket_task_events";

    /// <summary>One live lead↔machine binding per human (§8.3 human path) — named so
    /// <see cref="LeadMachineBindingService"/> can tell the two races apart from the
    /// constraint name on a unique violation.</summary>
    public const string OneLiveBindingPerHumanIndex = "ix_lead_machine_bindings_one_live_per_human";

    /// <summary>One live lead↔machine binding per machine (§8.3 human path).</summary>
    public const string OneLiveBindingPerMachineIndex = "ix_lead_machine_bindings_one_live_per_machine";

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
            // §9 check 4 completion provenance stores as its enum name, like the
            // other enum columns; null until the task reaches completed.
            e.Property(t => t.CompletionProvenance).HasConversion<string>();
            // §6/§11 continuation targeting: the machine-gone policy stores as its
            // enum name, exactly like State/CompletionMode above, so the dispatch
            // SQL can match the literal 'Degrade'.
            e.Property(t => t.OnMachineGone).HasConversion<string>();
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
            // The typed input-request kind stores as its enum name, exactly like
            // the from/to states above (§10/§12 derived telemetry, #50).
            e.Property(ev => ev.InputKind).HasConversion<string>();
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

        b.Entity<LeadMachineBindingRow>(e =>
        {
            e.ToTable("lead_machine_bindings");
            e.HasKey(x => x.Id);
            // One live binding per human and per machine (§8.3 human path), each a
            // partial unique index over the unrevoked rows — the same shape as
            // ix_credentials_one_live_lead_per_team: the invariant is the database's,
            // so two concurrent binds can never both win, and a revoked row keeps the
            // history without holding either slot.
            e.HasIndex(x => x.HumanId).IsUnique()
                .HasFilter("revoked = false")
                .HasDatabaseName(OneLiveBindingPerHumanIndex);
            e.HasIndex(x => x.MachineId).IsUnique()
                .HasFilter("revoked = false")
                .HasDatabaseName(OneLiveBindingPerMachineIndex);
        });

        b.Entity<RelayGrantRow>(e =>
        {
            e.ToTable("relay_grants");
            e.HasKey(g => g.Id);
            // One row per grant, one grant per forward pairing (§8.3): both are
            // looked up on the tunnel-open hot path, and the uniqueness is the
            // invariant, not a read's.
            e.HasIndex(g => g.GrantHash).IsUnique();
            e.HasIndex(g => g.ForwardId).IsUnique();
            // Revocation targets every live grant a leaving-working task produced
            // (ClearServicesAndForwards); index the column that join keys on.
            e.HasIndex(g => g.ProducerTaskId);
        });

        b.Entity<PreviewMappingRow>(e =>
        {
            e.ToTable("preview_mappings");
            e.HasKey(m => m.Id);
            // The label hash is the resolve hot-path lookup key (§8.4), and its
            // uniqueness is the invariant (one mapping per label), not a read's —
            // the same shape as every opaque credential's hash (§5).
            e.HasIndex(m => m.LabelHash).IsUnique();
            e.Property(m => m.AuthPolicy).HasConversion<string>();
        });

        b.Entity<OAuthAuthorizationCodeRow>(e =>
        {
            e.ToTable("oauth_authorization_codes");
            e.HasKey(c => c.Id);
            // The code hash is the lookup key on the token-exchange hot path, and
            // its uniqueness is the invariant (one row per issued code), not a
            // read's — exactly like every other opaque credential's hash (§5).
            e.HasIndex(c => c.CodeHash).IsUnique();
        });

        b.Entity<TeamBudgetRow>(e =>
        {
            e.ToTable("team_budgets");
            // The Team id IS the key: one ceiling per Team, so the row cannot be
            // duplicated and a concurrent commit contends on a single row (§9.9).
            e.HasKey(t => t.TeamId);
        });

        b.Entity<TeamForwardUsageRow>(e =>
        {
            e.ToTable("team_forward_usage");
            // Same shape and same reason as team_budgets: one row per Team, so concurrent
            // reports from different relays contend on a single row (§9.10).
            e.HasKey(t => t.TeamId);
        });
    }
}
