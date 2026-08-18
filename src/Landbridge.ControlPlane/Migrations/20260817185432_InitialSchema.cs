using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Landbridge.ControlPlane.Migrations
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
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                name: "lead_machine_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    human_id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_machine_bindings", x => x.id);
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
                name: "oauth_authorization_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: false),
                    redirect_uri = table.Column<string>(type: "text", nullable: false),
                    code_challenge = table.Column<string>(type: "text", nullable: false),
                    resource = table.Column<string>(type: "text", nullable: true),
                    scope = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_oauth_authorization_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "preview_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label_hash = table.Column<string>(type: "text", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    auth_policy = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preview_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "registered_services",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                name: "relay_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_hash = table.Column<string>(type: "text", nullable: false),
                    forward_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consumer_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    producer_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_by_consumer_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_by_producer_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_relay_grants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "session_events",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    from_state = table.Column<string>(type: "text", nullable: true),
                    to_state = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    input_kind = table.Column<string>(type: "text", nullable: true),
                    liveness_reason = table.Column<string>(type: "text", nullable: true),
                    auth_operation = table.Column<string>(type: "text", nullable: true),
                    auth_target = table.Column<string>(type: "text", nullable: true),
                    auth_error_code = table.Column<string>(type: "text", nullable: true),
                    auth_missing_scope = table.Column<string>(type: "text", nullable: true),
                    subagent_id = table.Column<string>(type: "text", nullable: true),
                    subagent_parent_id = table.Column<string>(type: "text", nullable: true),
                    permission_verdict = table.Column<string>(type: "text", nullable: true),
                    permission_answerer = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_events", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "session_usage",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false),
                    reasoning_output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_usage", x => new { x.session_id, x.model });
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    profile = table.Column<string>(type: "text", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    infrastructure_requeues = table.Column<int>(type: "integer", nullable: false),
                    verification_failures = table.Column<int>(type: "integer", nullable: false),
                    verification_retry_limit = table.Column<int>(type: "integer", nullable: false),
                    infrastructure_requeue_limit = table.Column<int>(type: "integer", nullable: false),
                    last_requeue_reason = table.Column<string>(type: "text", nullable: true),
                    current_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    park_machine = table.Column<string>(type: "text", nullable: true),
                    blocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    input_kind = table.Column<string>(type: "text", nullable: true),
                    input_question = table.Column<string>(type: "text", nullable: true),
                    input_answer = table.Column<string>(type: "text", nullable: true),
                    permission_tool = table.Column<string>(type: "text", nullable: true),
                    permission_verdict = table.Column<string>(type: "text", nullable: true),
                    permission_escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    permission_escalation_reason = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    workspace = table.Column<string>(type: "text", nullable: true),
                    result_reference = table.Column<string>(type: "text", nullable: true),
                    worker_report = table.Column<string>(type: "text", nullable: true),
                    trace_context = table.Column<string>(type: "text", nullable: true),
                    harness_session_ref = table.Column<string>(type: "text", nullable: true),
                    continues_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_dir_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    preferred_machine = table.Column<string>(type: "text", nullable: true),
                    on_machine_gone = table.Column<string>(type: "text", nullable: true),
                    completion_provenance = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "team_forward_usage",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    forwarded_bytes = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_forward_usage", x => x.team_id);
                });

            migrationBuilder.CreateTable(
                name: "worker_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    machine_id = table.Column<string>(type: "text", nullable: true)
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
                name: "ix_credentials_one_live_lead_per_team",
                table: "credentials",
                column: "team_id",
                unique: true,
                filter: "kind = 'Lead' AND revoked = false");

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
                name: "ix_lead_machine_bindings_one_live_per_human",
                table: "lead_machine_bindings",
                column: "human_id",
                unique: true,
                filter: "revoked = false");

            migrationBuilder.CreateIndex(
                name: "ix_lead_machine_bindings_one_live_per_machine",
                table: "lead_machine_bindings",
                column: "machine_id",
                unique: true,
                filter: "revoked = false");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_codes_code_hash",
                table: "oauth_authorization_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_preview_mappings_label_hash",
                table: "preview_mappings",
                column: "label_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_registered_services_session_id",
                table: "registered_services",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_registered_services_team_id_name",
                table: "registered_services",
                columns: new[] { "team_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_relay_grants_forward_id",
                table: "relay_grants",
                column: "forward_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_relay_grants_grant_hash",
                table: "relay_grants",
                column: "grant_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_relay_grants_producer_session_id",
                table: "relay_grants",
                column: "producer_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_events_session_id",
                table: "session_events",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_usage_team_id",
                table: "session_usage",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_namespace",
                table: "sessions",
                column: "namespace",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sessions_state_profile",
                table: "sessions",
                columns: new[] { "state", "profile" },
                filter: "state = 'Submitted'");

            migrationBuilder.CreateIndex(
                name: "ix_worker_instances_session_id",
                table: "worker_instances",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credentials");

            migrationBuilder.DropTable(
                name: "lead_events");

            migrationBuilder.DropTable(
                name: "lead_machine_bindings");

            migrationBuilder.DropTable(
                name: "machines");

            migrationBuilder.DropTable(
                name: "oauth_authorization_codes");

            migrationBuilder.DropTable(
                name: "preview_mappings");

            migrationBuilder.DropTable(
                name: "registered_services");

            migrationBuilder.DropTable(
                name: "relay_grants");

            migrationBuilder.DropTable(
                name: "session_events");

            migrationBuilder.DropTable(
                name: "session_usage");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "team_forward_usage");

            migrationBuilder.DropTable(
                name: "worker_instances");
        }
    }
}
