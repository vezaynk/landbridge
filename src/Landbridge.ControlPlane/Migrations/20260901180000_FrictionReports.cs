using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260901180000_FrictionReports")]
public class FrictionReports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "friction_reports",
            columns: table => new
            {
                seq = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                role = table.Column<string>(type: "text", nullable: false),
                team_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: true),
                human_id = table.Column<Guid>(type: "uuid", nullable: true),
                message = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_friction_reports", x => x.seq);
            });

        migrationBuilder.CreateIndex(
            name: "ix_friction_reports_at",
            table: "friction_reports",
            column: "at");

        migrationBuilder.CreateIndex(
            name: "ix_friction_reports_team_id",
            table: "friction_reports",
            column: "team_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "friction_reports");
    }
}
