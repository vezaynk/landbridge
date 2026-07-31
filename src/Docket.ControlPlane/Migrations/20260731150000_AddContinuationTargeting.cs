using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddContinuationTargeting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<System.Guid>(
                name: "continues_task_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preferred_machine",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "on_machine_gone",
                table: "tasks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "continues_task_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "preferred_machine",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "on_machine_gone",
                table: "tasks");
        }
    }
}
