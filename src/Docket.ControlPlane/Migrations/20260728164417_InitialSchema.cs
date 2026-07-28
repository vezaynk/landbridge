using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    machine_id = table.Column<Guid>(type: "uuid", nullable: true),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    worker_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    human_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    evicted_by_human = table.Column<Guid>(type: "uuid", nullable: true),
                    evicted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credentials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lead_events",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    human_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prior_human_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_events", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "machines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    os = table.Column<string>(type: "text", nullable: false),
                    permission_level = table.Column<string>(type: "text", nullable: false),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machines", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "registered_services",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_registered_services", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "task_events",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    from_state = table.Column<string>(type: "text", nullable: true),
                    to_state = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_events", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "text", nullable: false),
                    completion_mode = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    profile = table.Column<string>(type: "text", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    infrastructure_requeues = table.Column<int>(type: "integer", nullable: false),
                    verification_failures = table.Column<int>(type: "integer", nullable: false),
                    verification_retry_limit = table.Column<int>(type: "integer", nullable: false),
                    current_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    park_machine = table.Column<string>(type: "text", nullable: true),
                    park_directory = table.Column<string>(type: "text", nullable: true),
                    park_session_ref = table.Column<string>(type: "text", nullable: true),
                    park_attempt = table.Column<int>(type: "integer", nullable: true),
                    completion_criteria = table.Column<string>(type: "text", nullable: false),
                    workspace = table.Column<string>(type: "text", nullable: true),
                    result_reference = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tasks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "worker_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worker_instances", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credentials_machine_id",
                table: "credentials",
                column: "machine_id");

            migrationBuilder.CreateIndex(
                name: "ix_credentials_token_hash",
                table: "credentials",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lead_events_team_id",
                table: "lead_events",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "ix_registered_services_task_id",
                table: "registered_services",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_registered_services_team_id_name",
                table: "registered_services",
                columns: new[] { "team_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_task_events_task_id",
                table: "task_events",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_namespace",
                table: "tasks",
                column: "namespace",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tasks_state_profile",
                table: "tasks",
                columns: new[] { "state", "profile" },
                filter: "state = 'Submitted'");

            migrationBuilder.CreateIndex(
                name: "ix_worker_instances_task_id",
                table: "worker_instances",
                column: "task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credentials");

            migrationBuilder.DropTable(
                name: "lead_events");

            migrationBuilder.DropTable(
                name: "machines");

            migrationBuilder.DropTable(
                name: "registered_services");

            migrationBuilder.DropTable(
                name: "task_events");

            migrationBuilder.DropTable(
                name: "tasks");

            migrationBuilder.DropTable(
                name: "worker_instances");
        }
    }
}
