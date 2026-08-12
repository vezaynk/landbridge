using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §10 telemetry ingest / §12 measured view: what a task's harness said it consumed,
            // per model. Measured and reported — enforced on by nothing, which is the whole
            // difference from the dollar ceiling this replaces (§9's removal note).
            //
            // (task_id, model) is the key because one dispatch legitimately spans several
            // models: a Claude worker whose subagents ran on a cheaper one reports each
            // separately, and collapsing them would throw away the distinction the view exists
            // to show. Reports are cumulative-to-date and upserted to the high-water mark, so a
            // report lost to §10's best-effort ring costs freshness and never correctness.
            //
            // model carries ONLY what the harness named — a model the plane asserted (from a
            // profile declaration or a spawn argv) is deliberately not storable here, because it
            // would render inside a surface labelled "reported by the harness" (§2 principle 2).
            // Claude names its models; Codex names none, so a Codex row is unattributed and §12
            // renders "not reported" against real counts.
            //
            // model is NOT NULL with an empty-string default, and the empty string IS the
            // unnamed model: a composite primary key cannot contain a NULL, and a surrogate key
            // plus a partial unique index would hide the one invariant that matters here (one
            // row per task per model). The read view maps '' back to null.
            //
            // The four token columns are DISJOINT by construction — docketd normalizes a harness
            // that counts cache hits inside its input total before reporting (Codex does; Claude
            // does not), so summing them here is correct for both. reasoning_output_tokens is a
            // portion OF output_tokens, stored because it is a real cost lever and deliberately
            // excluded from every total.
            //
            // cost_usd is nullable because it holds only what a harness COMPUTED: Claude reports
            // dollars, Codex reports none anywhere, and nothing multiplies tokens into this
            // column. A NULL here means "no cost was measured", which is not the same claim as
            // zero — see ModelPricing.
            migrationBuilder.CreateTable(
                name: "task_usage",
                columns: table => new
                {
                    task_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    cache_read_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cache_write_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    reasoning_output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    reported_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    team_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_usage", x => new { x.task_id, x.model });
                });

            // The §12 Team roll-up filters on this and nothing else. team_id is denormalized off
            // the task so that roll-up is one indexed scan rather than a join per row.
            migrationBuilder.CreateIndex(
                name: "ix_task_usage_team_id",
                table: "task_usage",
                column: "team_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping measured history, and nothing can reconstruct it: these rows are what
            // harnesses reported at the time, and the harnesses are gone. Honest for this table
            // specifically — nothing enforces on it, so no decision was ever made from a value
            // here that a rollback would strand.
            migrationBuilder.DropTable(name: "task_usage");
        }
    }
}
