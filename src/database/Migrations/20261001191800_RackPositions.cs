using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class RackPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "rack_position",
                table: "equipment",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_equipment_rack_position",
                table: "equipment",
                sql: "rack_position IS NULL OR (rack_position > 0 AND parent_id IS NULL)");

            // Existing equipment stacked in its racks (#173).
            migrationBuilder.Sql(Cmdb.Database.RackStacking.Backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_equipment_rack_position",
                table: "equipment");

            migrationBuilder.DropColumn(
                name: "rack_position",
                table: "equipment");
        }
    }
}
