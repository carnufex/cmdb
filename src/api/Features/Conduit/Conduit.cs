using System.Diagnostics;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Map;
using Cmdb.Api.Features.Objects;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Conduit;

/// <param name="Occupancy">empty, reserved, cable or blown_fibre.</param>
/// <param name="Cable">The cable in the tube; a placeholder when it is outside the caller's scope.</param>
public sealed record SubductView(long Id, int Number, string? Color, string Occupancy, ObjectRef? Cable);

/// <param name="InSubduct">The tube of another duct this one is pulled into (duct in duct), as "DK-000012 rör 3".</param>
public sealed record DuctView(long Id, string Code, string TypeKey, string TypeName, int OuterDiameterMm, int InnerDiameterMm, string Lifecycle,
    string? InSubduct, IReadOnlyList<SubductView> Subducts);

public sealed record RouteSegmentDetail(long Id, string Code, string Construction, string? Owner, string Lifecycle, double LengthM, ObjectRef A,
    ObjectRef B, IReadOnlyList<DuctView> Ducts, int FreeSubducts, double ElapsedMs);

public sealed record RouteSegmentRequest(long Id);

/// <param name="Segment">The route segment; a placeholder when it is outside the caller's scope.</param>
public sealed record CablePathStep(int Seq, ObjectRef Segment, string? Construction, string Duct, int Subduct, string? Color);

public sealed record CablePathRequest(long Id);

/// <summary>
/// A route segment with its ducts and their tubes (ADR-0014, #236), within the caller's scopes: a cable in a tube is named
/// when the caller may see it and shown as taken otherwise; an end site outside the scope is a placeholder.
/// </summary>
public sealed class RouteSegmentEndpoint(RequestDb db) : Endpoint<RouteSegmentRequest, RouteSegmentDetail>
{
    public override void Configure() => Get("/route-segments/{id}");

    public override async Task HandleAsync(RouteSegmentRequest req, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var scope = HttpContext.Scope();
        await using var conn = await db.Source.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new($"""
                    SELECT r.id, r.code, r.construction, r.owner, r.lifecycle::text, r.length_m,
                           a.id, a.code, a.name, a.lifecycle::text, {ScopeSql.Site("a.id", 2)},
                           b.id, b.code, b.name, b.lifecycle::text, {ScopeSql.Site("b.id", 2)}
                    FROM route_segment r JOIN site a ON a.id = r.a_site_id JOIN site b ON b.id = r.b_site_id
                    WHERE r.id = $1 AND {ScopeSql.RouteSegment("r.id", 2)}
                    """) { Parameters = { new() { Value = req.Id }, scope.Parameter() } },
                new($"""
                    SELECT d.id, d.code, t.key, t.name, t.outer_diameter_mm, t.subduct_inner_diameter_mm, d.lifecycle::text,
                           p.code || ' rör ' || ps.number,
                           s.id, s.number, s.color, s.occupancy,
                           EXISTS (SELECT 1 FROM reservation v WHERE v.resource_kind = 'subduct' AND v.resource_id = s.id AND v.released_at IS NULL),
                           c.id, c.code, c.lifecycle::text, c.id IS NOT NULL AND {ScopeSql.Cable("c.id", 2)}
                    FROM duct_segment ds JOIN duct d ON d.id = ds.duct_id JOIN duct_type t ON t.id = d.duct_type_id
                    JOIN subduct s ON s.duct_id = d.id
                    LEFT JOIN subduct ps ON ps.id = d.parent_subduct_id LEFT JOIN duct p ON p.id = ps.duct_id
                    LEFT JOIN cable_path cp ON cp.subduct_id = s.id LEFT JOIN cable c ON c.id = cp.cable_id
                    WHERE ds.route_segment_id = $1
                    ORDER BY d.code, s.number
                    """) { Parameters = { new() { Value = req.Id }, scope.Parameter() } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await reader.DisposeAsync();
            await Send.NotFoundAsync(ct);
            return;
        }
        ObjectRef Site(int at) => reader.GetBoolean(at + 4)
            ? new ObjectRef("site", reader.GetInt64(at), reader.GetString(at + 1), reader.GetString(at + 2), reader.GetString(at + 3))
            : ObjectRef.Hidden("site");
        var (id, code, construction, owner, lifecycle, length) = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetDouble(5));
        var (a, b) = (Site(6), Site(11));

        await reader.NextResultAsync(ct);
        var ducts = new List<DuctView>();
        var tubes = new List<SubductView>();
        DuctView? current = null;
        while (await reader.ReadAsync(ct))
        {
            if (current?.Id != reader.GetInt64(0))
            {
                if (current is not null)
                {
                    ducts.Add(current with { Subducts = [.. tubes] });
                    tubes.Clear();
                }
                current = new DuctView(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                    reader.GetInt32(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), []);
            }
            var occupancy = reader.GetString(11);
            ObjectRef? cable = null;
            if (!reader.IsDBNull(13))
            {
                occupancy = "cable";
                cable = reader.GetBoolean(16)
                    ? new ObjectRef("cable", reader.GetInt64(13), reader.GetString(14), null, reader.GetString(15))
                    : ObjectRef.Hidden("cable");
            }
            else if (occupancy == "cable")
            {
                // Marked as holding a cable with no recorded path: taken, without a name.
                cable = ObjectRef.Hidden("cable");
            }
            else if (occupancy == "empty" && reader.GetBoolean(12))
            {
                occupancy = "reserved";
            }
            tubes.Add(new SubductView(reader.GetInt64(8), reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetString(10), occupancy, cable));
        }
        if (current is not null)
        {
            ducts.Add(current with { Subducts = [.. tubes] });
        }
        await Send.OkAsync(new RouteSegmentDetail(id, code, construction, owner, lifecycle, length, a, b, ducts,
            ducts.Sum(d => d.Subducts.Count(s => s.Occupancy == "empty")), Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1)), ct);
    }
}

