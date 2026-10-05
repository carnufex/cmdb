using Cmdb.Api.Auth;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Plans;
using Cmdb.Catalog;
using Cmdb.Graph;
using FastEndpoints;
using Npgsql;

namespace Cmdb.Api.Features.Classifications;

/// <param name="Kind">direct (set on the object), contains (something inside it has the level) or carries (a service
/// with the level runs through it).</param>
public sealed record DerivedReason(string Kind, ObjectRef Subject, int Level);

/// <param name="LocationLevels">For a site: the level each of its locations (rack, room, building) gets from the equipment inside.</param>
public sealed record DerivedClassification(string Schema, int Level, string Name, bool Critical, bool Inherited, IReadOnlyList<DerivedReason> Reasons,
    int Services, IReadOnlyList<DerivedLocation> LocationLevels, double ElapsedMs);

public sealed record DerivedLocation(long Id, int Level, string Because);

/// <summary>
/// The classification an object has once what it contains and what it carries is counted (#177, ADR-0017): the highest of
/// its own level, the levels of equipment inside it (a rack, room, building and site take the highest of their equipment) and
/// the levels of the services that run through it (what carries a classified service must support it, so a cable or a
/// site takes the highest of the services its failure would hit). Worked out on demand from the graph view, so it follows
/// deltas and plan views, and a plan's own classification changes count in its view.
/// </summary>
public sealed class ClassificationDerivation(RequestDb db, GraphHolder holder, ScopeMasks masks, PlanViews views)
{
    private const int MaxReasons = 5;

    public async Task<DerivedClassification?> DeriveAsync(UserScope scope, string type, long id, string schemaKey, long? planId, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (ClassificationCatalog.Embedded.Find(schemaKey) is not { } schema || !schema.AppliesTo.Contains(type) || holder.Current is not { } production)
        {
            return null;
        }
        var graph = production;
        var overrides = new Dictionary<(string, long), int?>();
        IReadOnlyList<PlanOp> chain = [];
        if (planId is { } plan)
        {
            if (await views.GetAsync(production, plan, scope, ct) is not { } view)
            {
                return null;
            }
            graph = view.Graph;
            chain = view.Chain.Operations;
            foreach (var op in view.Chain.Operations.Where(o => o.Kind == "set_classification" && o.Payload.GetProperty("schema").GetString() == schemaKey))
            {
                overrides[(op.Payload.GetProperty("type").GetString()!, op.Payload.GetProperty("id").GetInt64())] =
                    op.Payload.TryGetProperty("level", out var l) && l.ValueKind == System.Text.Json.JsonValueKind.Number ? l.GetInt32() : null;
            }
        }
        var mask = await masks.GetAsync(graph, scope, ct);

        // What the object's failure would hit, and the equipment inside it.
        int[] services = [];
        var inside = new List<long>();
        switch (type)
        {
            case "site" when graph.TryGetSite(id, out var site) && mask.SiteVisible(site):
                services = GraphImpact.OfSite(graph, site).Services;
                inside = await EquipmentAtAsync(id, ct);
                // Equipment the plan puts there counts too (#179).
                inside.AddRange(chain.Where(o => o.Kind == "create_equipment" && o.Payload.GetProperty("site").GetInt64() == id).Select(o => Planned.ObjectId(o.Id)));
                break;
            case "equipment" when graph.TryGetEquipment(id, out var equipment) && mask.SiteVisible(graph.SiteIndexOfEquipment(equipment)):
                services = GraphImpact.OfEquipment(graph, equipment).Services;
                break;
            case "cable" when graph.TryGetCable(id, out var cable) && mask.CableVisible(cable):
                services = GraphImpact.OfCable(graph, cable).Services;
                break;
            case "service":
                break;
            default:
                return null;
        }
        var serviceIds = services.Where(mask.ServiceVisible).Select(graph.ServiceId).ToArray();

        var levels = await LevelsAsync(schemaKey, serviceIds, [.. inside, .. type == "equipment" ? [id] : Array.Empty<long>()], type, id, overrides, ct);
        var reasons = new List<DerivedReason>();
        int Add(string kind, string objectType, long objectId, int level)
        {
            reasons.Add(new DerivedReason(kind, new ObjectRef(objectType, objectId, $"#{objectId}"), level));
            return level;
        }
        var best = 0;
        if (levels.TryGetValue((type, id), out var own))
        {
            best = Math.Max(best, Add("direct", type, id, own));
        }
        foreach (var e in inside)
        {
            if (levels.TryGetValue(("equipment", e), out var level) && !(type == "equipment" && e == id))
            {
                best = Math.Max(best, Add("contains", "equipment", e, level));
            }
        }
        foreach (var s in serviceIds)
        {
            if (levels.TryGetValue(("service", s), out var level) && !(type == "service" && s == id))
            {
                best = Math.Max(best, Add("carries", "service", s, level));
            }
        }
        var top = reasons.Where(r => r.Level == best).OrderBy(r => r.Kind == "direct" ? 0 : 1).ThenBy(r => r.Subject.Id).Take(MaxReasons).ToList();
        var named = await NamesAsync(top, chain, ct);
        var locations = type == "site" ? await LocationLevelsAsync(id, schemaKey, overrides, ct) : [];
        var inherited = best > 0 && !(levels.TryGetValue((type, id), out var direct) && direct == best);
        return new DerivedClassification(schemaKey, best, schema.Level(best)?.Name ?? (best == 0 ? "Ingen" : $"Nivå {best}"), best >= schema.CriticalFrom && best > 0,
            inherited, named, serviceIds.Length, locations, Math.Round(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1));
    }

