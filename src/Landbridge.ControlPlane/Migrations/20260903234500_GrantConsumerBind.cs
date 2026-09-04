using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260903234500_GrantConsumerBind")]
public class GrantConsumerBind : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "consumer_machine",
            table: "relay_grants",
            type: "text",
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "consumer_port",
            table: "relay_grants",
            type: "integer",
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "ix_relay_grants_consumer_machine",
            table: "relay_grants",
            column: "consumer_machine");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "ix_relay_grants_consumer_machine", table: "relay_grants");
        migrationBuilder.DropColumn(name: "consumer_machine", table: "relay_grants");
        migrationBuilder.DropColumn(name: "consumer_port", table: "relay_grants");
    }
}
