using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260901190000_LeadTeams")]
public class LeadTeams : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "lead_teams",
            columns: table => new
            {
                team_id = table.Column<Guid>(type: "uuid", nullable: false),
                lead_credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_lead_teams", x => x.team_id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_lead_teams_lead_credential_id",
            table: "lead_teams",
            column: "lead_credential_id");

        migrationBuilder.Sql(
            """
            INSERT INTO lead_teams (team_id, lead_credential_id, created_at)
            SELECT team_id, id, created_at
            FROM credentials
            WHERE kind = 'Lead' AND team_id IS NOT NULL AND NOT revoked
            ON CONFLICT (team_id) DO NOTHING;
            """);

        migrationBuilder.DropIndex(
            name: "ix_credentials_one_live_lead_per_team",
            table: "credentials");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_credentials_one_live_lead_per_team",
            table: "credentials",
            column: "team_id",
            unique: true,
            filter: "kind = 'Lead' AND revoked = false");

        migrationBuilder.DropTable(name: "lead_teams");
    }
}
