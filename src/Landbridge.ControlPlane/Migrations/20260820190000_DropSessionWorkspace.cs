using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

/// <summary>
/// The MCP workspace blob is gone. Description is the brief.
/// </summary>
[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260820190000_DropSessionWorkspace")]
public class DropSessionWorkspace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "workspace", table: "sessions");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "workspace",
            table: "sessions",
            type: "text",
            nullable: true);
    }
}
