using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Classifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation");

            migrationBuilder.CreateTable(
                name: "classification",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<long>(type: "bigint", nullable: false),
                    schema_key = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false, defaultValue: "set"),
                    set_by = table.Column<string>(type: "text", nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_classification", x => x.id);
                    table.CheckConstraint("ck_classification_level", "level > 0");
                    table.CheckConstraint("ck_classification_object_type", "object_type IN ('site', 'equipment', 'cable', 'service')");
                    table.CheckConstraint("ck_classification_source", "source IN ('set', 'imported')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation",
                sql: "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename', 'set_attributes', 'create_site', 'create_equipment', 'create_cable', 'split_cable', 'remove', 'set_classification')");

            migrationBuilder.CreateIndex(
                name: "ix_classification_object_type_object_id",
                table: "classification",
                columns: new[] { "object_type", "object_id" });

            migrationBuilder.CreateIndex(
                name: "ix_classification_schema_key_level",
                table: "classification",
                columns: new[] { "schema_key", "level" });

            migrationBuilder.CreateIndex(
                name: "ix_classification_schema_key_object_type_object_id",
                table: "classification",
                columns: new[] { "schema_key", "object_type", "object_id" },
                unique: true);

            // The services' criticality attribute becomes a classification (#176): critical is level 5 of the criticality schema.
            migrationBuilder.Sql("""
                INSERT INTO classification (object_type, object_id, schema_key, level, source, set_by)
                SELECT 'service', id, 'criticality', 5, 'imported', 'migration' FROM service WHERE attributes->>'criticality' = 'critical';
                UPDATE service SET attributes = attributes - 'criticality' WHERE attributes ? 'criticality';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE service s SET attributes = attributes || '{"criticality": "critical"}'::jsonb
                FROM classification c WHERE c.object_type = 'service' AND c.object_id = s.id AND c.schema_key = 'criticality' AND c.level >= 5;
                """);

            migrationBuilder.DropTable(
                name: "classification");

            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation",
                sql: "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename', 'set_attributes', 'create_site', 'create_equipment', 'create_cable', 'split_cable', 'remove')");
        }
    }
}
