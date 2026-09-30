using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class Changelog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "changelog_read_at",
                table: "user_preference",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "changelog_entry",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: true),
                    issues = table.Column<int[]>(type: "integer[]", nullable: false),
                    published = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_changelog_entry", x => x.key);
                    table.CheckConstraint("ck_changelog_entry_category", "category IN ('nytt', 'förbättrat', 'rättat')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_changelog_entry_date",
                table: "changelog_entry",
                column: "date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "changelog_entry");

            migrationBuilder.DropColumn(
                name: "changelog_read_at",
                table: "user_preference");
        }
    }
}
