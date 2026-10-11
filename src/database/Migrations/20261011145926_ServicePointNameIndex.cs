using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class ServicePointNameIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_service_point_name_trgm",
                table: "service_point",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_service_point_name_trgm",
                table: "service_point");
        }
    }
}
