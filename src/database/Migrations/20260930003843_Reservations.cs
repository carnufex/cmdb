using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Reservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reservation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    resource_kind = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<long>(type: "bigint", nullable: false),
                    slot = table.Column<string>(type: "text", nullable: true),
                    holder_kind = table.Column<string>(type: "text", nullable: false),
                    holder_id = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservation", x => x.id);
                    table.CheckConstraint("ck_reservation_holder", "holder_kind IN ('plan', 'service')");
                    table.CheckConstraint("ck_reservation_resource", "resource_kind IN ('terminal', 'conductor', 'slot', 'channel')");
                    table.CheckConstraint("ck_reservation_slot", "(resource_kind = 'slot') = (slot IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservation_active",
                table: "reservation",
                columns: new[] { "resource_kind", "resource_id", "slot" },
                unique: true,
                filter: "released_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_reservation_holder_kind_holder_id",
                table: "reservation",
                columns: new[] { "holder_kind", "holder_id" },
                filter: "released_at IS NULL");

            // Conflicts (#25): which operations connect a terminal. Expression indexes are not in the EF model.
            migrationBuilder.Sql("""
                CREATE INDEX ix_plan_operation_a ON plan_operation (((payload->>'a')::bigint)) WHERE kind = 'connect';
                CREATE INDEX ix_plan_operation_b ON plan_operation (((payload->>'b')::bigint)) WHERE kind = 'connect';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_plan_operation_a; DROP INDEX ix_plan_operation_b;");
            migrationBuilder.DropTable(
                name: "reservation");
        }
    }
}
