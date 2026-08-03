using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §9 check 9 / §9.9: a Team's spend ceiling and what has been AUTHORIZED
            // against it. Not measured spend — nothing ingests spend telemetry — so
            // committed_usd is the sum of per-dispatch caps handed out, and it only ever
            // increases. The Team id is the key: one ceiling per Team, so a concurrent
            // commit contends on a single row.
            migrationBuilder.CreateTable(
                name: "team_budgets",
                columns: table => new
                {
                    team_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    ceiling_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    per_task_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    committed_usd = table.Column<decimal>(type: "numeric", nullable: false),
                    updated_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_budgets", x => x.team_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "team_budgets");
        }
    }
}
