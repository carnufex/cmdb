using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class PruneOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "graph_change_pruned",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    tx = table.Column<ulong>(type: "xid8", nullable: false),
                    pruned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_graph_change_pruned", x => x.id);
                    table.CheckConstraint("ck_graph_change_pruned_single", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graph_change_pruned");
        }
    }
}
