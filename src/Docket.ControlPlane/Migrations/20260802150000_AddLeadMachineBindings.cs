using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadMachineBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_machine_bindings",
                columns: table => new
                {
                    id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    human_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    machine_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    bound_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_machine_bindings", x => x.id);
                });

            // One live binding per human and per machine (§8.3 human path). Partial
            // unique indexes over the unrevoked rows, so an unbind frees the slot and
            // two concurrent binds can never both win.
            migrationBuilder.CreateIndex(
                name: "ix_lead_machine_bindings_one_live_per_human",
                table: "lead_machine_bindings",
                column: "human_id",
                unique: true,
                filter: "revoked = false");

            migrationBuilder.CreateIndex(
                name: "ix_lead_machine_bindings_one_live_per_machine",
                table: "lead_machine_bindings",
                column: "machine_id",
                unique: true,
                filter: "revoked = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_machine_bindings");
        }
    }
}
