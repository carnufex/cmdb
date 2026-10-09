using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Sites;

public sealed record SiteRequest(long Id);

/// <param name="Position">The lowest rack unit it takes (#173), and <paramref name="Units"/> how many; null when not rack-mounted.</param>
public sealed record SiteEquipment(long Id, string Name, string Model, string Category, string Lifecycle, int Ports, int Cards,
    int? Position = null, int? Units = null);

/// <param name="RackUnits">A rack's height in units (#173).</param>
public sealed record SiteLocation(long Id, long? ParentId, string Kind, string Name, IReadOnlyList<SiteEquipment> Equipment, int? RackUnits = null);

public sealed record SiteCable(long Id, string Code, string TypeName, string Medium, int Conductors, double LengthM, string Lifecycle, ObjectRef OtherEnd);

public sealed record SiteDetail(
    long Id,
    string Code,
    string Name,
    string SiteType,
    string Lifecycle,
    double? X,
    double? Y,
    JsonElement Attributes,
    IReadOnlyList<SiteLocation> Locations,
    IReadOnlyList<SiteCable> Cables,
    IReadOnlyList<ObjectSource>? Sources = null);

/// <summary>A site with what is on it and what it connects to. Cards are counted under their chassis.</summary>
public sealed class GetSiteEndpoint(RequestDb db) : Endpoint<SiteRequest, SiteDetail>
{
    public override void Configure() => Get("/sites/{id}");

    public override async Task HandleAsync(SiteRequest req, CancellationToken ct)
    {
        var detail = await LoadAsync(db, req.Id, HttpContext.Scope(), ct);
        if (detail is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(detail, ct);
    }

    /// <summary>Also used by the MCP tools (#61), so agents see exactly what the UI shows.</summary>
    internal static async Task<SiteDetail?> LoadAsync(NpgsqlDataSource db, long id, UserScope scope, CancellationToken ct)
    {
        // Outside the caller's scope the site does not exist (404, #22); cables are listed when the scope shows them,
        // and a far end outside it is a placeholder.
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new($"""
                    SELECT id, code, name, site_type, lifecycle::text, ST_X(ST_PointOnSurface(geom)), ST_Y(ST_PointOnSurface(geom)), attributes::text
                    FROM site WHERE id = $1 AND {ScopeSql.Site("site.id", 2)}
                    """) { Parameters = { new() { Value = id }, scope.Parameter() } },
                new("""
                    SELECT id, parent_id, kind, name, CASE WHEN kind = 'rack' THEN coalesce(rack_units, 42) END
                    FROM location WHERE site_id = $1 ORDER BY parent_id NULLS FIRST, name
                    """) { Parameters = { new() { Value = id } } },
                new("""
                    SELECT e.id, e.location_id, e.name, et.model, et.category, e.lifecycle::text,
                           (SELECT count(*) FROM port p WHERE p.equipment_id = e.id)::int,
                           (SELECT count(*) FROM equipment c WHERE c.parent_id = e.id)::int,
                           e.rack_position, et.rack_units
                    FROM equipment e JOIN equipment_type et ON et.id = e.equipment_type_id
                    WHERE e.site_id = $1 AND e.parent_id IS NULL
                    ORDER BY et.category, e.name
                    """) { Parameters = { new() { Value = id } } },
                new($"""
                    SELECT c.id, c.code, ct.name, ct.medium::text, ct.conductor_count, c.length_m, c.lifecycle::text,
                           o.id, o.code, o.name, o.lifecycle::text, {ScopeSql.Site("o.id", 2)}
                    FROM cable c
                    JOIN cable_type ct ON ct.id = c.cable_type_id
                    JOIN site o ON o.id = CASE WHEN c.a_site_id = $1 THEN c.b_site_id ELSE c.a_site_id END
                    WHERE (c.a_site_id = $1 OR c.b_site_id = $1) AND {ScopeSql.Cable("c.id", 2)}
                    ORDER BY ct.conductor_count DESC, c.code
                    """) { Parameters = { new() { Value = id }, scope.Parameter() } },
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        var (siteId, code, name, type, lifecycle, x, y, attributes) = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), scope.HidesCoordinates ? (double?)null : reader.GetDouble(5),
            scope.HidesCoordinates ? (double?)null : reader.GetDouble(6), reader.GetString(7));

        await reader.NextResultAsync(ct);
        var locations = new List<(long Id, long? Parent, string Kind, string Name, int? Units)>();
        while (await reader.ReadAsync(ct))
        {
            locations.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        await reader.NextResultAsync(ct);
        var equipment = new Dictionary<long, List<SiteEquipment>>();
        while (await reader.ReadAsync(ct))
        {
            var location = reader.GetInt64(1);
            if (!equipment.TryGetValue(location, out var list))
            {
                equipment[location] = list = [];
            }
            list.Add(new SiteEquipment(reader.GetInt64(0), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt16(8), reader.IsDBNull(9) ? null : reader.GetInt16(9)));
        }

        await reader.NextResultAsync(ct);
        var cables = new List<SiteCable>();
        while (await reader.ReadAsync(ct))
        {
            cables.Add(new SiteCable(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetDouble(5), reader.GetString(6),
                reader.GetBoolean(11)
                    ? new ObjectRef("site", reader.GetInt64(7), reader.GetString(8), reader.GetString(9), reader.GetString(10))
                    : ObjectRef.Hidden("site")));
        }

        await reader.DisposeAsync();
        return new SiteDetail(siteId, code, name, type, lifecycle, x, y, Terminals.Json(scope.MaskAttributes(attributes)),
            [.. locations.Select(l => new SiteLocation(l.Id, l.Parent, l.Kind, l.Name, equipment.GetValueOrDefault(l.Id) ?? [], l.Units))],
            cables, await Sources.LoadAsync(conn, "site", siteId, scope, ct));
    }
}
