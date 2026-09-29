using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class AccessScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_scope",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    area = table.Column<Geometry>(type: "geometry(Geometry, 3006)", nullable: true),
                    site_types = table.Column<string[]>(type: "text[]", nullable: false),
                    hidden_attributes = table.Column<string[]>(type: "text[]", nullable: false),
                    plans = table.Column<string[]>(type: "text[]", nullable: false),
                    crossing_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "whole"),
                    groups = table.Column<string[]>(type: "text[]", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    granted_by = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_scope", x => x.key);
                    table.CheckConstraint("ck_access_scope_area", "area IS NULL OR GeometryType(area) IN ('POLYGON', 'MULTIPOLYGON')");
                    table.CheckConstraint("ck_access_scope_crossing", "crossing_mode IN ('whole', 'clip')");
                    table.CheckConstraint("ck_access_scope_two_persons", "approved_by <> granted_by");
                });

            migrationBuilder.CreateTable(
                name: "scope_cable",
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    cable_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_cable", x => new { x.scope_key, x.cable_id });
                    table.ForeignKey(
                        name: "fk_scope_cable_access_scope_scope_key",
                        column: x => x.scope_key,
                        principalTable: "access_scope",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scope_circuit",
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    circuit_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_circuit", x => new { x.scope_key, x.circuit_id });
                    table.ForeignKey(
                        name: "fk_scope_circuit_access_scope_scope_key",
                        column: x => x.scope_key,
                        principalTable: "access_scope",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scope_service",
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    service_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_service", x => new { x.scope_key, x.service_id });
                    table.ForeignKey(
                        name: "fk_scope_service_access_scope_scope_key",
                        column: x => x.scope_key,
                        principalTable: "access_scope",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scope_site",
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    site_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_site", x => new { x.scope_key, x.site_id });
                    table.ForeignKey(
                        name: "fk_scope_site_access_scope_scope_key",
                        column: x => x.scope_key,
                        principalTable: "access_scope",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scope_cable_cable_id",
                table: "scope_cable",
                column: "cable_id");

            migrationBuilder.CreateIndex(
                name: "ix_scope_circuit_circuit_id",
                table: "scope_circuit",
                column: "circuit_id");

            migrationBuilder.CreateIndex(
                name: "ix_scope_service_service_id",
                table: "scope_service",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_scope_site_site_id",
                table: "scope_site",
                column: "site_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scope_cable");

            migrationBuilder.DropTable(
                name: "scope_circuit");

            migrationBuilder.DropTable(
                name: "scope_service");

            migrationBuilder.DropTable(
                name: "scope_site");

            migrationBuilder.DropTable(
                name: "access_scope");
        }
    }
}
