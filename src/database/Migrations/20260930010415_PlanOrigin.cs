using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class PlanOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client",
                table: "plan",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "created_via",
                table: "plan",
                type: "text",
                nullable: false,
                defaultValue: "api");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_created_via",
                table: "plan",
                sql: "created_via IN ('api', 'mcp')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_created_via",
                table: "plan");

            migrationBuilder.DropColumn(
                name: "client",
                table: "plan");

            migrationBuilder.DropColumn(
                name: "created_via",
                table: "plan");
        }
    }
}
