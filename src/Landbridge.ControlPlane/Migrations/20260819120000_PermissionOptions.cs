using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260819120000_PermissionOptions")]
public class PermissionOptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "permission_option_id",
            table: "sessions",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "permission_options",
            table: "sessions",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "permission_option_id", table: "sessions");
        migrationBuilder.DropColumn(name: "permission_options", table: "sessions");
    }
}
