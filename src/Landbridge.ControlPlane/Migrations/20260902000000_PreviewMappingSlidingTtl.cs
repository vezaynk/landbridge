using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260902000000_PreviewMappingSlidingTtl")]
public class PreviewMappingSlidingTtl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<TimeSpan>(
            name: "ttl",
            table: "preview_mappings",
            type: "interval",
            nullable: false,
            defaultValue: new TimeSpan(2, 0, 0));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ttl",
            table: "preview_mappings");
    }
}
