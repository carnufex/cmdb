using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class ImportProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_service_source_system_external_id",
                table: "service",
                columns: new[] { "source_system", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_location_source_system_external_id",
                table: "location",
                columns: new[] { "source_system", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_source_system_external_id",
                table: "equipment",
                columns: new[] { "source_system", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_circuit_source_system_external_id",
                table: "circuit",
                columns: new[] { "source_system", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cable_source_system_external_id",
                table: "cable",
                columns: new[] { "source_system", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_service_source_system_external_id",
                table: "service");

            migrationBuilder.DropIndex(
                name: "ix_location_source_system_external_id",
                table: "location");

            migrationBuilder.DropIndex(
                name: "ix_equipment_source_system_external_id",
                table: "equipment");

            migrationBuilder.DropIndex(
                name: "ix_circuit_source_system_external_id",
                table: "circuit");

            migrationBuilder.DropIndex(
                name: "ix_cable_source_system_external_id",
                table: "cable");
        }
    }
}
