using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class DisableRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Row-level security (#22, step 2) is withdrawn: under RLS, Postgres may not push non-leakproof conditions
            // (LIKE, trigram similarity, PostGIS operators) into index scans, so search and tiles fell back to
            // sequential scans (demo: search p95 28 s, tiles 7 s). See the decision in #96.
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS scope_write ON site;
                DROP POLICY IF EXISTS scope_read ON site;
                ALTER TABLE site NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE site DISABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS scope_write ON equipment;
                DROP POLICY IF EXISTS scope_read ON equipment;
                ALTER TABLE equipment NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE equipment DISABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS scope_write ON cable;
                DROP POLICY IF EXISTS scope_read ON cable;
                ALTER TABLE cable NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE cable DISABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS scope_write ON circuit;
                DROP POLICY IF EXISTS scope_read ON circuit;
                ALTER TABLE circuit NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE circuit DISABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS scope_write ON service;
                DROP POLICY IF EXISTS scope_read ON service;
                ALTER TABLE service NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE service DISABLE ROW LEVEL SECURITY;
                DROP FUNCTION IF EXISTS cmdb_scope_keys();
                DROP FUNCTION IF EXISTS cmdb_scope_all();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-enabling means re-running RowLevelSecurity's Up; not done automatically.
        }
    }
}
