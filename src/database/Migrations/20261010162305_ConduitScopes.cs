using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class ConduitScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scope_route_segment",
                columns: table => new
                {
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    route_segment_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scope_route_segment", x => new { x.scope_key, x.route_segment_id });
                    table.ForeignKey(
                        name: "fk_scope_route_segment_access_scope_scope_key",
                        column: x => x.scope_key,
                        principalTable: "access_scope",
                        principalColumn: "key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scope_route_segment_route_segment_id",
                table: "scope_route_segment",
                column: "route_segment_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scope_route_segment");
        }
    }
}
