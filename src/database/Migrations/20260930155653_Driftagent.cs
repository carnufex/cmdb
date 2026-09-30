using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Driftagent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incident",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    site_id = table.Column<long>(type: "bigint", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    observations = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    priority = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "open"),
                    conversation_id = table.Column<string>(type: "text", nullable: false),
                    reported_by = table.Column<string>(type: "text", nullable: false),
                    enrichment = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident", x => x.id);
                    table.CheckConstraint("ck_incident_priority", "priority IN ('P1', 'P2', 'P3')");
                    table.CheckConstraint("ck_incident_status", "status IN ('open', 'closed')");
                    table.ForeignKey(
                        name: "fk_incident_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "site",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "voice_caller",
                columns: table => new
                {
                    employee_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    phone = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    groups = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_caller", x => x.employee_id);
                    table.CheckConstraint("ck_voice_caller_role", "role IN ('technician', 'contractor', 'noc')");
                });

            migrationBuilder.CreateTable(
                name: "voice_challenge",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversation_id = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<string>(type: "text", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_challenge", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "voice_session",
                columns: table => new
                {
                    conversation_id = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<string>(type: "text", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_session", x => x.conversation_id);
                });

            migrationBuilder.CreateTable(
                name: "voice_sms",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    to_phone = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_sms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "voice_tool_call",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversation_id = table.Column<string>(type: "text", nullable: false),
                    employee_id = table.Column<string>(type: "text", nullable: true),
                    tool = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    milliseconds = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_tool_call", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_incident_created_at",
                table: "incident",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_incident_site_id",
                table: "incident",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_voice_challenge_conversation_id_employee_id",
                table: "voice_challenge",
                columns: new[] { "conversation_id", "employee_id" });

            migrationBuilder.CreateIndex(
                name: "ix_voice_sms_created_at",
                table: "voice_sms",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_voice_tool_call_conversation_id",
                table: "voice_tool_call",
                column: "conversation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident");

            migrationBuilder.DropTable(
                name: "voice_caller");

            migrationBuilder.DropTable(
                name: "voice_challenge");

            migrationBuilder.DropTable(
                name: "voice_session");

            migrationBuilder.DropTable(
                name: "voice_sms");

            migrationBuilder.DropTable(
                name: "voice_tool_call");
        }
    }
}
