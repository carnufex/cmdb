using Cmdb.Api.Auth;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Map;

public sealed record TileRequest(int Z, int X, int Y)
{
    /// <summary>Draws the network as it was at this time (#23) instead of now.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>
    /// With <see cref="At"/>: <c>reality</c> (default) is what was in service then, as we know it today; <c>knowledge</c> is
    /// what the CMDB said then, with the versions that were recorded at the time.
    /// </summary>
    public string? Mode { get; init; }
}

/// <summary>
/// Vector tiles (MVT) of sites and cables in SWEREF 99 TM. The grid is one square z0 tile over
/// <see cref="TileGrid.Bounds"/>; the web map uses the same grid (map-grid.ts). With a time (#23, ADR-0013) the tile is drawn
/// from valid time, and in the knowledge mode from the versions in the history tables as well; the current tables' queries
/// are unchanged without one.
/// </summary>
public sealed class TilesEndpoint(RequestDb db) : Endpoint<TileRequest>
{
    public override void Configure() => Get("/tiles/{z}/{x}/{y}");

    public override async Task HandleAsync(TileRequest req, CancellationToken ct)
    {
        if (req.Z is < 0 or > TileGrid.MaxZoom || req.X < 0 || req.Y < 0 || req.X >= 1 << req.Z || req.Y >= 1 << req.Z
            || req.Mode is not (null or "reality" or "knowledge"))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var scope = HttpContext.Scope();
        HttpContext.Response.Headers.CacheControl = "private, max-age=60";
        if (scope.HidesCoordinates)
        {
            // Hidden positions (#22): the map is empty, not an error, so the rest of the UI works.
            await Send.BytesAsync([], contentType: "application/vnd.mapbox-vector-tile", cancellation: ct);
            return;
        }

        var at = req.At;
        var knowledge = at is not null && req.Mode == "knowledge";
        var sites = !knowledge ? "site" : """
            (SELECT id, geom, code, name, site_type, lifecycle, valid_from, valid_to FROM site WHERE sys_from <= $6
             UNION ALL
             SELECT id, geom, code, name, site_type, lifecycle, valid_from, valid_to FROM site_history WHERE sys_from <= $6 AND sys_to > $6)
            """;
        var cables = !knowledge ? "cable" : """
            (SELECT id, geom, code, cable_type_id, lifecycle, valid_from, valid_to FROM cable WHERE sys_from <= $6
             UNION ALL
             SELECT id, geom, code, cable_type_id, lifecycle, valid_from, valid_to FROM cable_history WHERE sys_from <= $6 AND sys_to > $6)
            """;
        string When(string alias) => at is null
            ? $"{alias}.lifecycle <> 'removed'"
            : $"{alias}.valid_from <= $6 AND ({alias}.valid_to IS NULL OR {alias}.valid_to > $6) AND ({alias}.lifecycle <> 'removed' OR {alias}.valid_to > $6)";

        // Access sites and small cables only from DetailZoom: a national view of 40 000 points is noise and
        // blows the tile budget. Only what the caller's scopes show is drawn (#22); tiles are per user and private.
        await using var cmd = db.CreateCommand($"""
            WITH bounds AS (
                SELECT ST_TileEnvelope($1, $2, $3, ST_MakeEnvelope({TileGrid.MinX}, {TileGrid.MinY}, {TileGrid.MaxX}, {TileGrid.MaxY}, 3006)) AS geom
            ), sites AS (
                SELECT ST_AsMVTGeom(s.geom, b.geom, 4096, 64, true) AS geom,
                       s.id, s.code, s.name, s.site_type AS kind, s.lifecycle::text AS lifecycle
                FROM {sites} s, bounds b
                WHERE s.geom && b.geom
                  AND {When("s")}
                  AND ($1 >= {TileGrid.DetailZoom} OR s.site_type = ANY($5))
                  AND {ScopeSql.Site("s.id", 4)}
            ), cables AS (
                SELECT ST_AsMVTGeom({ScopeSql.CableGeometry("c", 4, scope)}, b.geom, 4096, 64, true) AS geom,
                       c.id, c.code, c.lifecycle::text AS lifecycle, ct.medium::text AS medium, ct.conductor_count AS conductors
                FROM {cables} c JOIN cable_type ct ON ct.id = c.cable_type_id, bounds b
                WHERE c.geom && b.geom
                  AND {When("c")}
                  AND ($1 >= {TileGrid.DetailZoom} OR ct.conductor_count >= 96)
                  AND {ScopeSql.Cable("c.id", 4)}
            )
            SELECT (SELECT coalesce(ST_AsMVT(cables, 'cables', 4096, 'geom'), '') FROM cables)
                || (SELECT coalesce(ST_AsMVT(sites, 'sites', 4096, 'geom'), '') FROM sites)
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.Z });
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.X });
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.Y });
        cmd.Parameters.Add(scope.Parameter());
        // Hubs and aggregation nodes show at every zoom level (#208: by role, not type name).
        cmd.Parameters.Add(new NpgsqlParameter { Value = Cmdb.Catalog.TypeCatalog.Current.SiteTypesWith(Cmdb.Catalog.CatalogRoles.Hub, Cmdb.Catalog.CatalogRoles.Aggregation) });
        if (at is not null)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = at.Value.ToUniversalTime() });
        }
        var tile = (byte[])(await cmd.ExecuteScalarAsync(ct))!;

        await Send.BytesAsync(tile, contentType: "application/vnd.mapbox-vector-tile", cancellation: ct);
    }
}

/// <summary>The tile grid shared with the web map. Changing it means changing map-grid.ts too.</summary>
public static class TileGrid
{
    public const int MinX = -1_200_000;
    public const int MaxX = 1_800_000;
    public const int MinY = 5_500_000;
    public const int MaxY = 8_500_000;
    public const int MaxZoom = 16;

    /// <summary>First zoom level that includes access sites and small cables.</summary>
    public const int DetailZoom = 5;
}
