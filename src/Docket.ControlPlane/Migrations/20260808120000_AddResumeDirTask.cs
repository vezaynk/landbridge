using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddResumeDirTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §11 continuation, directory half: the task whose machine-local work dir holds
            // the harness session this task resumes — Claude Code resumes a session only
            // from the directory that created it, and a continuation runs under a new task
            // id. Nullable, and null means "this task's own dir", so every existing row
            // keeps exactly today's behaviour. Distinct from continues_task_id (lineage)
            // because a chain does not create a session per link: the session stays in the
            // root task's dir, so this is seeded transitively at creation.
            migrationBuilder.AddColumn<Guid>(
                name: "resume_dir_task_id",
                table: "tasks",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resume_dir_task_id",
                table: "tasks");
        }
    }
}
