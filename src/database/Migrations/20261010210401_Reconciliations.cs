using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Reconciliations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "missing_since",
                table: "source_record",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reconciliation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    source_system = table.Column<string>(type: "text", nullable: false),
                    run_by = table.Column<string>(type: "text", nullable: false),
                    dry_run = table.Column<bool>(type: "boolean", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    elapsed_ms = table.Column<double>(type: "double precision", nullable: false),
                    review_plan_id = table.Column<long>(type: "bigint", nullable: true),
                    applied_plan_id = table.Column<long>(type: "bigint", nullable: true),
                    report = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation", x => x.id);
                    table.CheckConstraint("ck_reconciliation_report", "jsonb_typeof(report) = 'object'");
                    table.ForeignKey(
                        name: "fk_reconciliation_plan_applied_plan_id",
                        column: x => x.applied_plan_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_reconciliation_plan_review_plan_id",
                        column: x => x.review_plan_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_applied_plan_id",
                table: "reconciliation",
                column: "applied_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_review_plan_id",
                table: "reconciliation",
                column: "review_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_source_system_started_at",
                table: "reconciliation",
                columns: new[] { "source_system", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reconciliation");

            migrationBuilder.DropColumn(
                name: "missing_since",
                table: "source_record");
        }
    }
}
