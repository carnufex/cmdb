using System;
using Cmdb.Database.Model;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:cable_medium", "fiber,copper,coax,power")
                .Annotation("Npgsql:Enum:connection_kind", "patch,splice,termination,internal")
                .Annotation("Npgsql:Enum:lifecycle_state", "planned,under_construction,in_service,decommissioning,removed")
                .Annotation("Npgsql:Enum:terminal_kind", "port,conductor_end")
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "cable_type",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    medium = table.Column<CableMedium>(type: "cable_medium", nullable: false),
                    conductor_count = table.Column<int>(type: "integer", nullable: false),
                    color_code = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cable_type", x => x.id);
                    table.CheckConstraint("ck_cable_type_conductor_count", "conductor_count > 0");
                });

            migrationBuilder.CreateTable(
                name: "equipment_type",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    manufacturer = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    rack_units = table.Column<short>(type: "smallint", nullable: true),
                    panel = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{\"rows\": 1, \"columns\": 1}'"),
                    attribute_schema = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    port_template = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    slot_template = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_equipment_type", x => x.id);
                    table.CheckConstraint("ck_equipment_type_port_template", "jsonb_typeof(port_template) = 'array'");
                    table.CheckConstraint("ck_equipment_type_rack_units", "rack_units > 0");
                    table.CheckConstraint("ck_equipment_type_slot_template", "jsonb_typeof(slot_template) = 'array'");
                });

            migrationBuilder.CreateTable(
                name: "site",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    site_type = table.Column<string>(type: "text", nullable: false),
                    geom = table.Column<Geometry>(type: "geometry(Geometry, 3006)", nullable: false),
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
                    table.PrimaryKey("pk_site", x => x.id);
                    table.CheckConstraint("ck_site_geom_type", "GeometryType(geom) IN ('POINT', 'POLYGON', 'MULTIPOLYGON')");
                    table.CheckConstraint("ck_site_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                });

            migrationBuilder.CreateTable(
                name: "terminal",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    kind = table.Column<TerminalKind>(type: "terminal_kind", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_terminal", x => x.id);
                    table.UniqueConstraint("ak_terminals_id_kind", x => new { x.id, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "cable",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    cable_type_id = table.Column<long>(type: "bigint", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    a_site_id = table.Column<long>(type: "bigint", nullable: false),
                    b_site_id = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("pk_cable", x => x.id);
                    table.CheckConstraint("ck_cable_distinct_ends", "a_site_id <> b_site_id");
                    table.CheckConstraint("ck_cable_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_cable_cable_types_cable_type_id",
                        column: x => x.cable_type_id,
                        principalTable: "cable_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cable_sites_a_site_id",
                        column: x => x.a_site_id,
                        principalTable: "site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cable_sites_b_site_id",
                        column: x => x.b_site_id,
                        principalTable: "site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "location",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    site_id = table.Column<long>(type: "bigint", nullable: false),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    rack_units = table.Column<short>(type: "smallint", nullable: true),
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
                    table.PrimaryKey("pk_location", x => x.id);
                    table.CheckConstraint("ck_location_kind", "kind IN ('building', 'room', 'rack', 'position')");
                    table.CheckConstraint("ck_location_not_own_parent", "parent_id IS DISTINCT FROM id");
                    table.CheckConstraint("ck_location_rack_units", "rack_units > 0");
                    table.CheckConstraint("ck_location_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_location_location_parent_id",
                        column: x => x.parent_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_location_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "connection",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    a_terminal_id = table.Column<long>(type: "bigint", nullable: false),
                    b_terminal_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<ConnectionKind>(type: "connection_kind", nullable: false),
                    lifecycle = table.Column<LifecycleState>(type: "lifecycle_state", nullable: false, defaultValue: LifecycleState.Planned),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_system = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    last_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection", x => x.id);
                    table.CheckConstraint("ck_connection_ordered", "a_terminal_id < b_terminal_id");
                    table.CheckConstraint("ck_connection_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_connection_terminals_a_terminal_id",
                        column: x => x.a_terminal_id,
                        principalTable: "terminal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_connection_terminals_b_terminal_id",
                        column: x => x.b_terminal_id,
                        principalTable: "terminal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conductor",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    cable_id = table.Column<long>(type: "bigint", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    color = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conductor", x => x.id);
                    table.CheckConstraint("ck_conductor_number", "number > 0");
                    table.ForeignKey(
                        name: "fk_conductor_cable_cable_id",
                        column: x => x.cable_id,
                        principalTable: "cable",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "equipment",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    equipment_type_id = table.Column<long>(type: "bigint", nullable: false),
                    site_id = table.Column<long>(type: "bigint", nullable: false),
                    location_id = table.Column<long>(type: "bigint", nullable: true),
                    parent_id = table.Column<long>(type: "bigint", nullable: true),
                    slot = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_equipment", x => x.id);
                    table.CheckConstraint("ck_equipment_not_own_parent", "parent_id IS DISTINCT FROM id");
                    table.CheckConstraint("ck_equipment_placement", "(location_id IS NOT NULL) <> (parent_id IS NOT NULL)");
                    table.CheckConstraint("ck_equipment_slot", "(parent_id IS NULL) = (slot IS NULL)");
                    table.CheckConstraint("ck_equipment_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_equipment_equipment_parent_id",
                        column: x => x.parent_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_equipment_equipment_types_equipment_type_id",
                        column: x => x.equipment_type_id,
                        principalTable: "equipment_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_equipment_locations_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_equipment_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conductor_end",
                columns: table => new
                {
                    terminal_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<TerminalKind>(type: "terminal_kind", nullable: false, defaultValue: TerminalKind.ConductorEnd),
                    conductor_id = table.Column<long>(type: "bigint", nullable: false),
                    side = table.Column<char>(type: "char(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conductor_end", x => x.terminal_id);
                    table.CheckConstraint("ck_conductor_end_kind", "kind = 'conductor_end'");
                    table.CheckConstraint("ck_conductor_end_side", "side IN ('A', 'B')");
                    table.ForeignKey(
                        name: "fk_conductor_end_conductor_conductor_id",
                        column: x => x.conductor_id,
                        principalTable: "conductor",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conductor_end_terminals_terminal_id_kind",
                        columns: x => new { x.terminal_id, x.kind },
                        principalTable: "terminal",
                        principalColumns: new[] { "id", "kind" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "port",
                columns: table => new
                {
                    terminal_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<TerminalKind>(type: "terminal_kind", nullable: false, defaultValue: TerminalKind.Port),
                    equipment_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    port_type = table.Column<string>(type: "text", nullable: false),
                    port_group = table.Column<string>(type: "text", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_port", x => x.terminal_id);
                    table.CheckConstraint("ck_port_kind", "kind = 'port'");
                    table.ForeignKey(
                        name: "fk_port_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_port_terminals_terminal_id_kind",
                        columns: x => new { x.terminal_id, x.kind },
                        principalTable: "terminal",
                        principalColumns: new[] { "id", "kind" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cable_a_site_id",
                table: "cable",
                column: "a_site_id");

            migrationBuilder.CreateIndex(
                name: "ix_cable_b_site_id",
                table: "cable",
                column: "b_site_id");

            migrationBuilder.CreateIndex(
                name: "ix_cable_cable_type_id",
                table: "cable",
                column: "cable_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_cable_code",
                table: "cable",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cable_geom",
                table: "cable",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_cable_type_key",
                table: "cable_type",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conductor_cable_id_number",
                table: "conductor",
                columns: new[] { "cable_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conductor_end_conductor_id_side",
                table: "conductor_end",
                columns: new[] { "conductor_id", "side" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_conductor_end_terminal_id_kind",
                table: "conductor_end",
                columns: new[] { "terminal_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connection_b_terminal_id",
                table: "connection",
                column: "b_terminal_id");

            migrationBuilder.CreateIndex(
                name: "ix_connection_current_pair",
                table: "connection",
                columns: new[] { "a_terminal_id", "b_terminal_id" },
                unique: true,
                filter: "valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_equipment_type_id",
                table: "equipment",
                column: "equipment_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_location_id",
                table: "equipment",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_parent_id_slot",
                table: "equipment",
                columns: new[] { "parent_id", "slot" },
                unique: true,
                filter: "parent_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_site_id",
                table: "equipment",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_type_key",
                table: "equipment_type",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_equipment_type_manufacturer_model",
                table: "equipment_type",
                columns: new[] { "manufacturer", "model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_location_parent_id",
                table: "location",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_location_site_id_parent_id_name",
                table: "location",
                columns: new[] { "site_id", "parent_id", "name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_port_equipment_id_name",
                table: "port",
                columns: new[] { "equipment_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_port_terminal_id_kind",
                table: "port",
                columns: new[] { "terminal_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_code",
                table: "site",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_geom",
                table: "site",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_site_source_system_external_id",
                table: "site",
                columns: new[] { "source_system", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conductor_end");

            migrationBuilder.DropTable(
                name: "connection");

            migrationBuilder.DropTable(
                name: "port");

            migrationBuilder.DropTable(
                name: "conductor");

            migrationBuilder.DropTable(
                name: "equipment");

            migrationBuilder.DropTable(
                name: "terminal");

            migrationBuilder.DropTable(
                name: "cable");

            migrationBuilder.DropTable(
                name: "equipment_type");

            migrationBuilder.DropTable(
                name: "location");

            migrationBuilder.DropTable(
                name: "cable_type");

            migrationBuilder.DropTable(
                name: "site");
        }
    }
}
