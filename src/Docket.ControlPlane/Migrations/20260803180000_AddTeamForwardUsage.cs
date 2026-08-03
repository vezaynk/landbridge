using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamForwardUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §9 check 10 / §9.10: bytes a Team has moved through relay forwards. Its own
            // table rather than columns on team_budgets, because that row records
            // authorization Docket GRANTED and this is the one quantity actually MEASURED —
            // and because a Team should not acquire a budget row merely by moving bytes.
            // Nothing is enforced on this: §8.3 forbids severing an established splice
            // mid-flight, so a byte ceiling's behaviour is unresolved. It is a visibility
            // signal, reported best-effort by the relay, monotonic because bytes already
            // moved cannot un-move. The Team id is the key: one row per Team, so concurrent
            // reports contend on a single row.
            migrationBuilder.CreateTable(
                name: "team_forward_usage",
                columns: table => new
                {
                    team_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    forwarded_bytes = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_forward_usage", x => x.team_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "team_forward_usage");
        }
    }
}
