using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations
{
    /// <summary>
    /// Whether an outbound command waits for its machine. Transient rows are delivered
    /// live and never replayed, so a transcript read does not arrive for a waiter that
    /// gave up during the disconnect.
    /// </summary>
    [DbContext(typeof(LandbridgeDbContext))]
    [Migration("20260924000000_RunnerOutboxDurable")]
    public partial class RunnerOutboxDurableMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<bool>(
                name: "durable",
                table: "runner_outbox",
                type: "boolean",
                nullable: false,
                defaultValue: true);

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "durable", table: "runner_outbox");
    }
}
