using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260820120000_OccupancyAndMessages")]
public class OccupancyAndMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Not in production: existing session rows may be dropped. Defaults match create.
        migrationBuilder.Sql("TRUNCATE TABLE sessions CASCADE;");

        migrationBuilder.AddColumn<string>(
            name: "occupancy_desired",
            table: "sessions",
            type: "text",
            nullable: false,
            defaultValue: "Running");

        migrationBuilder.AddColumn<string>(
            name: "occupancy_observed",
            table: "sessions",
            type: "text",
            nullable: false,
            defaultValue: "None");

        migrationBuilder.AddColumn<string>(
            name: "health",
            table: "sessions",
            type: "text",
            nullable: false,
            defaultValue: "Ok");

        migrationBuilder.AddColumn<bool>(
            name: "hidden",
            table: "sessions",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "message_state",
            table: "sessions",
            type: "text",
            nullable: false,
            defaultValue: "Idle");

        migrationBuilder.AddColumn<string>(
            name: "message_verdict",
            table: "sessions",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "pending_spawn",
            table: "sessions",
            type: "text",
            nullable: true,
            defaultValue: "New");

        migrationBuilder.AddColumn<bool>(
            name: "pull_redelivered",
            table: "sessions",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "message_pulled_at",
            table: "sessions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_sessions_profile_occupancy_desired_occupancy_observed_health",
            table: "sessions",
            columns: new[] { "profile", "occupancy_desired", "occupancy_observed", "health" },
            filter: "occupancy_desired = 'Running' AND health = 'Ok' AND hidden = false AND occupancy_observed IN ('None','OnDisk') AND current_instance_id IS NULL AND pending_spawn IN ('New','Load')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_sessions_profile_occupancy_desired_occupancy_observed_health",
            table: "sessions");
        migrationBuilder.DropColumn(name: "occupancy_desired", table: "sessions");
        migrationBuilder.DropColumn(name: "occupancy_observed", table: "sessions");
        migrationBuilder.DropColumn(name: "health", table: "sessions");
        migrationBuilder.DropColumn(name: "hidden", table: "sessions");
        migrationBuilder.DropColumn(name: "message_state", table: "sessions");
        migrationBuilder.DropColumn(name: "message_verdict", table: "sessions");
        migrationBuilder.DropColumn(name: "pending_spawn", table: "sessions");
        migrationBuilder.DropColumn(name: "pull_redelivered", table: "sessions");
        migrationBuilder.DropColumn(name: "message_pulled_at", table: "sessions");
    }
}
