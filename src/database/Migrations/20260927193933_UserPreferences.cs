using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class UserPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_preference",
                columns: table => new
                {
                    subject = table.Column<string>(type: "text", nullable: false),
                    theme = table.Column<string>(type: "text", nullable: false, defaultValue: "dark"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_preference", x => x.subject);
                    table.CheckConstraint("ck_user_preference_theme", "theme IN ('dark', 'light')");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_preference");
        }
    }
}
