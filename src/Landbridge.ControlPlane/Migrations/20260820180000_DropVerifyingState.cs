using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations;

/// <summary>
/// Derived <c>SessionState.Verifying</c> is gone. Rows that still store it
/// rewrite to Working; <c>awaiting_report</c> on the message column is the report.
/// </summary>
[DbContext(typeof(LandbridgeDbContext))]
[Migration("20260820180000_DropVerifyingState")]
public class DropVerifyingState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE sessions SET state = 'Working' WHERE state = 'Verifying';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE sessions SET state = 'Verifying'
            WHERE state = 'Working' AND message_state = 'AwaitingReport';
            """);
    }
}
