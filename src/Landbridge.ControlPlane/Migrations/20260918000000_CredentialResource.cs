using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations
{
    /// <summary>
    /// RFC 8707 audience on a credential: which resource server a token was minted for.
    ///
    /// <para>Nullable with no backfill, deliberately. Every credential already in flight
    /// was minted before there was an audience to record, and a null reads as "not
    /// audience-bound" — accepted by any resource server. Backfilling them to the current
    /// resource id would be inventing a fact, and defaulting them to anything else would
    /// revoke every live session on deploy.</para>
    /// </summary>
    [DbContext(typeof(LandbridgeDbContext))]
    [Migration("20260918000000_CredentialResource")]
    public partial class CredentialResource : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<string>(
                name: "resource",
                table: "credentials",
                type: "text",
                nullable: true);

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "resource", table: "credentials");
    }
}
