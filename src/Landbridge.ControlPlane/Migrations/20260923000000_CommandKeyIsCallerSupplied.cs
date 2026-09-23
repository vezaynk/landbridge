using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations
{
    /// <summary>
    /// <c>command_queue.idempotency_key</c> becomes nullable, and its uniqueness applies
    /// only where one was supplied.
    ///
    /// <para>The column was filled with a hash of the payload, which made the key a
    /// function of what was said rather than of who said it when — so a legitimate repeat
    /// was indistinguishable from a retry and lost, permanently, with the caller handed
    /// the earlier command's outcome. Existing rows are cleared rather than rewritten:
    /// there is no attempt identity to recover from a content hash, and the alternative
    /// is keeping keys that would go on swallowing.</para>
    /// </summary>
    [DbContext(typeof(LandbridgeDbContext))]
    [Migration("20260923000000_CommandKeyIsCallerSupplied")]
    public partial class CommandKeyIsCallerSupplied : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE command_queue ALTER COLUMN idempotency_key DROP NOT NULL;
                UPDATE command_queue SET idempotency_key = NULL;
                DROP INDEX IF EXISTS ix_command_queue_actor_kind_actor_id_idempotency_key;
                CREATE UNIQUE INDEX ix_command_queue_actor_kind_actor_id_idempotency_key
                    ON command_queue (actor_kind, actor_id, idempotency_key)
                    WHERE idempotency_key IS NOT NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_command_queue_actor_kind_actor_id_idempotency_key;
                UPDATE command_queue SET idempotency_key = id::text WHERE idempotency_key IS NULL;
                ALTER TABLE command_queue ALTER COLUMN idempotency_key SET NOT NULL;
                CREATE UNIQUE INDEX ix_command_queue_actor_kind_actor_id_idempotency_key
                    ON command_queue (actor_kind, actor_id, idempotency_key);
                """);
        }
    }
}
