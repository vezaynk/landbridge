using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class DropTeamBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The dollar budget subsystem is removed (§9's note, 2026-08-12): the Team ceiling,
            // the per-dispatch cap it handed each harness, and the committed-authorization
            // total. What replaces it is measured telemetry the harness itself reports (§12),
            // which is a different number and gets its own storage — so this table is not
            // renamed into that role, it goes.
            //
            // Dropping rows, and unlike the columns the previous migration dropped these WERE
            // read: a Team's ceiling and its committed total drove admission and containment.
            // Down restores the shape, never the amounts. That is the honest limit of a
            // reversal here, and it is the reason the design is written down in §9 rather than
            // left to be reconstructed from this file: reviving the feature means re-entering
            // the ceilings, which only a human knows.
            migrationBuilder.DropTable(name: "team_budgets");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The shape exactly as 20260803120000_AddTeamBudgets created it, so a revival
            // starts from the schema the removed design assumed. Every Team comes back
            // unconfigured, which is that design's own default for "no ceiling": unbounded.
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
    }
}
