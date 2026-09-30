using Cmdb.Database.Model;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Cmdb.Database.Scopes;

/// <summary>
/// The demo's access scopes (#22), synthetic like everything else. Synced in the same step as the migrations and
/// the type catalog; a real installation manages scopes through a granting flow instead.
/// </summary>
public static class ScopeCatalog
{
    private static readonly WKTReader Wkt = new(new NtsGeometryServices(new PrecisionModel(), 3006));

    public static IReadOnlyList<AccessScope> Demo { get; } =
    [
        new()
        {
            Key = "hela-natet",
            Name = "Hela nätet",
            Groups = ["cmdb-full", "cmdb-agents"],
            Plans = ["*"],
            Reason = "Drift och förvaltning av hela nätet (demo).",
            GrantedBy = "demo-behorighetsagare",
            ApprovedBy = "demo-sakerhetschef",
        },
        new()
        {
            Key = "region-nord",
            Name = "Region Nord",
            // Roughly north of Sundsvall.
            Area = Wkt.Read("POLYGON((200000 6950000, 1000000 6950000, 1000000 7800000, 200000 7800000, 200000 6950000))"),
            Groups = ["cmdb-region-nord"],
            DbRoles = ["cmdb_rapport_nord"],
            Reason = "Regional drift i norr (demo).",
            GrantedBy = "demo-behorighetsagare",
            ApprovedBy = "demo-sakerhetschef",
        },
        new()
        {
            Key = "projekt-a",
            Name = "Projekt A",
            // An area on the west coast.
            Area = Wkt.Read("POLYGON((250000 6450000, 380000 6450000, 380000 6580000, 250000 6580000, 250000 6450000))"),
            SiteTypes = ["radio", "cabinet"],
            HiddenAttributes = ["serialNumber"],
            Groups = ["cmdb-projekt-a"],
            Reason = "Utbyggnad av radioaccess, projekt A (demo).",
            GrantedBy = "demo-projektledare",
            ApprovedBy = "demo-behorighetsagare",
        },
    ];

    /// <summary>Inserts or updates the demo scopes by key and refreshes what they show.</summary>
    public static async Task SyncAsync(CmdbDbContext db, CancellationToken ct = default)
    {
        var existing = await db.AccessScopes.ToDictionaryAsync(s => s.Key, StringComparer.Ordinal, ct);
        foreach (var scope in Demo)
        {
            if (!existing.TryGetValue(scope.Key, out var row))
            {
                row = new AccessScope
                {
                    Key = scope.Key,
                    Name = scope.Name,
                    Reason = scope.Reason,
                    GrantedBy = scope.GrantedBy,
                    ApprovedBy = scope.ApprovedBy,
                };
                db.AccessScopes.Add(row);
            }
            row.Name = scope.Name;
            row.Area = scope.Area;
            row.SiteTypes = scope.SiteTypes;
            row.HiddenAttributes = scope.HiddenAttributes;
            row.Plans = scope.Plans;
            row.CrossingMode = scope.CrossingMode;
            row.Groups = scope.Groups;
            row.DbRoles = scope.DbRoles;
            row.ValidTo = scope.ValidTo;
            row.Reason = scope.Reason;
            row.GrantedBy = scope.GrantedBy;
            row.ApprovedBy = scope.ApprovedBy;
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Materialises what each scope shows into <c>scope_site</c>, <c>scope_cable</c>, <c>scope_circuit</c> and
/// <c>scope_service</c> (#22), in one transaction so readers see either the old or the new state.
/// </summary>
public static class ScopeVisibility
{
    private const string Sql = """
        SELECT pg_advisory_xact_lock(hashtext('scope_refresh'));
        DELETE FROM scope_service;
        DELETE FROM scope_circuit;
        DELETE FROM scope_cable;
        DELETE FROM scope_site;

        INSERT INTO scope_site (scope_key, site_id)
        SELECT a.key, s.id
        FROM access_scope a JOIN site s
          ON (a.area IS NULL OR ST_Intersects(a.area, s.geom))
         AND (cardinality(a.site_types) = 0 OR s.site_type = ANY(a.site_types))
        WHERE a.valid_to IS NULL OR a.valid_to > now();

        -- A cable is shown when either end is. Clipping scopes also show a cable passing through the area without an
        -- end in it; the API cuts its geometry at the edge.
        INSERT INTO scope_cable (scope_key, cable_id)
        SELECT a.key, c.id
        FROM access_scope a JOIN cable c ON true
        LEFT JOIN scope_site sa ON sa.scope_key = a.key AND sa.site_id = c.a_site_id
        LEFT JOIN scope_site sb ON sb.scope_key = a.key AND sb.site_id = c.b_site_id
        WHERE (a.valid_to IS NULL OR a.valid_to > now())
          AND (sa.site_id IS NOT NULL OR sb.site_id IS NOT NULL
               OR (a.crossing_mode = 'clip' AND a.area IS NOT NULL AND ST_Intersects(a.area, c.geom)));

        -- A circuit is shown when the site at either end is. An end is a port (its equipment's site) or a conductor end
        -- (the site at that side of the cable).
        WITH ends AS (
            SELECT c.id,
                   coalesce(ea.site_id, CASE cea.side WHEN 'A' THEN ka.a_site_id ELSE ka.b_site_id END) AS a_site,
                   coalesce(eb.site_id, CASE ceb.side WHEN 'A' THEN kb.a_site_id ELSE kb.b_site_id END) AS b_site
            FROM circuit c
            LEFT JOIN port pa ON pa.terminal_id = c.a_terminal_id LEFT JOIN equipment ea ON ea.id = pa.equipment_id
            LEFT JOIN conductor_end cea ON cea.terminal_id = c.a_terminal_id
            LEFT JOIN conductor ca ON ca.id = cea.conductor_id LEFT JOIN cable ka ON ka.id = ca.cable_id
            LEFT JOIN port pb ON pb.terminal_id = c.b_terminal_id LEFT JOIN equipment eb ON eb.id = pb.equipment_id
            LEFT JOIN conductor_end ceb ON ceb.terminal_id = c.b_terminal_id
            LEFT JOIN conductor cb ON cb.id = ceb.conductor_id LEFT JOIN cable kb ON kb.id = cb.cable_id
        )
        INSERT INTO scope_circuit (scope_key, circuit_id)
        SELECT a.key, c.id
        FROM access_scope a JOIN ends c ON true
        LEFT JOIN scope_site sa ON sa.scope_key = a.key AND sa.site_id = c.a_site
        LEFT JOIN scope_site sb ON sb.scope_key = a.key AND sb.site_id = c.b_site
        WHERE (a.valid_to IS NULL OR a.valid_to > now())
          AND (sa.site_id IS NOT NULL OR sb.site_id IS NOT NULL);

        -- A service is shown when any circuit carrying it is.
        INSERT INTO scope_service (scope_key, service_id)
        SELECT DISTINCT z.scope_key, sc.service_id
        FROM scope_circuit z JOIN service_circuit sc ON sc.circuit_id = z.circuit_id;
        """;

    public static async Task RefreshAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(Sql, conn, tx) { CommandTimeout = 600 })
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
