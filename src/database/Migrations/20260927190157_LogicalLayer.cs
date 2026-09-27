using System;
using Cmdb.Database.Model;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class LogicalLayer : Migration
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
                .Annotation("Npgsql:PostgresExtension:postgis", ",,")
                .OldAnnotation("Npgsql:Enum:cable_medium", "fiber,copper,coax,power")
                .OldAnnotation("Npgsql:Enum:connection_kind", "patch,splice,termination,internal")
                .OldAnnotation("Npgsql:Enum:lifecycle_state", "planned,under_construction,in_service,decommissioning,removed")
                .OldAnnotation("Npgsql:Enum:terminal_kind", "port,conductor_end")
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "channel",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    terminal_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<ChannelKind>(type: "channel_kind", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel", x => x.id);
                    table.CheckConstraint("ck_channel_number", "number >= 0");
                    table.ForeignKey(
                        name: "fk_channel_terminals_terminal_id",
                        column: x => x.terminal_id,
                        principalTable: "terminal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "circuit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    layer = table.Column<CircuitLayer>(type: "circuit_layer", nullable: false),
                    a_terminal_id = table.Column<long>(type: "bigint", nullable: false),
                    b_terminal_id = table.Column<long>(type: "bigint", nullable: false),
                    lifecycle = table.Column<LifecycleState>(type: "lifecycle_state", nullable: false, defaultValue: LifecycleState.Planned),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_system = table.Column<string>(type: "text", nullable: true),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    last_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_circuit", x => x.id);
                    table.CheckConstraint("ck_circuit_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                    table.ForeignKey(
                        name: "fk_circuit_terminals_a_terminal_id",
                        column: x => x.a_terminal_id,
                        principalTable: "terminal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_circuit_terminals_b_terminal_id",
                        column: x => x.b_terminal_id,
                        principalTable: "terminal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    service_type = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_service", x => x.id);
                    table.CheckConstraint("ck_service_valid_time", "valid_to IS NULL OR valid_to > valid_from");
                });

            migrationBuilder.CreateTable(
                name: "circuit_dependency",
                columns: table => new
                {
                    circuit_id = table.Column<long>(type: "bigint", nullable: false),
                    carrier_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_circuit_dependency", x => new { x.circuit_id, x.carrier_id });
                    table.CheckConstraint("ck_circuit_dependency_not_self", "circuit_id <> carrier_id");
                    table.ForeignKey(
                        name: "fk_circuit_dependency_circuit_carrier_id",
                        column: x => x.carrier_id,
                        principalTable: "circuit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_circuit_dependency_circuit_circuit_id",
                        column: x => x.circuit_id,
                        principalTable: "circuit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "circuit_hop",
                columns: table => new
                {
                    circuit_id = table.Column<long>(type: "bigint", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    terminal_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_circuit_hop", x => new { x.circuit_id, x.seq });
                    table.CheckConstraint("ck_circuit_hop_seq", "seq >= 0");
                    table.ForeignKey(
                        name: "fk_circuit_hop_channel_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channel",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_circuit_hop_circuit_circuit_id",
                        column: x => x.circuit_id,
                        principalTable: "circuit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_circuit_hop_terminals_terminal_id",
                        column: x => x.terminal_id,
                        principalTable: "terminal",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_circuit",
                columns: table => new
                {
                    service_id = table.Column<long>(type: "bigint", nullable: false),
                    circuit_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_circuit", x => new { x.service_id, x.circuit_id });
                    table.ForeignKey(
                        name: "fk_service_circuit_circuit_circuit_id",
                        column: x => x.circuit_id,
                        principalTable: "circuit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_circuit_services_service_id",
                        column: x => x.service_id,
                        principalTable: "service",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_terminal_id_kind_number",
                table: "channel",
                columns: new[] { "terminal_id", "kind", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_circuit_a_terminal_id",
                table: "circuit",
                column: "a_terminal_id");

            migrationBuilder.CreateIndex(
                name: "ix_circuit_b_terminal_id",
                table: "circuit",
                column: "b_terminal_id");

            migrationBuilder.CreateIndex(
                name: "ix_circuit_code",
                table: "circuit",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_circuit_dependency_carrier_id",
                table: "circuit_dependency",
                column: "carrier_id");

            migrationBuilder.CreateIndex(
                name: "ix_circuit_hop_channel_id",
                table: "circuit_hop",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_circuit_hop_terminal_id",
                table: "circuit_hop",
                column: "terminal_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_code",
                table: "service",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_circuit_circuit_id",
                table: "service_circuit",
                column: "circuit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "circuit_dependency");

            migrationBuilder.DropTable(
                name: "circuit_hop");

            migrationBuilder.DropTable(
                name: "service_circuit");

            migrationBuilder.DropTable(
                name: "channel");

            migrationBuilder.DropTable(
                name: "circuit");

            migrationBuilder.DropTable(
                name: "service");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:cable_medium", "fiber,copper,coax,power")
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
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
