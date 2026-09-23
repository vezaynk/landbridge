using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Landbridge.ControlPlane.Migrations
{
    /// <summary>
    /// Outbound runner commands. Unacked rows replay on GET /runner/events.
    /// </summary>
    [DbContext(typeof(LandbridgeDbContext))]
    [Migration("20260923000000_RunnerOutbox")]
    public partial class RunnerOutboxMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runner_outbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    machine_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_runner_outbox", x => x.id));

            migrationBuilder.CreateIndex(
                name: "ix_runner_outbox_machine_id",
                table: "runner_outbox",
                column: "machine_id",
                filter: "acked_at IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "runner_outbox");
    }
}