    private async Task<List<long>> EquipmentAtAsync(long site, CancellationToken ct)
    {
        await using var cmd = db.Source.CreateCommand("SELECT id FROM equipment WHERE site_id = $1 AND lifecycle <> 'removed'");
        cmd.Parameters.Add(new() { Value = site });
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>Direct levels of the given services, equipment and the object itself, with a plan's changes applied.</summary>
    private async Task<Dictionary<(string, long), int>> LevelsAsync(string schemaKey, long[] services, long[] equipment, string type, long id,
        Dictionary<(string, long), int?> overrides, CancellationToken ct)
    {
        var levels = new Dictionary<(string, long), int>();
        await using (var cmd = db.Source.CreateCommand("""
            SELECT object_type, object_id, level FROM classification
            WHERE schema_key = $1 AND ((object_type = 'service' AND object_id = ANY($2)) OR (object_type = 'equipment' AND object_id = ANY($3))
                                       OR (object_type = $4 AND object_id = $5))
            """))
        {
            cmd.Parameters.Add(new() { Value = schemaKey });
            cmd.Parameters.Add(new() { Value = services });
            cmd.Parameters.Add(new() { Value = equipment });
            cmd.Parameters.Add(new() { Value = type });
            cmd.Parameters.Add(new() { Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                levels[(reader.GetString(0), reader.GetInt64(1))] = reader.GetInt32(2);
            }
        }
        var relevant = new HashSet<(string, long)>([.. services.Select(s => ("service", s)), .. equipment.Select(e => ("equipment", e)), (type, id)]);
        foreach (var (key, level) in overrides.Where(o => relevant.Contains(o.Key)))
        {
            if (level is { } l)
            {
                levels[key] = l;
            }
            else
            {
                levels.Remove(key);
            }
        }
        return levels;
    }

    private async Task<List<DerivedReason>> NamesAsync(List<DerivedReason> reasons, IReadOnlyList<PlanOp> chain, CancellationToken ct)
    {
        if (reasons.Count == 0)
        {
            return reasons;
        }
        // What the plan creates has no row yet: named from its operations.
        var plannedNames = new Dictionary<(string, long), string>();
        foreach (var op in chain.Where(o => o.Kind is "create_site" or "create_equipment"))
        {
            plannedNames[(op.Kind == "create_site" ? "site" : "equipment", Planned.ObjectId(op.Id))] =
                op.Kind == "create_site" ? op.Payload.GetProperty("code").GetString()! : op.Payload.GetProperty("name").GetString()!;
        }
        await using var cmd = db.Source.CreateCommand("""
            SELECT 'service', id, code, name FROM service WHERE id = ANY($1)
            UNION ALL SELECT 'equipment', id, name, NULL FROM equipment WHERE id = ANY($2)
            UNION ALL SELECT 'site', id, code, name FROM site WHERE id = ANY($3)
            UNION ALL SELECT 'cable', id, code, NULL FROM cable WHERE id = ANY($4)
            """);
        foreach (var kind in new[] { "service", "equipment", "site", "cable" })
        {
            cmd.Parameters.Add(new() { Value = reasons.Where(r => r.Subject.Type == kind).Select(r => r.Subject.Id).ToArray() });
        }
        var names = new Dictionary<(string, long), (string Code, string? Name)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                names[(reader.GetString(0), reader.GetInt64(1))] = (reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
            }
        }
        return [.. reasons.Select(r => names.TryGetValue((r.Subject.Type, r.Subject.Id), out var n)
            ? r with { Subject = new ObjectRef(r.Subject.Type, r.Subject.Id, n.Code, n.Name) }
            : plannedNames.TryGetValue((r.Subject.Type, r.Subject.Id), out var planned)
                ? r with { Subject = new ObjectRef(r.Subject.Type, r.Subject.Id, planned, null, "planned") } : r)];
    }

    /// <summary>The level each location of the site gets from the equipment inside it, rolled up through its parents.</summary>
    private async Task<List<DerivedLocation>> LocationLevelsAsync(long site, string schemaKey, Dictionary<(string, long), int?> overrides, CancellationToken ct)
    {
        var parent = new Dictionary<long, long?>();
        await using (var cmd = db.Source.CreateCommand("SELECT id, parent_id FROM location WHERE site_id = $1"))
        {
            cmd.Parameters.Add(new() { Value = site });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                parent[reader.GetInt64(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
        }
        var inLocation = new List<(long Location, long Equipment, string Name, int Level)>();
        await using (var cmd = db.Source.CreateCommand("""
            SELECT e.location_id, e.id, e.name, coalesce(c.level, 0) FROM equipment e
            LEFT JOIN classification c ON c.object_type = 'equipment' AND c.object_id = e.id AND c.schema_key = $2
            WHERE e.site_id = $1 AND e.location_id IS NOT NULL AND e.lifecycle <> 'removed'
            """))
        {
            cmd.Parameters.Add(new() { Value = site });
            cmd.Parameters.Add(new() { Value = schemaKey });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var equipment = reader.GetInt64(1);
                var level = overrides.TryGetValue(("equipment", equipment), out var changed) ? changed ?? 0 : reader.GetInt32(3);
                if (level > 0)
                {
                    inLocation.Add((reader.GetInt64(0), equipment, reader.GetString(2), level));
                }
            }
        }
        var best = new Dictionary<long, (int Level, string Because)>();
        foreach (var (location, _, name, level) in inLocation)
        {
            for (long? at = location; at is { } l && parent.ContainsKey(l); at = parent[l])
            {
                if (!best.TryGetValue(l, out var current) || level > current.Level)
                {
                    best[l] = (level, name);
                }
            }
        }
        return [.. best.OrderBy(b => b.Key).Select(b => new DerivedLocation(b.Key, b.Value.Level, b.Value.Because))];
    }
}

public sealed class DerivedClassificationRequest
{
    public string Type { get; set; } = "";
    public long Id { get; set; }
    public string Schema { get; set; } = "criticality";

    /// <summary>The plan whose view to derive in; its own classification changes count.</summary>
    public long? Plan { get; set; }
}

/// <summary>An object's derived classification (#177), within the caller's scopes. Nothing for an object they cannot see.</summary>
public sealed class DerivedClassificationEndpoint(RequestDb db, ClassificationDerivation derivation) : Endpoint<DerivedClassificationRequest, DerivedClassification>
{
    public override void Configure() => Get("/classifications/derived");

    public override async Task HandleAsync(DerivedClassificationRequest req, CancellationToken ct)
    {
        var scope = HttpContext.Scope();
        if (!ClassificationCatalog.ObjectTypes.Contains(req.Type) || !await PlanSql.ObjectVisibleAsync(db.Source, req.Type, req.Id, scope, ct)
            || await derivation.DeriveAsync(scope, req.Type, req.Id, req.Schema, req.Plan, ct) is not { } derived)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(derived, ct);
    }
}
