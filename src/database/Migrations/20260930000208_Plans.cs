using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Plans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plan",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "draft"),
                    flag = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    applied_by = table.Column<string>(type: "text", nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan", x => x.id);
                    table.CheckConstraint("ck_plan_status", "status IN ('draft', 'applied', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "plan_dependency",
                columns: table => new
                {
                    plan_id = table.Column<long>(type: "bigint", nullable: false),
                    depends_on_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_dependency", x => new { x.plan_id, x.depends_on_id });
                    table.CheckConstraint("ck_plan_dependency_self", "plan_id <> depends_on_id");
                    table.ForeignKey(
                        name: "fk_plan_dependency_plan_depends_on_id",
                        column: x => x.depends_on_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_plan_dependency_plan_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plan_operation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    plan_id = table.Column<long>(type: "bigint", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_operation", x => x.id);
                    table.CheckConstraint("ck_plan_operation_kind", "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename')");
                    table.CheckConstraint("ck_plan_operation_payload", "jsonb_typeof(payload) = 'object'");
                    table.ForeignKey(
                        name: "fk_plan_operation_plan_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plan_status",
                table: "plan",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_plan_dependency_depends_on_id",
                table: "plan_dependency",
                column: "depends_on_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_operation_plan_id_seq",
                table: "plan_operation",
                columns: new[] { "plan_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plan_dependency");

            migrationBuilder.DropTable(
                name: "plan_operation");

            migrationBuilder.DropTable(
                name: "plan");
        }
    }
}
