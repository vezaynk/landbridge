using Docket.Meta.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Docket.Meta.Migrations
{
    /// <summary>
    /// Meta's initial schema. Hand-authored (the repo convention when <c>dotnet ef</c>
    /// SIGKILLs mid-scaffold — see Docket.ControlPlane/Migrations). Applied at runtime
    /// by <c>MigrateAsync</c>: the <c>[DbContext]</c> attribute associates it with
    /// <see cref="MetaDbContext"/> during the migrations-assembly scan, and
    /// <c>[Migration]</c> gives it its id — no design-time snapshot is needed to apply.
    /// </summary>
    [DbContext(typeof(MetaDbContext))]
    [Migration("20260801000000_InitialSchema")]
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hosts",
                columns: table => new
                {
                    id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    endpoint_uri = table.Column<string>(type: "text", nullable: false),
                    endpoint_kind = table.Column<string>(type: "text", nullable: false),
                    tls_ca_pem = table.Column<string>(type: "text", nullable: true),
                    tls_client_cert_pem = table.Column<string>(type: "text", nullable: true),
                    tls_client_key_pem = table.Column<string>(type: "text", nullable: true),
                    published_host = table.Column<string>(type: "text", nullable: false),
                    port_range_low = table.Column<int>(type: "integer", nullable: false),
                    port_range_high = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table => table.PrimaryKey("pk_hosts", x => x.id));

            migrationBuilder.CreateTable(
                name: "instances",
                columns: table => new
                {
                    id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    account_label = table.Column<string>(type: "text", nullable: true),
                    host_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    image_tag = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    failed_step = table.Column<string>(type: "text", nullable: true),
                    network_name = table.Column<string>(type: "text", nullable: true),
                    volume_name = table.Column<string>(type: "text", nullable: true),
                    pg_container_id = table.Column<string>(type: "text", nullable: true),
                    mcp_container_id = table.Column<string>(type: "text", nullable: true),
                    relay_container_id = table.Column<string>(type: "text", nullable: true),
                    mcp_published_port = table.Column<int>(type: "integer", nullable: true),
                    relay_published_port = table.Column<int>(type: "integer", nullable: true),
                    public_url = table.Column<string>(type: "text", nullable: true),
                    passphrase_hash = table.Column<string>(type: "text", nullable: false),
                    db_password = table.Column<string>(type: "text", nullable: false),
                    relay_bearer = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    suspended_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    destroyed_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instances", x => x.id);
                    table.ForeignKey(
                        name: "fk_instances_hosts_host_id",
                        column: x => x.host_id,
                        principalTable: "hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "instance_steps",
                columns: table => new
                {
                    id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<System.Guid>(type: "uuid", nullable: false),
                    step = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    external_ref = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<System.DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instance_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_instance_steps_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hosts_name",
                table: "hosts",
                column: "name",
                unique: true);

            // One live instance per name; a destroyed tombstone retains its name.
            migrationBuilder.CreateIndex(
                name: "ix_instances_name",
                table: "instances",
                column: "name",
                unique: true,
                filter: "destroyed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_instances_host_id",
                table: "instances",
                column: "host_id");

            migrationBuilder.CreateIndex(
                name: "ix_instance_steps_instance_id_step",
                table: "instance_steps",
                columns: new[] { "instance_id", "step" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "instance_steps");
            migrationBuilder.DropTable(name: "instances");
            migrationBuilder.DropTable(name: "hosts");
        }
    }
}
