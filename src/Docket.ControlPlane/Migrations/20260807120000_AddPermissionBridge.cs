using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §11 permission bridge. The tool a pending permission request is about, the
            // verdict it was decided with, and the escalation that took the decision away
            // from the Lead. All nullable: every existing row has asked for no permission,
            // and even a task that does keeps the escalation pair null unless a Lead handed
            // it over. The request's proposed tool input reuses the existing
            // input_question column and its message reuses input_answer, so the two prose
            // fields of the exchange keep one home per direction.
            migrationBuilder.AddColumn<string>(
                name: "permission_tool",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "permission_verdict",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "permission_escalated_at",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "permission_escalation_reason",
                table: "tasks",
                type: "text",
                nullable: true);

            // The audit half (§12): which way each decision went and who had the authority
            // to make it, on the deciding event row itself — the same typed-telemetry shape
            // input_kind and liveness_reason already use.
            migrationBuilder.AddColumn<string>(
                name: "permission_verdict",
                table: "task_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "permission_answerer",
                table: "task_events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "permission_answerer",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "permission_verdict",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "permission_escalation_reason",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "permission_escalated_at",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "permission_verdict",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "permission_tool",
                table: "tasks");
        }
    }
}
