using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260820140000_MessageEnvelopeId")]
public class MessageEnvelopeId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "message_id",
            table: "sessions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "last_message_id",
            table: "sessions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "last_message_terminal",
            table: "sessions",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "message_opened_at",
            table: "sessions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_message_closed_at",
            table: "sessions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_sessions_team_id_message_id",
            table: "sessions",
            columns: ["team_id", "message_id"]);

        migrationBuilder.CreateIndex(
            name: "ix_sessions_team_id_last_message_id",
            table: "sessions",
            columns: ["team_id", "last_message_id"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "ix_sessions_team_id_message_id", table: "sessions");
        migrationBuilder.DropIndex(name: "ix_sessions_team_id_last_message_id", table: "sessions");
        migrationBuilder.DropColumn(name: "message_id", table: "sessions");
        migrationBuilder.DropColumn(name: "last_message_id", table: "sessions");
        migrationBuilder.DropColumn(name: "last_message_terminal", table: "sessions");
        migrationBuilder.DropColumn(name: "message_opened_at", table: "sessions");
        migrationBuilder.DropColumn(name: "last_message_closed_at", table: "sessions");
    }
}
