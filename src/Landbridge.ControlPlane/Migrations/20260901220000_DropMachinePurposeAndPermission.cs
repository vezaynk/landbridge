using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260901220000_DropMachinePurposeAndPermission")]
public class DropMachinePurposeAndPermission : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "permission_level",
            table: "machines");

        migrationBuilder.DropColumn(
            name: "purpose",
            table: "machines");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "permission_level",
            table: "machines",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "purpose",
            table: "machines",
            type: "text",
            nullable: false,
            defaultValue: "");
    }
}
