using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class VoiceCallEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "voice_call_end",
                columns: table => new
                {
                    conversation_id = table.Column<string>(type: "text", nullable: false),
                    ended_by = table.Column<string>(type: "text", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voice_call_end", x => x.conversation_id);
                });

            // Writes get an operation like the service requests' (#23).
            migrationBuilder.Sql("CREATE TRIGGER voice_call_end_operation AFTER INSERT OR UPDATE OR DELETE ON voice_call_end FOR EACH STATEMENT EXECUTE FUNCTION cmdb_operation_statement();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "voice_call_end");
        }
    }
}
