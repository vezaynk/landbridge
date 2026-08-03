using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerInstanceMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §12: the machine a dispatch ran on, recorded on the one row per dispatch, so a
            // terminal task's machine-local transcript can still be found. Nullable — rows
            // written before this column have no machine, and the dashboard says so rather
            // than guessing.
            migrationBuilder.AddColumn<string>(
                name: "machine_id",
                table: "worker_instances",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "machine_id",
                table: "worker_instances");
        }
    }
}
