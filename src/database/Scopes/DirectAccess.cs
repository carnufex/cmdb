using Npgsql;

namespace Cmdb.Database.Scopes;

/// <summary>
/// Grants for roles that read the database directly (ADR-0012, #96). Each existing role named in
/// <c>access_scope.db_roles</c> gets SELECT on the tables that have a row-level security policy, their type catalogs
/// and the materialised scope tables, and nothing else. Roles whose scopes hide attributes do not get the
/// <c>attributes</c> columns, and hidden coordinates remove <c>geom</c>. The roles are created outside the application
/// (CloudNativePG managed roles); a named role that does not exist is skipped.
/// </summary>
public static class DirectAccess
{
    private const string Sql = """
        DO $$
        DECLARE r record; t text; cols text;
        BEGIN
            FOR r IN
                SELECT u.role,
                       bool_or(cardinality(a.hidden_attributes) > 0) AS hide_attributes,
                       bool_or('coordinates' = ANY (a.hidden_attributes)) AS hide_geom
                FROM access_scope a
                CROSS JOIN LATERAL unnest(a.db_roles) AS u(role)
                JOIN pg_roles pr ON pr.rolname = u.role
                GROUP BY u.role
            LOOP
                EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', r.role);
                EXECUTE format('GRANT SELECT ON scope_site, scope_cable, scope_circuit, scope_service, equipment_type, '
                    'cable_type, port, conductor, circuit_hop, service_circuit TO %I', r.role);
                EXECUTE format('GRANT SELECT (key, db_roles, valid_to) ON access_scope TO %I', r.role);
                FOREACH t IN ARRAY ARRAY['site', 'location', 'equipment', 'cable', 'circuit', 'service'] LOOP
                    -- Revoking the table privilege also revokes earlier column grants, so newly hidden columns go.
                    EXECUTE format('REVOKE SELECT ON %I FROM %I', t, r.role);
                    SELECT string_agg(quote_ident(column_name), ', ' ORDER BY ordinal_position) INTO cols
                    FROM information_schema.columns
                    WHERE table_schema = 'public' AND table_name = t
                      AND NOT (r.hide_attributes AND column_name = 'attributes')
                      AND NOT (r.hide_geom AND column_name = 'geom');
                    EXECUTE format('GRANT SELECT (%s) ON %I TO %I', cols, t, r.role);
                END LOOP;
            END LOOP;
        END $$;
        """;

    public static async Task GrantAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
