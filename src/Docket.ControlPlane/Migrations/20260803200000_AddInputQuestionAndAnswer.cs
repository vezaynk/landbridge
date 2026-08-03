using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddInputQuestionAndAnswer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §10/§11 the human-in-the-loop channel's content: what the worker asked,
            // what it was answered, and the live kind of that request. All nullable —
            // every existing row has asked nothing, and a task that never asks keeps
            // all three null.
            migrationBuilder.AddColumn<string>(
                name: "input_kind",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "input_question",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "input_answer",
                table: "tasks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "input_answer",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "input_question",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "input_kind",
                table: "tasks");
        }
    }
}
