using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

/// <summary>
/// A report is unread mail, not <c>awaiting_report</c>. The worker stays idle.
/// Per-session inbox delivery clears the flag.
/// </summary>
[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260820200000_ReportUnread")]
public class ReportUnread : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "report_unread",
            table: "sessions",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql(
            """
            UPDATE sessions
            SET report_unread = true,
                message_state = 'Idle',
                message_id = NULL,
                last_message_id = COALESCE(last_message_id, message_id),
                last_message_terminal = COALESCE(last_message_terminal, 'Completed')
            WHERE message_state = 'AwaitingReport';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE sessions
            SET message_state = 'AwaitingReport'
            WHERE report_unread = true AND message_state = 'Idle' AND hidden = false;
            """);

        migrationBuilder.DropColumn(name: "report_unread", table: "sessions");
    }
}
