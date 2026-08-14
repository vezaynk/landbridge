using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class UniqueServiceNamePerTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §8.2: a service name is the Team-scoped ADDRESS of an endpoint, and everything
            // that resolves a forward — open_forward, an §8.4 preview, the §8.3 human path —
            // is handed a name and a Team and nothing else. Two rows could hold one name
            // (the register write was an unconditional insert), which made "which port does
            // this forward reach" a raffle between them. The invariant belongs in the
            // database, so two concurrent registrations can never both land.
            //
            // Existing duplicates are collapsed first, keeping the LOWEST seq per
            // (team_id, name) — the registration that held the name first, matching the
            // oldest-first resolution the mint read now uses. This deletes rows, and that is
            // the least-bad option in a table where it is defensible: a registration lives
            // only while its task is working (ClearServicesAndForwards deletes it), it is an
            // advertisement rather than a record of anything, and a duplicate name is exactly
            // the broken state this index exists to prevent — the losing worker's service was
            // never reliably reachable by that name anyway. A re-register (§8.2's own retry
            // guidance) is all it takes to come back under a name nothing else holds.
            migrationBuilder.Sql(
                """
                DELETE FROM registered_services s
                 USING registered_services keep
                 WHERE s.team_id = keep.team_id
                   AND s.name = keep.name
                   AND s.seq > keep.seq
                """);

            migrationBuilder.DropIndex(
                name: "ix_registered_services_team_id_name",
                table: "registered_services");

            migrationBuilder.CreateIndex(
                name: "ix_registered_services_team_id_name",
                table: "registered_services",
                columns: ["team_id", "name"],
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to the plain lookup index. The rows the Up collapsed do not come back —
            // nothing here knows what they were — and they do not need to: every one belonged
            // to a task that has long since left working, and reachability is re-declared by
            // a live worker calling register_service, never restored from history.
            migrationBuilder.DropIndex(
                name: "ix_registered_services_team_id_name",
                table: "registered_services");

            migrationBuilder.CreateIndex(
                name: "ix_registered_services_team_id_name",
                table: "registered_services",
                columns: ["team_id", "name"]);
        }
    }
}
