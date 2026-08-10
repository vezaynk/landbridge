using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class RenameResumeDirToWorkDirTask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column landed as resume_dir_task_id when directory inheritance was thought
            // to be a resume mechanism. It is not: a continuation runs in its predecessor's
            // directory whether or not it resumes that transcript (§7, §11), so the name is
            // now work_dir_task_id. A rename rather than an edit of the migration that
            // introduced it, because that one has shipped — rewriting it would leave any
            // database that already applied it holding the old column and an unmatched
            // history row.
            migrationBuilder.RenameColumn(
                name: "resume_dir_task_id",
                table: "tasks",
                newName: "work_dir_task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "work_dir_task_id",
                table: "tasks",
                newName: "resume_dir_task_id");
        }
    }
}
