using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskEventTelemetryColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "input_kind",
                table: "task_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "auth_operation",
                table: "task_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "auth_target",
                table: "task_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "auth_error_code",
                table: "task_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "auth_missing_scope",
                table: "task_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subagent_id",
                table: "task_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subagent_parent_id",
                table: "task_events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "input_kind",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "auth_operation",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "auth_target",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "auth_error_code",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "auth_missing_scope",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "subagent_id",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "subagent_parent_id",
                table: "task_events");
        }
    }
}
