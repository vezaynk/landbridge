using System;
using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations
{
    [DbContext(typeof(LandbridgeDbContext))]
    [Migration("20260904190000_MachineLiveness")]
    public partial class MachineLiveness : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_spoke_at",
                table: "machines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ready",
                table: "machines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "under_back_pressure",
                table: "machines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string[]>(
                name: "profiles",
                table: "machines",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");


            migrationBuilder.CreateIndex(
                name: "ix_machines_last_spoke_at",
                table: "machines",
                column: "last_spoke_at");

            migrationBuilder.CreateTable(
                name: "machine_processes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    machine_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    declared_by_session = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    exited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stdin_open = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_machine_processes", x => x.id);
                    table.ForeignKey(
                        name: "fk_machine_processes_machines_machine_id",
                        column: x => x.machine_id,
                        principalTable: "machines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_machine_processes_machine_id",
                table: "machine_processes",
                column: "machine_id");

            migrationBuilder.CreateIndex(
                name: "ix_machine_processes_machine_id_name",
                table: "machine_processes",
                columns: new[] { "machine_id", "name" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "machine_processes");
            migrationBuilder.DropIndex(name: "ix_machines_last_spoke_at", table: "machines");
            migrationBuilder.DropColumn(name: "last_spoke_at", table: "machines");
            migrationBuilder.DropColumn(name: "ready", table: "machines");
            migrationBuilder.DropColumn(name: "under_back_pressure", table: "machines");
            migrationBuilder.DropColumn(name: "profiles", table: "machines");
        }
    }
}
