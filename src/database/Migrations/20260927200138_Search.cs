using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:cable_medium", "fiber,copper,coax,power")
                .Annotation("Npgsql:Enum:channel_kind", "wavelength,timeslot,vlan")
                .Annotation("Npgsql:Enum:circuit_layer", "physical,transmission,logical")
                .Annotation("Npgsql:Enum:connection_kind", "patch,splice,termination,internal")
                .Annotation("Npgsql:Enum:lifecycle_state", "planned,under_construction,in_service,decommissioning,removed")
                .Annotation("Npgsql:Enum:terminal_kind", "port,conductor_end")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:Enum:cable_medium", "fiber,copper,coax,power")
                .OldAnnotation("Npgsql:Enum:channel_kind", "wavelength,timeslot,vlan")
                .OldAnnotation("Npgsql:Enum:circuit_layer", "physical,transmission,logical")
                .OldAnnotation("Npgsql:Enum:connection_kind", "patch,splice,termination,internal")
                .OldAnnotation("Npgsql:Enum:lifecycle_state", "planned,under_construction,in_service,decommissioning,removed")
                .OldAnnotation("Npgsql:Enum:terminal_kind", "port,conductor_end")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateIndex(
                name: "ix_site_code_pattern",
                table: "site",
                column: "code")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_site_code_trgm",
                table: "site",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_site_name_trgm",
                table: "site",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_service_code_pattern",
                table: "service",
                column: "code")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_service_code_trgm",
                table: "service",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_service_name_trgm",
                table: "service",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_equipment_name_trgm",
                table: "equipment",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_circuit_code_pattern",
                table: "circuit",
                column: "code")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_circuit_code_trgm",
                table: "circuit",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_cable_code_pattern",
                table: "cable",
                column: "code")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_cable_code_trgm",
                table: "cable",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            // Indexes EF cannot express (expression indexes) live here as SQL (ADR-0009):
            // attribute search (serial numbers, IP addresses) and case-insensitive name prefix and equality.
            migrationBuilder.Sql("CREATE INDEX ix_equipment_attributes_trgm ON equipment USING gin ((attributes::text) gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_equipment_name_lower ON equipment (lower(name) text_pattern_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_site_name_lower ON site (lower(name) text_pattern_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_service_name_lower ON service (lower(name) text_pattern_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_equipment_attributes_trgm, ix_equipment_name_lower, ix_site_name_lower, ix_service_name_lower;");

            migrationBuilder.DropIndex(
                name: "ix_site_code_pattern",
                table: "site");

            migrationBuilder.DropIndex(
                name: "ix_site_code_trgm",
                table: "site");

            migrationBuilder.DropIndex(
                name: "ix_site_name_trgm",
                table: "site");

            migrationBuilder.DropIndex(
                name: "ix_service_code_pattern",
                table: "service");

            migrationBuilder.DropIndex(
                name: "ix_service_code_trgm",
                table: "service");

            migrationBuilder.DropIndex(
                name: "ix_service_name_trgm",
                table: "service");

            migrationBuilder.DropIndex(
                name: "ix_equipment_name_trgm",
                table: "equipment");

            migrationBuilder.DropIndex(
                name: "ix_circuit_code_pattern",
                table: "circuit");

            migrationBuilder.DropIndex(
                name: "ix_circuit_code_trgm",
                table: "circuit");

            migrationBuilder.DropIndex(
                name: "ix_cable_code_pattern",
                table: "cable");

            migrationBuilder.DropIndex(
                name: "ix_cable_code_trgm",
                table: "cable");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:cable_medium", "fiber,copper,coax,power")
                .Annotation("Npgsql:Enum:channel_kind", "wavelength,timeslot,vlan")
                .Annotation("Npgsql:Enum:circuit_layer", "physical,transmission,logical")
                .Annotation("Npgsql:Enum:connection_kind", "patch,splice,termination,internal")
                .Annotation("Npgsql:Enum:lifecycle_state", "planned,under_construction,in_service,decommissioning,removed")
                .Annotation("Npgsql:Enum:terminal_kind", "port,conductor_end")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:Enum:cable_medium", "fiber,copper,coax,power")
                .OldAnnotation("Npgsql:Enum:channel_kind", "wavelength,timeslot,vlan")
                .OldAnnotation("Npgsql:Enum:circuit_layer", "physical,transmission,logical")
                .OldAnnotation("Npgsql:Enum:connection_kind", "patch,splice,termination,internal")
                .OldAnnotation("Npgsql:Enum:lifecycle_state", "planned,under_construction,in_service,decommissioning,removed")
                .OldAnnotation("Npgsql:Enum:terminal_kind", "port,conductor_end")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
