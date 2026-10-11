using System.Diagnostics;
using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Plans;
using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Sites;

public sealed class RoomRequest
{
    public long Id { get; set; }

    /// <summary>The room in a plan's view (#24): new racks and racks placed elsewhere.</summary>
    [QueryParam]
    public long? Plan { get; set; }
}

/// <summary>A rack in its room (#256).</summary>
/// <param name="Row">Its row from 1, with <paramref name="Slot"/>; null when its place is not known (drawn last).</param>
/// <param name="Facing">front or back: which way its front faces along the row.</param>
/// <param name="FreeUnits">Units no rack-mounted equipment takes.</param>
/// <param name="Criticality">The highest criticality set on equipment in the rack (#180), or null.</param>
/// <param name="Plan">new (the plan adds it, negative id) or moved (the plan gives it another place); null otherwise.</param>
public sealed record RoomRack(long Id, string Name, int? Row, int? Slot, string? Facing, int Units, int FreeUnits, int Equipment, int? Criticality,
    string? Plan = null);

/// <summary>Connections between equipment in two racks of the room.</summary>
public sealed record RoomLink(long A, long B, int Connections);

/// <summary>A room seen from above (#256): its racks in their rows and the connections between them.</summary>
/// <param name="CriticalFrom">The criticality level from which a rack is critical (#180).</param>
public sealed record RoomDetail(long Id, string Name, ObjectRef Site, string? Building, IReadOnlyList<RoomRack> Racks, IReadOnlyList<RoomLink> Links,
    int CriticalFrom, double ElapsedMs);

/// <summary>A room of a site, counted for the site panel (#256).</summary>
public sealed record SiteRoom(long Id, string Name, string? Building, int Racks, int Units, int FreeUnits);

/// <summary>
/// The room view (#256): the room's racks with their row, place and facing, how full each is, the highest criticality in
/// it, and the connections between racks; in a plan, the racks it adds or places elsewhere. Access as for the equipment
/// in the racks: outside the caller's scope the room does not exist (#22).
/// </summary>
public sealed class GetRoomEndpoint(RequestDb db, GraphHolder holder, PlanViews plans) : Endpoint<RoomRequest, RoomDetail>
{
    public override void Configure() => Get("/rooms/{id}");

    public override async Task HandleAsync(RoomRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var scope = HttpContext.Scope();
        long siteId;
        string siteCode, siteName, name;
        string? building;
        await using (var cmd = db.CreateCommand($"""
            SELECT l.site_id, s.code, s.name, l.name, b.name
            FROM location l JOIN site s ON s.id = l.site_id LEFT JOIN location b ON b.id = l.parent_id AND b.kind = 'building'
            WHERE l.id = $1 AND l.kind = 'room' AND {ScopeSql.Site("s.id", 2)}
            """))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            cmd.Parameters.Add(scope.Parameter());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            (siteId, siteCode, siteName, name, building) = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }

