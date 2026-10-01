using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class IncidentResolved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resolved_at",
                table: "incident",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolved_by",
                table: "incident",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resolved_at",
                table: "incident");

            migrationBuilder.DropColumn(
                name: "resolved_by",
                table: "incident");
        }
    }
}
