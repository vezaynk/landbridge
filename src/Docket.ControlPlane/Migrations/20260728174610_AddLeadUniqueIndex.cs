using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_credentials_one_live_lead_per_team",
                table: "credentials",
                column: "team_id",
                unique: true,
                filter: "kind = 'Lead' AND revoked = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credentials_one_live_lead_per_team",
                table: "credentials");
        }
    }
}
