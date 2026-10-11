using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class RackRoomPlace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation");

            migrationBuilder.AddColumn<string>(
                name: "facing",
                table: "location",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "room_row",
                table: "location",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "room_slot",
                table: "location",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation",
                sql: "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename', 'set_attributes', 'create_site', 'create_equipment', 'create_cable', 'split_cable', 'remove', 'set_classification', 'move', 'set_conductor_usage', 'create_location', 'create_service', 'create_circuit', 'set_circuit_path', 'link_circuit', 'link_service', 'set_rack_place')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_location_facing",
                table: "location",
                sql: "facing IN ('front', 'back')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_location_room_place",
                table: "location",
                sql: "(room_row IS NULL AND room_slot IS NULL AND facing IS NULL) OR (kind = 'rack' AND room_row > 0 AND room_slot > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_location_facing",
                table: "location");

            migrationBuilder.DropCheckConstraint(
                name: "ck_location_room_place",
                table: "location");

            migrationBuilder.DropColumn(
                name: "facing",
                table: "location");

            migrationBuilder.DropColumn(
                name: "room_row",
                table: "location");

            migrationBuilder.DropColumn(
                name: "room_slot",
                table: "location");

            migrationBuilder.AddCheckConstraint(
                name: "ck_plan_operation_kind",
                table: "plan_operation",
                sql: "kind IN ('connect', 'disconnect', 'set_lifecycle', 'rename', 'set_attributes', 'create_site', 'create_equipment', 'create_cable', 'split_cable', 'remove', 'set_classification', 'move', 'set_conductor_usage', 'create_location', 'create_service', 'create_circuit', 'set_circuit_path', 'link_circuit', 'link_service')");
        }
    }
}
