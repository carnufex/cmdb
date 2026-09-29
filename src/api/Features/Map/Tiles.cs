using Cmdb.Api.Auth;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Map;

public sealed record TileRequest(int Z, int X, int Y);

/// <summary>
/// Vector tiles (MVT) of sites and cables in SWEREF 99 TM. The grid is one square z0 tile over
/// <see cref="TileGrid.Bounds"/>; the web map uses the same grid (map-grid.ts).
/// </summary>
public sealed class TilesEndpoint(NpgsqlDataSource db) : Endpoint<TileRequest>
{
    public override void Configure() => Get("/tiles/{z}/{x}/{y}");

    public override async Task HandleAsync(TileRequest req, CancellationToken ct)
    {
        if (req.Z is < 0 or > TileGrid.MaxZoom || req.X < 0 || req.Y < 0 || req.X >= 1 << req.Z || req.Y >= 1 << req.Z)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Access sites and small cables only from DetailZoom: a national view of 40 000 points is noise and
        // blows the tile budget. Only what the caller's scopes show is drawn (#22); tiles are per user and private.
        await using var cmd = db.CreateCommand($"""
            WITH bounds AS (
                SELECT ST_TileEnvelope($1, $2, $3, ST_MakeEnvelope({TileGrid.MinX}, {TileGrid.MinY}, {TileGrid.MaxX}, {TileGrid.MaxY}, 3006)) AS geom
            ), sites AS (
                SELECT ST_AsMVTGeom(s.geom, b.geom, 4096, 64, true) AS geom,
                       s.id, s.code, s.name, s.site_type AS kind, s.lifecycle::text AS lifecycle
                FROM site s, bounds b
                WHERE s.geom && b.geom
                  AND s.lifecycle <> 'removed'
                  AND ($1 >= {TileGrid.DetailZoom} OR s.site_type IN ('hub', 'aggregation'))
                  AND {ScopeSql.Site("s.id", 4)}
            ), cables AS (
                SELECT ST_AsMVTGeom(c.geom, b.geom, 4096, 64, true) AS geom,
                       c.id, c.code, c.lifecycle::text AS lifecycle, ct.medium::text AS medium, ct.conductor_count AS conductors
                FROM cable c JOIN cable_type ct ON ct.id = c.cable_type_id, bounds b
                WHERE c.geom && b.geom
                  AND c.lifecycle <> 'removed'
                  AND ($1 >= {TileGrid.DetailZoom} OR ct.conductor_count >= 96)
                  AND {ScopeSql.Cable("c.id", 4)}
            )
            SELECT (SELECT coalesce(ST_AsMVT(cables, 'cables', 4096, 'geom'), '') FROM cables)
                || (SELECT coalesce(ST_AsMVT(sites, 'sites', 4096, 'geom'), '') FROM sites)
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.Z });
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.X });
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.Y });
        cmd.Parameters.Add(HttpContext.Scope().Parameter());
        var tile = (byte[])(await cmd.ExecuteScalarAsync(ct))!;

        HttpContext.Response.Headers.CacheControl = "private, max-age=60";
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
