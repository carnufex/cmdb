using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class PlannedObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation",
                sql: "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename', 'create_site', 'create_equipment', 'create_cable')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation",
                sql: "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename')");
        }
    }
}
