using System;
using Cmdb.Database.Model;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Conduit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_reservation_resource",
                table: "reservation");

            migrationBuilder.AddColumn<string>(
                name: "usage",
                table: "conductor",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "duct_type",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    manufacturer = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    outer_diameter_mm = table.Column<int>(type: "integer", nullable: false),
                    subduct_count = table.Column<int>(type: "integer", nullable: false),
                    subduct_inner_diameter_mm = table.Column<int>(type: "integer", nullable: false),
                    color_code = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_duct_type", x => x.id);
                    table.CheckConstraint("ck_duct_type_subducts", "subduct_count > 0 AND subduct_inner_diameter_mm > 0 AND outer_diameter_mm > 0");
                });

            migrationBuilder.CreateTable(
                name: "route_segment",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    a_site_id = table.Column<long>(type: "bigint", nullable: false),
                    b_site_id = table.Column<long>(type: "bigint", nullable: false),
                    construction = table.Column<string>(type: "text", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: true),
                    geom = table.Column<LineString>(type: "geometry(LineString, 3006)", nullable: false),
                    length_m = table.Column<double>(type: "double precision", nullable: false, computedColumnSql: "ST_Length(geom)", stored: true),
                    attributes = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    lifecycle = table.Column<LifecycleState>(type: "lifecycle_state", nullable: false, defaultValue: LifecycleState.Planned),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_system = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    last_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_route_segment", x => x.id);
                    table.CheckConstraint("ck_route_segment_construction", "construction IN ('trench', 'plough', 'aerial', 'existing')");
                    table.CheckConstraint("ck_route_segment_distinct_ends", "a_site_id <> b_site_id");
                    table.CheckConstraint("ck_route_segment_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_route_segment_sites_a_site_id",
                        column: x => x.a_site_id,
                        principalTable: "site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_route_segment_sites_b_site_id",
                        column: x => x.b_site_id,
                        principalTable: "site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cable_path",
                columns: table => new
                {
                    cable_id = table.Column<long>(type: "bigint", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    subduct_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cable_path", x => new { x.cable_id, x.seq });
                    table.CheckConstraint("ck_cable_path_seq", "seq >= 0");
                    table.ForeignKey(
                        name: "fk_cable_path_cable_cable_id",
                        column: x => x.cable_id,
                        principalTable: "cable",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "duct",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    duct_type_id = table.Column<long>(type: "bigint", nullable: false),
                    parent_subduct_id = table.Column<long>(type: "bigint", nullable: true),
                    attributes = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    lifecycle = table.Column<LifecycleState>(type: "lifecycle_state", nullable: false, defaultValue: LifecycleState.Planned),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_system = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    last_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_duct", x => x.id);
                    table.CheckConstraint("ck_duct_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_duct_duct_types_duct_type_id",
                        column: x => x.duct_type_id,
                        principalTable: "duct_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "duct_segment",
                columns: table => new
                {
                    duct_id = table.Column<long>(type: "bigint", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    route_segment_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_duct_segment", x => new { x.duct_id, x.seq });
                    table.CheckConstraint("ck_duct_segment_seq", "seq >= 0");
                    table.ForeignKey(
                        name: "fk_duct_segment_duct_duct_id",
                        column: x => x.duct_id,
                        principalTable: "duct",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_duct_segment_route_segments_route_segment_id",
                        column: x => x.route_segment_id,
                        principalTable: "route_segment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subduct",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    duct_id = table.Column<long>(type: "bigint", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    color = table.Column<string>(type: "text", nullable: true),
                    occupancy = table.Column<string>(type: "text", nullable: false, defaultValue: "empty")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subduct", x => x.id);
                    table.CheckConstraint("ck_subduct_number", "number > 0");
                    table.CheckConstraint("ck_subduct_occupancy", "occupancy IN ('empty', 'cable', 'blown_fibre')");
                    table.ForeignKey(
                        name: "fk_subduct_duct_duct_id",
                        column: x => x.duct_id,
                        principalTable: "duct",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservation_resource",
                table: "reservation",
                sql: "resource_kind IN ('terminal', 'conductor', 'slot', 'channel', 'subduct')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conductor_usage",
                table: "conductor",
                sql: "usage IS NULL OR usage IN ('dark', 'dark_fibre', 'spare')");

            migrationBuilder.CreateIndex(
                name: "ix_cable_path_subduct_id",
                table: "cable_path",
                column: "subduct_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_duct_code",
                table: "duct",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_duct_duct_type_id",
                table: "duct",
                column: "duct_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_duct_parent_subduct_id",
                table: "duct",
                column: "parent_subduct_id",
                filter: "parent_subduct_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_duct_segment_duct_id_route_segment_id",
                table: "duct_segment",
                columns: new[] { "duct_id", "route_segment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_duct_segment_route_segment_id",
                table: "duct_segment",
                column: "route_segment_id");

            migrationBuilder.CreateIndex(
                name: "ix_duct_type_key",
                table: "duct_type",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_route_segment_a_site_id",
                table: "route_segment",
                column: "a_site_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_segment_b_site_id",
                table: "route_segment",
                column: "b_site_id");

            migrationBuilder.CreateIndex(
                name: "ix_route_segment_code",
                table: "route_segment",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_route_segment_geom",
                table: "route_segment",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_subduct_duct_id_number",
                table: "subduct",
                columns: new[] { "duct_id", "number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_cable_path_subducts_subduct_id",
                table: "cable_path",
                column: "subduct_id",
                principalTable: "subduct",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_duct_subducts_parent_subduct_id",
                table: "duct",
                column: "parent_subduct_id",
                principalTable: "subduct",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_duct_subducts_parent_subduct_id",
                table: "duct");

            migrationBuilder.DropTable(
                name: "cable_path");

            migrationBuilder.DropTable(
                name: "duct_segment");

            migrationBuilder.DropTable(
                name: "route_segment");

            migrationBuilder.DropTable(
                name: "subduct");

            migrationBuilder.DropTable(
                name: "duct");

            migrationBuilder.DropTable(
                name: "duct_type");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservation_resource",
                table: "reservation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conductor_usage",
                table: "conductor");

            migrationBuilder.DropColumn(
                name: "usage",
                table: "conductor");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservation_resource",
                table: "reservation",
                sql: "resource_kind IN ('terminal', 'conductor', 'slot', 'channel')");
        }
    }
}
