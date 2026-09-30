using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class VoiceToolCallReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reference",
                table: "voice_tool_call",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reference",
                table: "voice_tool_call");
        }
    }
}
