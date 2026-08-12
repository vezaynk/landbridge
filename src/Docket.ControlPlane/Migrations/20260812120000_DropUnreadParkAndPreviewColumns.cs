using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class DropUnreadParkAndPreviewColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Four columns that were written and never read back.
            //
            // The park record (§11) is one fact — the machine redispatch should prefer — and
            // park_machine holds it. The other three were snapshots of facts that live
            // elsewhere and are read live from there: park_session_ref and park_attempt were
            // copied out of tasks.harness_session_ref and tasks.attempt on the way into a
            // park and read back by nothing (dispatch reads the live columns, which is why
            // the snapshots could drift from them unnoticed), and park_directory never had a
            // producer at all — every caller passed null, because the plane cannot observe a
            // machine's filesystem layout. §11's directory inheritance is expressed as a task
            // id on the dispatch command instead (work_dir_task_id).
            //
            // preview_mappings.created_at was written at mint and never consulted: a
            // mapping's whole lifetime is expires_at, which is what resolve checks.
            //
            // Dropping data, so Down cannot restore what was in these columns — it restores
            // the shape only. That is honest for these four specifically: nothing read them,
            // so nothing can have depended on their contents, and a re-added park_directory
            // would be null on every row exactly as it was before.
            migrationBuilder.DropColumn(
                name: "park_attempt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "park_directory",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "park_session_ref",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "preview_mappings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "park_attempt",
                table: "tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "park_directory",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "park_session_ref",
                table: "tasks",
                type: "text",
                nullable: true);

            // Non-null on the way back, so it needs a default for any existing row. The epoch
            // rather than now(): a restored stamp that claims the mapping was created at
            // migration time would be a fabricated fact, and this column's whole problem was
            // that nobody read it — an obviously-sentinel value says so.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "preview_mappings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));
        }
    }
}
