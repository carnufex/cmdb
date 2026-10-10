using System.Text.Json;
using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;

namespace Cmdb.Api.Features.Sites;

public sealed class RackRequest
{
    public long Id { get; set; }

    /// <summary>The rack in a plan's view (#24): what the plan adds to it and takes out of it.</summary>
    [QueryParam]
    public long? Plan { get; set; }
}

/// <param name="Position">Its lowest unit; null for equipment that is not rack-mounted.</param>
/// <param name="Images">The model's panel pictures (#214), when the catalog has them.</param>
public sealed record RackEquipment(long Id, string Name, string Model, string TypeKey, string Category, string Lifecycle, int? Position, int Units,
    PanelImages? Images);

/// <summary>A rack and what sits in it (#255), with the active plan's changes: new equipment and what it removes or moves out.</summary>
/// <param name="Planned">Equipment the plan puts in the rack, with negative ids; a position left out goes on top when applied.</param>
/// <param name="Removed">Equipment in the rack that the plan removes or moves elsewhere.</param>
public sealed record RackDetail(long Id, string Name, int Units, ObjectRef Site, string? Room, IReadOnlyList<RackEquipment> Equipment,
    IReadOnlyList<RackEquipment> Planned, IReadOnlyList<long> Removed);

/// <summary>
/// A rack for the rack view (#255): its height, the equipment on its units with the models' pictures, and in a plan
/// what the plan changes there. Outside the caller's scope the rack does not exist (#22).
/// </summary>
public sealed class GetRackEndpoint(RequestDb db, GraphHolder holder, PlanViews plans, TypeCatalog catalog) : Endpoint<RackRequest, RackDetail>
{
    public override void Configure() => Get("/racks/{id}");

    public override async Task HandleAsync(RackRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        long siteId;
        string siteCode, siteName, name;
        string? room;
        int units;
        await using (var cmd = db.CreateCommand($"""
            SELECT l.site_id, s.code, s.name, l.name, coalesce(l.rack_units, 42), p.name
            FROM location l JOIN site s ON s.id = l.site_id LEFT JOIN location p ON p.id = l.parent_id AND p.kind = 'room'
            WHERE l.id = $1 AND l.kind = 'rack' AND {ScopeSql.Site("s.id", 2)}
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
            (siteId, siteCode, siteName, name, units, room) = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        }

        var equipment = new List<RackEquipment>();
        await using (var cmd = db.CreateCommand("""
            SELECT e.id, e.name, t.model, t.key, t.category, e.lifecycle::text, e.rack_position, coalesce(t.rack_units, 1)
            FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id
            WHERE e.location_id = $1 AND e.parent_id IS NULL AND e.lifecycle <> 'removed'
            ORDER BY e.rack_position DESC NULLS LAST, e.name
            """))
        {
            cmd.Parameters.Add(new() { Value = req.Id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(3);
                equipment.Add(new RackEquipment(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), key, reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt16(6), reader.GetInt32(7), catalog.Find(key)?.Panel.Images));
            }
        }

        var planned = new List<RackEquipment>();
        var removed = new List<long>();
        if (req.Plan is { } planId)
        {
            if (holder.Current is not { } graph || await plans.GetAsync(graph, planId, scope, ct) is not { } view)
            {
                await Send.NotFoundAsync(ct);
                return;
            }
            var first = await FirstRackAsync(siteId, ct);
            var here = equipment.Select(e => e.Id).ToHashSet();
            foreach (var op in view.Chain.Operations)
            {
                var p = op.Payload;
                switch (op.Kind)
                {
                    case "create_equipment" when !p.TryGetProperty("parent", out var parent) || parent.ValueKind != JsonValueKind.Number:
                        {
                            // The named rack on this site, or the site's first rack when none is named.
                            var rack = Text(p, "rack");
                            if (p.GetProperty("site").GetInt64() != siteId || (rack is null ? first != req.Id : rack != name))
                            {
                                break;
                            }
                            var key = p.GetProperty("typeKey").GetString()!;
                            var type = catalog.Find(key);
                            planned.Add(new RackEquipment(Planned.ObjectId(op.Id), p.GetProperty("name").GetString()!, type?.Model ?? key, key,
                                type?.Category ?? "", "planned", p.TryGetProperty("position", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt32() : null,
                                type?.RackUnits ?? 1, type?.Panel.Images));
                            break;
                        }
                    case "remove" or "move" when Text(p, "type") == "equipment" && p.GetProperty("id").GetInt64() is var id && here.Contains(id):
                        // A move within the rack keeps it here, at its new position; anything else takes it out.
                        if (op.Kind == "move" && p.GetProperty("site").GetInt64() == siteId && Text(p, "rack") == name)
                        {
                            break;
                        }
                        removed.Add(id);
                        break;
                }
            }
        }
        await Send.OkAsync(new RackDetail(req.Id, name, units, new ObjectRef("site", siteId, siteCode, siteName), room, equipment, planned, removed), ct);
    }

    private static string? Text(JsonElement p, string field) =>
        p.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The rack planned equipment without a named rack goes in: the site's first, as the apply picks it.</summary>
    private async Task<long?> FirstRackAsync(long siteId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id FROM location WHERE site_id = $1 ORDER BY (kind = 'rack') DESC, id LIMIT 1");
        cmd.Parameters.Add(new() { Value = siteId });
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }
}
