using System;
using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;



#nullable disable

namespace Landbridge.ControlPlane.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(LandbridgeDbContext))]
    [Migration("20260904183000_HubQueue")]
    public partial class HubQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "hub_queue",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    topic = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hub_queue", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hub_queue_created_at",
                table: "hub_queue",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_hub_queue_topic_entity_id_id",
                table: "hub_queue",
                columns: new[] { "topic", "entity_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "hub_queue");
        }
    }
}
