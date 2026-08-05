using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddRequeueCapAndLivenessReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §9 check 7 (#73): the cap a task's infrastructure requeues are judged
            // against, stamped at creation like verification_retry_limit. The default
            // applies it to tasks that already exist — a task mid-requeue-loop when this
            // lands gets the cap too, which is the point.
            migrationBuilder.AddColumn<int>(
                name: "infrastructure_requeue_limit",
                table: "tasks",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            // §6 (#73): why the task was last requeued — the live one on the row, and
            // every requeue's own on its event row. Both nullable and stored as the enum
            // name like the other enum columns: a task that was never requeued, and every
            // event that is not a requeue, leave them null.
            migrationBuilder.AddColumn<string>(
                name: "last_requeue_reason",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "liveness_reason",
                table: "task_events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "liveness_reason",
                table: "task_events");

            migrationBuilder.DropColumn(
                name: "last_requeue_reason",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "infrastructure_requeue_limit",
                table: "tasks");
        }
    }
}
