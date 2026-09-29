using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class RowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Row-level security as the last barrier (#22, ADR-0007). Each session says what it may see in
            // cmdb.scopes: '*' for everything (system work and unrestricted users), a comma-separated list of scope
            // keys, or nothing, which sees nothing. FORCE makes the policies apply to the owning role too; only a
            // superuser bypasses them. The setting is read once per query (InitPlan), not per row.
            migrationBuilder.Sql("""
                CREATE FUNCTION cmdb_scope_all() RETURNS boolean LANGUAGE sql STABLE PARALLEL SAFE AS
                $$ SELECT coalesce(current_setting('cmdb.scopes', true), '') = '*' $$;

                CREATE FUNCTION cmdb_scope_keys() RETURNS text[] LANGUAGE sql STABLE PARALLEL SAFE AS
                $$ SELECT string_to_array(nullif(nullif(nullif(current_setting('cmdb.scopes', true), ''), 'none'), '*'), ',') $$;

                ALTER TABLE site ENABLE ROW LEVEL SECURITY;
                ALTER TABLE site FORCE ROW LEVEL SECURITY;
                CREATE POLICY scope_read ON site FOR SELECT
                    USING ((SELECT cmdb_scope_all()) OR EXISTS (SELECT 1 FROM scope_site z WHERE z.scope_key = ANY (cmdb_scope_keys()) AND z.site_id = site.id));
                -- Writes need the whole network until plans (#24) bring scoped writes.
                CREATE POLICY scope_write ON site FOR ALL
                    USING ((SELECT cmdb_scope_all())) WITH CHECK ((SELECT cmdb_scope_all()));

                ALTER TABLE equipment ENABLE ROW LEVEL SECURITY;
                ALTER TABLE equipment FORCE ROW LEVEL SECURITY;
                CREATE POLICY scope_read ON equipment FOR SELECT
                    USING ((SELECT cmdb_scope_all()) OR EXISTS (SELECT 1 FROM scope_site z WHERE z.scope_key = ANY (cmdb_scope_keys()) AND z.site_id = equipment.site_id));
                -- Writes need the whole network until plans (#24) bring scoped writes.
                CREATE POLICY scope_write ON equipment FOR ALL
                    USING ((SELECT cmdb_scope_all())) WITH CHECK ((SELECT cmdb_scope_all()));

                ALTER TABLE cable ENABLE ROW LEVEL SECURITY;
                ALTER TABLE cable FORCE ROW LEVEL SECURITY;
                CREATE POLICY scope_read ON cable FOR SELECT
                    USING ((SELECT cmdb_scope_all()) OR EXISTS (SELECT 1 FROM scope_cable z WHERE z.scope_key = ANY (cmdb_scope_keys()) AND z.cable_id = cable.id));
                -- Writes need the whole network until plans (#24) bring scoped writes.
                CREATE POLICY scope_write ON cable FOR ALL
                    USING ((SELECT cmdb_scope_all())) WITH CHECK ((SELECT cmdb_scope_all()));

                ALTER TABLE circuit ENABLE ROW LEVEL SECURITY;
                ALTER TABLE circuit FORCE ROW LEVEL SECURITY;
                CREATE POLICY scope_read ON circuit FOR SELECT
                    USING ((SELECT cmdb_scope_all()) OR EXISTS (SELECT 1 FROM scope_circuit z WHERE z.scope_key = ANY (cmdb_scope_keys()) AND z.circuit_id = circuit.id));
                -- Writes need the whole network until plans (#24) bring scoped writes.
                CREATE POLICY scope_write ON circuit FOR ALL
                    USING ((SELECT cmdb_scope_all())) WITH CHECK ((SELECT cmdb_scope_all()));

                ALTER TABLE service ENABLE ROW LEVEL SECURITY;
                ALTER TABLE service FORCE ROW LEVEL SECURITY;
                CREATE POLICY scope_read ON service FOR SELECT
                    USING ((SELECT cmdb_scope_all()) OR EXISTS (SELECT 1 FROM scope_service z WHERE z.scope_key = ANY (cmdb_scope_keys()) AND z.service_id = service.id));
                -- Writes need the whole network until plans (#24) bring scoped writes.
                CREATE POLICY scope_write ON service FOR ALL
                    USING ((SELECT cmdb_scope_all())) WITH CHECK ((SELECT cmdb_scope_all()));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY scope_write ON site;
                DROP POLICY scope_read ON site;
                ALTER TABLE site NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE site DISABLE ROW LEVEL SECURITY;
                DROP POLICY scope_write ON equipment;
                DROP POLICY scope_read ON equipment;
                ALTER TABLE equipment NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE equipment DISABLE ROW LEVEL SECURITY;
                DROP POLICY scope_write ON cable;
                DROP POLICY scope_read ON cable;
                ALTER TABLE cable NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE cable DISABLE ROW LEVEL SECURITY;
                DROP POLICY scope_write ON circuit;
                DROP POLICY scope_read ON circuit;
                ALTER TABLE circuit NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE circuit DISABLE ROW LEVEL SECURITY;
                DROP POLICY scope_write ON service;
                DROP POLICY scope_read ON service;
                ALTER TABLE service NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE service DISABLE ROW LEVEL SECURITY;
                DROP FUNCTION cmdb_scope_keys();
                DROP FUNCTION cmdb_scope_all();
                """);
        }
    }
}
