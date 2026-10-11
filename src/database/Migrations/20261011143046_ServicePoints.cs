using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class ServicePoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation");

            migrationBuilder.CreateTable(
                name: "service_point",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    address = table.Column<string>(type: "text", nullable: true),
                    type_key = table.Column<string>(type: "text", nullable: false),
                    units = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "planned"),
                    geom = table.Column<Point>(type: "geometry(Point, 3006)", nullable: false),
                    source_system = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    site_id = table.Column<long>(type: "bigint", nullable: true),
                    terminal_id = table.Column<long>(type: "bigint", nullable: true),
                    attributes = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_point", x => x.id);
                    table.CheckConstraint("ck_service_point_status", "status IN ('planned', 'ordered', 'connected')");
                    table.CheckConstraint("ck_service_point_units", "units >= 1");
                    table.ForeignKey(
                        name: "fk_service_point_port_terminal_id",
                        column: x => x.terminal_id,
                        principalTable: "port",
                        principalColumn: "terminal_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_service_point_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation",
                sql: "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename', 'set_attributes', 'create_site', 'create_equipment', 'create_cable', 'split_cable', 'remove', 'set_classification', 'move', 'set_conductor_usage', 'create_location', 'create_service', 'create_circuit', 'set_circuit_path', 'link_circuit', 'link_service', 'set_rack_place', 'create_service_point', 'update_service_point')");

            migrationBuilder.CreateIndex(
                name: "ix_service_point_address_trgm",
                table: "service_point",
                column: "address")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_service_point_code",
                table: "service_point",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_point_code_trgm",
                table: "service_point",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_service_point_geom",
                table: "service_point",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_service_point_site_id",
                table: "service_point",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_point_source_system_external_id",
                table: "service_point",
                columns: new[] { "source_system", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_service_point_terminal_id",
                table: "service_point",
                column: "terminal_id");

            // Writes get an operation like the network's (#23): from the command layer, or a fallback from this trigger.
            migrationBuilder.Sql("CREATE TRIGGER service_point_operation AFTER INSERT OR UPDATE OR DELETE ON service_point FOR EACH STATEMENT EXECUTE FUNCTION cmdb_operation_statement();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_point");

            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation",
                sql: "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename', 'set_attributes', 'create_site', 'create_equipment', 'create_cable', 'split_cable', 'remove', 'set_classification', 'move', 'set_conductor_usage', 'create_location', 'create_service', 'create_circuit', 'set_circuit_path', 'link_circuit', 'link_service', 'set_rack_place')");
        }
    }
}