/// <summary>A cable's way through the conduit (ADR-0014, #236): route segment, duct and tube from its A end.</summary>
public sealed class CablePathEndpoint(RequestDb db) : Endpoint<CablePathRequest, IReadOnlyList<CablePathStep>>
{
    public override void Configure() => Get("/cables/{id}/path");

    public override async Task HandleAsync(CablePathRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        await using var cmd = db.Source.CreateCommand($"""
            SELECT cp.seq, r.id, r.code, r.lifecycle::text, r.construction, {ScopeSql.RouteSegment("r.id", 2)}, d.code, s.number, s.color
            FROM cable c JOIN cable_path cp ON cp.cable_id = c.id JOIN subduct s ON s.id = cp.subduct_id JOIN duct d ON d.id = s.duct_id
            JOIN duct_segment ds ON ds.duct_id = d.id JOIN route_segment r ON r.id = ds.route_segment_id
            WHERE c.id = $1 AND {ScopeSql.Cable("c.id", 2)}
            ORDER BY cp.seq
            """);
        cmd.Parameters.Add(new() { Value = req.Id });
        cmd.Parameters.Add(scope.Parameter());
        var steps = new List<CablePathStep>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var visible = reader.GetBoolean(5);
            steps.Add(new CablePathStep(reader.GetInt32(0),
                visible ? new ObjectRef("route-segment", reader.GetInt64(1), reader.GetString(2), null, reader.GetString(3)) : ObjectRef.Hidden("route-segment"),
                visible ? reader.GetString(4) : null, visible ? reader.GetString(6) : "Dold", visible ? reader.GetInt32(7) : 0,
                visible && !reader.IsDBNull(8) ? reader.GetString(8) : null));
        }
        await Send.OkAsync(steps, ct);
    }
}

/// <summary>
/// Vector tiles of the conduit (ADR-0014, #236), apart from the network's so the network tiles keep their budget: route
/// segments with how they are built, their ducts and free tubes. Corridors (segments at a manhole) at every zoom level,
/// the rest from <see cref="TileGrid.DetailZoom"/>. Cut at the edge of the caller's scope areas.
/// </summary>
public sealed class ConduitTilesEndpoint(RequestDb db) : Endpoint<TileRequest>
{
    public override void Configure() => Get("/tiles/conduit/{z}/{x}/{y}");

    public override async Task HandleAsync(TileRequest req, CancellationToken ct)
    {
        if (req.Z is < 0 or > TileGrid.MaxZoom || req.X < 0 || req.Y < 0 || req.X >= 1 << req.Z || req.Y >= 1 << req.Z)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scope = HttpContext.Scope();
        HttpContext.Response.Headers.CacheControl = "private, max-age=60";
        if (scope.HidesCoordinates)
        {
            await Send.BytesAsync([], contentType: "application/vnd.mapbox-vector-tile", cancellation: ct);
            return;
        }
        await using var cmd = db.Source.CreateCommand($"""
            WITH bounds AS (
                SELECT ST_TileEnvelope($1, $2, $3, ST_MakeEnvelope({TileGrid.MinX}, {TileGrid.MinY}, {TileGrid.MaxX}, {TileGrid.MaxY}, 3006)) AS geom
            ), routes AS (
                SELECT ST_AsMVTGeom({ScopeSql.RouteSegmentGeometry("r", 4, scope)}, b.geom, 4096, 64, true) AS geom,
                       r.id, r.code, r.construction, r.lifecycle::text AS lifecycle,
                       (SELECT count(*) FROM duct_segment ds WHERE ds.route_segment_id = r.id)::int AS ducts,
                       (SELECT count(*) FROM duct_segment ds JOIN subduct s ON s.duct_id = ds.duct_id
                        WHERE ds.route_segment_id = r.id AND s.occupancy = 'empty')::int AS free
                FROM route_segment r, bounds b
                WHERE r.geom && b.geom
                  AND r.lifecycle <> 'removed'
                  AND ($1 >= {TileGrid.DetailZoom} OR EXISTS (SELECT 1 FROM site m WHERE m.id IN (r.a_site_id, r.b_site_id) AND m.site_type = 'manhole'))
                  AND {ScopeSql.RouteSegment("r.id", 4)}
            )
            SELECT coalesce(ST_AsMVT(routes, 'routes', 4096, 'geom'), '') FROM routes
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.Z });
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.X });
        cmd.Parameters.Add(new NpgsqlParameter { Value = req.Y });
        cmd.Parameters.Add(scope.Parameter());
        var tile = (byte[])(await cmd.ExecuteScalarAsync(ct))!;
        await Send.BytesAsync(tile, contentType: "application/vnd.mapbox-vector-tile", cancellation: ct);
    }
}
