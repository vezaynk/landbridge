using System;
using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Landbridge.ControlPlane.Migrations
{
    /// <summary>
    /// The machine-handle columns become real <c>uuid</c>s: <c>worker_instances.machine_id</c>,
    /// <c>sessions.park_machine</c>, <c>sessions.preferred_machine</c> and
    /// <c>relay_grants.consumer_machine</c>.
    ///
    /// <para>They only ever held a machine id's string form — the runner endpoint
    /// registers the authenticated <c>Principal.Machine</c>, so the registry key the
    /// dispatcher hands over is a real machine id — but the columns' type let readers
    /// compare renderings instead of ids, which silently denied a worker access to its
    /// own machine's processes.</para>
    ///
    /// <para>The cast preserves every value the plane actually wrote (the "D" form
    /// the regex matches) and nulls anything else rather than failing the migration.
    /// A null already means "the plane cannot say where that attempt ran", so a row
    /// that held something unreadable degrades to the answer the column already has
    /// a meaning for.</para>
    /// </summary>
    [DbContext(typeof(LandbridgeDbContext))]
    [Migration("20260917000000_MachineHandlesAreUuids")]
    public partial class MachineHandlesAreUuids : Migration
    {
        private const string UuidPattern =
            "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

        private static readonly (string Table, string Column)[] Columns =
        [
            ("worker_instances", "machine_id"),
            ("sessions", "park_machine"),
            ("sessions", "preferred_machine"),
            ("relay_grants", "consumer_machine"),
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Columns)
                migrationBuilder.Sql($"""
                    ALTER TABLE {table}
                        ALTER COLUMN {column} TYPE uuid
                        USING CASE
                            WHEN {column} ~ '{UuidPattern}' THEN {column}::uuid
                            ELSE NULL
                        END;
                    """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Columns)
                migrationBuilder.Sql($"""
                    ALTER TABLE {table}
                        ALTER COLUMN {column} TYPE text
                        USING {column}::text;
                    """);
        }
    }
}
