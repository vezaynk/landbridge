using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddRelayGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "relay_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_hash = table.Column<string>(type: "text", nullable: false),
                    forward_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    producer_task_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                name: "ix_relay_grants_producer_task_id",
                table: "relay_grants",
                column: "producer_task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "relay_grants");
        }
    }
}
