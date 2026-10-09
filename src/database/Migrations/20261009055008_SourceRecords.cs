using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class SourceRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_record",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<long>(type: "bigint", nullable: false),
                    source_system = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    reported = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_record", x => x.id);
                    table.CheckConstraint("ck_source_record_object_type", "object_type IN ('site', 'location', 'equipment', 'cable', 'circuit', 'service')");
                    table.CheckConstraint("ck_source_record_reported", "jsonb_typeof(reported) = 'object'");
                });

            migrationBuilder.CreateIndex(
                name: "ix_source_record_object_type_object_id_source_system",
                table: "source_record",
                columns: new[] { "object_type", "object_id", "source_system" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_source_record_object_type_source_system_external_id",
                table: "source_record",
                columns: new[] { "object_type", "source_system", "external_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_record");
        }
    }
}