        var schema = Cmdb.Catalog.ClassificationCatalog.Current.Find("criticality");
        var racks = new List<RoomRack>();
        await using (var cmd = db.CreateCommand("""
            SELECT r.id, r.name, r.room_row, r.room_slot, r.facing, coalesce(r.rack_units, 42),
                   coalesce(sum(coalesce(t.rack_units, 1)) FILTER (WHERE e.rack_position IS NOT NULL), 0)::int,
                   count(e.id)::int,
                   (SELECT max(c.level) FROM classification c JOIN equipment x ON c.object_type = 'equipment' AND c.object_id = x.id
                    WHERE x.location_id = r.id AND c.schema_key = $2)
            FROM location r
            LEFT JOIN equipment e ON e.location_id = r.id AND e.parent_id IS NULL AND e.lifecycle <> 'removed'
            LEFT JOIN equipment_type t ON t.id = e.equipment_type_id
            WHERE r.parent_id = $1 AND r.kind = 'rack'
            GROUP BY r.id
            ORDER BY r.room_row NULLS LAST, r.room_slot, r.name
            """))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            cmd.Parameters.Add(new() { Value = schema?.Key ?? "criticality" });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var units = reader.GetInt32(5);
                racks.Add(new RoomRack(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt16(2),
                    reader.IsDBNull(3) ? null : reader.GetInt16(3), reader.IsDBNull(4) ? null : reader.GetString(4), units,
                    Math.Max(0, units - reader.GetInt32(6)), reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8)));
            }
        }

        var links = new List<RoomLink>();
        await using (var cmd = db.CreateCommand("""
            SELECT least(ea.location_id, eb.location_id), greatest(ea.location_id, eb.location_id), count(*)::int
            FROM connection c
            JOIN port pa ON pa.terminal_id = c.a_terminal_id JOIN equipment ea ON ea.id = pa.equipment_id
            JOIN port pb ON pb.terminal_id = c.b_terminal_id JOIN equipment eb ON eb.id = pb.equipment_id
            WHERE c.valid_to IS NULL AND ea.location_id = ANY($1) AND eb.location_id = ANY($1) AND ea.location_id <> eb.location_id
            GROUP BY 1, 2
            """))
        {
            cmd.Parameters.Add(new() { Value = racks.Select(r => r.Id).ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                links.Add(new RoomLink(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2)));
            }
        }

        if (req.Plan is { } planId)
        {
            if (holder.Current is not { } graph || await plans.GetAsync(graph, planId, scope, ct) is not { } view)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            racks = Planned(racks, view.Chain.Operations, req.Id, siteId);
        }
        await Send.OkAsync(new RoomDetail(req.Id, name, new ObjectRef("site", siteId, siteCode, siteName), building, racks, links,
            schema?.CriticalFrom ?? 4, Math.Round(sw.Elapsed.TotalMilliseconds, 1)), ct);
    }

    /// <summary>The room's racks in a plan's view: racks the plan creates in the room, and racks it gives another place.</summary>
    internal static List<RoomRack> Planned(List<RoomRack> racks, IReadOnlyList<PlanOp> operations, long room, long site)
    {
        var result = racks.ToList();
        foreach (var op in operations)
        {
            var p = op.Payload;
            switch (op.Kind)
            {
                case "create_location" when Text(p, "kind") == "rack" && p.GetProperty("site").GetInt64() == site
                    && p.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Number && parent.GetInt64() == room:
                    {
                        var units = Number(p, "rackUnits") ?? 42;
                        result.Add(new RoomRack(Plans.Planned.ObjectId(op.Id), p.GetProperty("name").GetString()!, Number(p, "roomRow"), Number(p, "roomSlot"),
                            Text(p, "facing"), units, units, 0, null, "new"));
                        break;
                    }
                case "set_rack_place" when result.FindIndex(r => r.Id == p.GetProperty("id").GetInt64()) is var i and >= 0:
                    result[i] = result[i] with { Row = Number(p, "roomRow"), Slot = Number(p, "roomSlot"), Facing = Text(p, "facing"), Plan = result[i].Plan ?? "moved" };
                    break;
            }
        }
        return [.. result.OrderBy(r => r.Row ?? int.MaxValue).ThenBy(r => r.Slot).ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    private static string? Text(JsonElement p, string field) =>
        p.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Number(JsonElement p, string field) =>
        p.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}

/// <summary>The rooms of a site with their racks and free units, for the site panel (#256). Outside the scope: none.</summary>
public sealed class SiteRoomsEndpoint(RequestDb db) : Endpoint<SiteRoomsRequest, IReadOnlyList<SiteRoom>>
{
    public override void Configure() => Get("/sites/{id}/rooms");

    public override async Task HandleAsync(SiteRoomsRequest req, CancellationToken ct)
    {
        var rooms = new List<SiteRoom>();
        await using var cmd = db.CreateCommand($"""
            SELECT m.id, m.name, b.name,
                   (SELECT count(*) FROM location r WHERE r.parent_id = m.id AND r.kind = 'rack')::int,
                   (SELECT coalesce(sum(coalesce(r.rack_units, 42)), 0) FROM location r WHERE r.parent_id = m.id AND r.kind = 'rack')::int,
                   (SELECT coalesce(sum(coalesce(t.rack_units, 1)), 0) FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id
                    JOIN location r ON r.id = e.location_id
                    WHERE r.parent_id = m.id AND r.kind = 'rack' AND e.parent_id IS NULL AND e.rack_position IS NOT NULL AND e.lifecycle <> 'removed')::int
            FROM location m
            LEFT JOIN location b ON b.id = m.parent_id AND b.kind = 'building'
            WHERE m.site_id = $1 AND m.kind = 'room' AND {ScopeSql.Site("m.site_id", 2)}
            ORDER BY b.name NULLS LAST, m.name
            """);
        cmd.Parameters.Add(new() { Value = req.Id });
        cmd.Parameters.Add(HttpContext.Scope().Parameter());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var units = reader.GetInt32(4);
            rooms.Add(new SiteRoom(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3),
                units, Math.Max(0, units - reader.GetInt32(5))));
        }
        await Send.OkAsync(rooms, ct);
    }
}

public sealed class SiteRoomsRequest
{
    public long Id { get; set; }
}
