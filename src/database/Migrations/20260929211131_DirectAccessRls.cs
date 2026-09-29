using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class DirectAccessRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "db_roles",
                table: "access_scope",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            // Row-level security for direct database access (ADR-0012, #96). The API connects as the table owner and
            // is not subject to it (no FORCE); it applies scopes itself (#22). Roles that read the database directly
            // see only what the scopes naming them in access_scope.db_roles show, and get no other tables
            // (DirectAccess.GrantAsync). Children follow their parent through a subquery that is itself under RLS.
            migrationBuilder.Sql("""
                CREATE FUNCTION cmdb_direct_scopes() RETURNS text[] LANGUAGE sql STABLE PARALLEL SAFE AS
                $$ SELECT coalesce(array_agg(key), '{}') FROM access_scope
                   WHERE current_user::text = ANY (db_roles) AND (valid_to IS NULL OR valid_to > now()) $$;

                ALTER TABLE site ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON site FOR SELECT
                    USING (id IN (SELECT z.site_id FROM scope_site z WHERE z.scope_key = ANY (cmdb_direct_scopes())));

                ALTER TABLE location ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON location FOR SELECT
                    USING (site_id IN (SELECT z.site_id FROM scope_site z WHERE z.scope_key = ANY (cmdb_direct_scopes())));

                ALTER TABLE equipment ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON equipment FOR SELECT
                    USING (site_id IN (SELECT z.site_id FROM scope_site z WHERE z.scope_key = ANY (cmdb_direct_scopes())));

                ALTER TABLE port ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON port FOR SELECT
                    USING (equipment_id IN (SELECT e.id FROM equipment e));

                ALTER TABLE cable ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON cable FOR SELECT
                    USING (id IN (SELECT z.cable_id FROM scope_cable z WHERE z.scope_key = ANY (cmdb_direct_scopes())));

                ALTER TABLE conductor ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON conductor FOR SELECT
                    USING (cable_id IN (SELECT c.id FROM cable c));

                ALTER TABLE circuit ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON circuit FOR SELECT
                    USING (id IN (SELECT z.circuit_id FROM scope_circuit z WHERE z.scope_key = ANY (cmdb_direct_scopes())));

                ALTER TABLE circuit_hop ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON circuit_hop FOR SELECT
                    USING (circuit_id IN (SELECT c.id FROM circuit c));

                ALTER TABLE service ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON service FOR SELECT
                    USING (id IN (SELECT z.service_id FROM scope_service z WHERE z.scope_key = ANY (cmdb_direct_scopes())));

                ALTER TABLE service_circuit ENABLE ROW LEVEL SECURITY;
                CREATE POLICY direct_read ON service_circuit FOR SELECT
                    USING (service_id IN (SELECT s.id FROM service s) AND circuit_id IN (SELECT c.id FROM circuit c));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY direct_read ON service_circuit;
                ALTER TABLE service_circuit DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON service;
                ALTER TABLE service DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON circuit_hop;
                ALTER TABLE circuit_hop DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON circuit;
                ALTER TABLE circuit DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON conductor;
                ALTER TABLE conductor DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON cable;
                ALTER TABLE cable DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON port;
                ALTER TABLE port DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON equipment;
                ALTER TABLE equipment DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON location;
                ALTER TABLE location DISABLE ROW LEVEL SECURITY;
                DROP POLICY direct_read ON site;
                ALTER TABLE site DISABLE ROW LEVEL SECURITY;
                DROP FUNCTION cmdb_direct_scopes();
                """);

            migrationBuilder.DropColumn(
                name: "db_roles",
                table: "access_scope");
        }
    }
}
