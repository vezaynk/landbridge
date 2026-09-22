using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations
{
    /// <summary>
    /// Session-command accept queue. Façades insert queued; Core drains with
    /// SKIP LOCKED. Pending is a Hub GET.
    /// </summary>
    [DbContext(typeof(LandbridgeDbContext))]
    [Migration("20260922000000_CommandQueue")]
    public partial class CommandQueueMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "command_queue",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    actor_kind = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    rule = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    slug = table.Column<string>(type: "text", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_command_queue", x => x.id));

            migrationBuilder.CreateIndex(
                name: "ix_command_queue_actor_kind_actor_id_idempotency_key",
                table: "command_queue",
                columns: new[] { "actor_kind", "actor_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_command_queue_status",
                table: "command_queue",
                column: "status",
                filter: "status = 'queued'");

            migrationBuilder.CreateIndex(
                name: "ix_command_queue_team_id",
                table: "command_queue",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "ix_command_queue_session_id",
                table: "command_queue",
                column: "session_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "command_queue");
    }
}
