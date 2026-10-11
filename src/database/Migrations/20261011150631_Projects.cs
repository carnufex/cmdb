using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Projects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    area = table.Column<Geometry>(type: "geometry(Geometry, 3006)", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project", x => x.id);
                    table.CheckConstraint("ck_project_area", "GeometryType(area) IN ('POLYGON', 'MULTIPOLYGON')");
                });

            migrationBuilder.CreateTable(
                name: "project_plan",
                columns: table => new
                {
                    project_id = table.Column<long>(type: "bigint", nullable: false),
                    plan_id = table.Column<long>(type: "bigint", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_plan", x => new { x.project_id, x.plan_id });
                    table.ForeignKey(
                        name: "fk_project_plan_plan_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_plan_project_project_id",
                        column: x => x.project_id,
                        principalTable: "project",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_area",
                table: "project",
                column: "area")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_project_name",
                table: "project",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_plan_plan_id",
                table: "project_plan",
                column: "plan_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_plan_project_id_seq",
                table: "project_plan",
                columns: new[] { "project_id", "seq" },
                unique: true);

            // Writes get an operation like the network's (#23).
            migrationBuilder.Sql("""
                CREATE TRIGGER project_operation AFTER INSERT OR UPDATE OR DELETE ON project FOR EACH STATEMENT EXECUTE FUNCTION cmdb_operation_statement();
                CREATE TRIGGER project_plan_operation AFTER INSERT OR UPDATE OR DELETE ON project_plan FOR EACH STATEMENT EXECUTE FUNCTION cmdb_operation_statement();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_plan");

            migrationBuilder.DropTable(
                name: "project");
        }
    }
}
