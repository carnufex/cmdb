using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class RouteSegmentTrunk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "trunk",
                table: "route_segment",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Trunk conduit carries a cable of 96 fibres or more, as the large cables drawn at every zoom level (#243).
            migrationBuilder.Sql("""
                UPDATE route_segment r SET trunk = true
                WHERE EXISTS (SELECT 1 FROM duct_segment ds JOIN subduct s ON s.duct_id = ds.duct_id JOIN cable_path p ON p.subduct_id = s.id
                              JOIN cable c ON c.id = p.cable_id JOIN cable_type t ON t.id = c.cable_type_id
                              WHERE ds.route_segment_id = r.id AND t.conductor_count >= 96);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_route_segment_trunk_geom",
                table: "route_segment",
                column: "geom",
                filter: "trunk")
                .Annotation("Npgsql:IndexMethod", "gist");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_route_segment_trunk_geom",
                table: "route_segment");

            migrationBuilder.DropColumn(
                name: "trunk",
                table: "route_segment");
        }
    }
}
