using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Api.Auth;
using Cmdb.Catalog;
using Cmdb.Graph;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>
/// The plan operations for what reconciliation (#230) needs besides sites, equipment and cables: locations, services,
/// circuits with their path, the links between circuits (dependencies) and between services and circuits, and cards
/// (equipment in a slot). They are checked like every other operation, change the plan's view of the graph where the graph
/// holds them, and are written by the apply.
/// </summary>
public static class NetworkLinks
{
    public static readonly string[] Kinds = ["create_location", "create_service", "create_circuit", "set_circuit_path", "link_circuit", "link_service"];

    public static readonly string[] LocationKinds = ["building", "room", "rack", "position"];

    private static readonly JsonSerializerOptions OmitNull = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static CircuitLayer? Layer(string? layer) => layer switch
    {
        "physical" => CircuitLayer.Physical,
        "transmission" => CircuitLayer.Transmission,
        "logical" => CircuitLayer.Logical,
        _ => null,
    };

    public static long[] Hops(JsonElement p) => p.TryGetProperty("hops", out var h) && h.ValueKind == JsonValueKind.Array
        ? [.. h.EnumerateArray().Select(x => x.GetInt64())] : [];

    /// <summary>A new circuit in the plan's view: its path; carriers and services come with link operations.</summary>
    public static GraphChange NewCircuit(PlanOp op) => new GraphCircuitsChange(
        [new GraphCircuit(Planned.ObjectId(op.Id), Layer(op.Payload.GetProperty("layer").GetString()) ?? CircuitLayer.Physical, Hops(op.Payload), [], [])], []);

    /// <summary>
    /// A path or link change worked out against the view so far: the circuit as it is there, with the change made. Null
    /// when the circuit is not in the view. <paramref name="mapped"/> gives the id a planned circuit has after an apply.
    /// </summary>
    public static GraphChange? CircuitChange(Cmdb.Graph.Graph g, PlanOp op, Func<long, long> mapped)
    {
        var p = op.Payload;
        var circuitId = op.Kind switch
        {
            "link_service" => p.GetProperty("circuit").GetInt64(),
            "link_circuit" => p.GetProperty("circuit").GetInt64(),
            _ => p.GetProperty("id").GetInt64(),
        };
        if (!g.TryGetCircuit(mapped(circuitId), out var c))
        {
            return null;
        }
        var hops = g.HopsOf(c).ToArray().Select(g.TerminalId).ToList();
        var carriers = g.CarriersOf(c).ToArray().Select(g.CircuitId).ToList();
        var services = g.ServicesOf(c).ToArray().Select(g.ServiceId).ToList();
        var remove = p.TryGetProperty("remove", out var r) && r.ValueKind == JsonValueKind.True;
        switch (op.Kind)
        {
            case "set_circuit_path":
                hops = [.. Hops(p)];
                break;
            case "link_circuit":
                {
                    var carrier = mapped(p.GetProperty("carrier").GetInt64());
                    carriers.Remove(carrier);
                    if (!remove)
                    {
                        carriers.Add(carrier);
                    }
                    break;
                }
            default:
                {
                    var service = mapped(p.GetProperty("service").GetInt64());
                    services.Remove(service);
                    if (!remove)
                    {
                        services.Add(service);
                    }
                    break;
                }
        }
        return new GraphCircuitsChange([new GraphCircuit(g.CircuitId(c), g.LayerOf(c), hops, carriers, services)], []);
    }

    /// <summary>Planned ids an operation refers to.</summary>
    public static IEnumerable<long> References(PlanOp op)
    {
        var p = op.Payload;
        IEnumerable<long> Fields(params string[] names) => names.Where(n => p.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
            .Select(n => p.GetProperty(n).GetInt64());
        return (op.Kind switch
        {
            "create_location" => Fields("site", "parent"),
            "create_circuit" => Hops(p),
            "set_circuit_path" => Fields("id").Concat(Hops(p)),
            "link_circuit" => Fields("circuit", "carrier"),
            "link_service" => Fields("service", "circuit"),
            _ => [],
        }).Where(id => id < 0);
    }

    /// <summary>The payload fields holding ids, for rewriting planned ids to production's after an apply.</summary>
    public static string[] IdFields(string kind) => kind switch
    {
        "create_location" => ["site", "parent"],
        "set_circuit_path" => ["id"],
        "link_circuit" => ["circuit", "carrier"],
        "link_service" => ["service", "circuit"],
        _ => [],
    };

    /// <summary>Checks one of the new operations against the plan's view and gives its payload, or why not.</summary>
    internal static async Task<(string? Payload, string? Error)> CheckAsync(NpgsqlDataSource db, UserScope scope, PlanView view, GraphMask mask,
        AddOperationRequest req, CancellationToken ct)
    {
        var chain = view.Chain.Operations;
        bool Planned(string kind, long id) => chain.Any(o => o.Kind == kind && Plans.Planned.ObjectId(o.Id) == id);
        async Task<bool> Visible(string type, long id) => await PlanSql.ObjectVisibleAsync(db, type, id, scope, ct);
        async Task<string?> HopsProblem(long[] hops)
        {
            if (hops.Length < 2)
            {
                return "En krets har minst två hopp.";
            }
            foreach (var t in hops)
            {
                if (!view.Graph.TryGetNode(t, out var node) || !PlanWrites.NodeVisible(view.Graph, mask, node))
                {
                    return $"Terminal {t} finns inte.";
                }
            }
            await Task.CompletedTask;
            return null;
        }
        bool CircuitVisible(long id) => view.Graph.TryGetCircuit(id, out var c) && (id < 0 || mask.CircuitVisible(c));

        switch (req.Kind)
        {
            case "create_location":
                {
                    var site = req.SiteId!.Value;
                    if (site < 0 ? !Planned("create_site", site) : !await Visible("site", site))
                    {
                        return (null, $"Site {site} finns inte.");
                    }
                    if (req.ParentId is { } parent)
                    {
                        var ok = parent < 0
                            ? chain.Any(o => o.Kind == "create_location" && Plans.Planned.ObjectId(o.Id) == parent && o.Payload.GetProperty("site").GetInt64() == site)
                            : await ExistsAsync(db, "SELECT EXISTS (SELECT 1 FROM location WHERE id = $1 AND site_id = $2)", ct, parent, site);
                        if (!ok)
                        {
                            return (null, $"Platsen {parent} finns inte på siten.");
                        }
                    }
                    return (JsonSerializer.Serialize(new
                    {
                        site,
                        parent = req.ParentId,
                        kind = req.LocationKind,
                        name = req.Name!.Trim(),
                        rackUnits = req.RackUnits,
                    }, OmitNull), null);
                }
            case "create_service":
                {
                    var code = req.Code!.Trim();
                    if (chain.Any(o => o.Kind == "create_service" && o.Payload.GetProperty("code").GetString() == code)
                        || await ExistsAsync(db, "SELECT EXISTS (SELECT 1 FROM service WHERE code = $1)", ct, code))
                    {
                        return (null, $"Koden {code} används redan.");
                    }
                    if (req.Attributes is { ValueKind: JsonValueKind.Object } a
                        && PlannedAttributes.Problems("service", req.ServiceType!, a) is { Count: > 0 } problems)
                    {
                        return (null, $"Attributen passar inte typens schema: {string.Join("; ", problems)}");
                    }
                    return (JsonSerializer.Serialize(new
                    {
                        code,
                        name = req.Name!.Trim(),
                        serviceType = req.ServiceType,
                        attributes = req.Attributes is { ValueKind: JsonValueKind.Object } at ? JsonNode.Parse(at.GetRawText()) : null,
                    }, OmitNull), null);
                }
            case "create_circuit":
                {
                    var code = req.Code!.Trim();
                    if (chain.Any(o => o.Kind == "create_circuit" && o.Payload.GetProperty("code").GetString() == code)
                        || await ExistsAsync(db, "SELECT EXISTS (SELECT 1 FROM circuit WHERE code = $1)", ct, code))
                    {
                        return (null, $"Koden {code} används redan.");
                    }
                    if (await HopsProblem(req.Hops!) is { } hopProblem)
                    {
                        return (null, hopProblem);
                    }
                    return (JsonSerializer.Serialize(new { code, layer = req.Layer, hops = req.Hops }), null);
                }
            case "set_circuit_path":
                {
                    if (!CircuitVisible(req.ObjectId!.Value))
                    {
                        return (null, $"Krets {req.ObjectId} finns inte.");
                    }
                    if (await HopsProblem(req.Hops!) is { } hopProblem)
                    {
                        return (null, hopProblem);
                    }
                    return (JsonSerializer.Serialize(new { type = "circuit", id = req.ObjectId, hops = req.Hops }), null);
                }
            case "link_circuit":
                {
                    if (!CircuitVisible(req.ObjectId!.Value) || !CircuitVisible(req.CarrierId!.Value))
                    {
                        return (null, "Kretsen eller bäraren finns inte.");
                    }
                    if (req.ObjectId == req.CarrierId)
                    {
                        return (null, "En krets kan inte gå på sig själv.");
                    }
                    return (JsonSerializer.Serialize(new { type = "circuit", circuit = req.ObjectId, carrier = req.CarrierId, remove = req.Remove }), null);
                }
            default:
                {
                    var service = req.ObjectId!.Value;
                    if (service < 0 ? !Planned("create_service", service) : !await Visible("service", service))
                    {
                        return (null, $"Tjänst {service} finns inte.");
                    }
                    if (!CircuitVisible(req.CircuitId!.Value))
                    {
                        return (null, $"Krets {req.CircuitId} finns inte.");
                    }
                    return (JsonSerializer.Serialize(new { type = "service", service, circuit = req.CircuitId, remove = req.Remove }), null);
                }
        }
    }

    /// <summary>
    /// Checks a card (equipment in a slot, #230) and gives the site it stands at, or why not: the parent exists, has the
    /// slot, the slot takes the card's category and is free in production and in the plan.
    /// </summary>
    internal static async Task<(long? Site, string? Error)> CardAsync(NpgsqlDataSource db, UserScope scope, PlanView view, long parent, string slot,
        string typeKey, CancellationToken ct)
    {
        var card = TypeCatalog.Current.Find(typeKey);
        if (card is null || !TypeCatalog.Current.CategoryHas(card.Category, CatalogRoles.Card))
        {
            return (null, "Modellen är inget kort.");
        }
        string parentType;
        long site;
        if (parent < 0)
        {
            if (view.Chain.Operations.FirstOrDefault(o => o.Kind == "create_equipment" && Planned.ObjectId(o.Id) == parent) is not { } planned)
            {
                return (null, $"Den planerade utrustningen {parent} finns inte i planen.");
            }
            (parentType, site) = (planned.Payload.GetProperty("typeKey").GetString()!, planned.Payload.GetProperty("site").GetInt64());
        }
        else
        {
            if (!await PlanSql.ObjectVisibleAsync(db, "equipment", parent, scope, ct))
            {
                return (null, $"Utrustning {parent} finns inte.");
            }
            await using var cmd = db.CreateCommand("""
                SELECT t.key, e.site_id, EXISTS (SELECT 1 FROM equipment c WHERE c.parent_id = e.id AND c.slot = $2 AND c.lifecycle <> 'removed')
                FROM equipment e JOIN equipment_type t ON t.id = e.equipment_type_id WHERE e.id = $1
                """);
            cmd.Parameters.Add(new() { Value = parent });
            cmd.Parameters.Add(new() { Value = slot });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            (parentType, site) = (reader.GetString(0), reader.GetInt64(1));
            if (reader.GetBoolean(2))
            {
                return (null, $"Slot {slot} är upptagen.");
            }
        }
        if (view.Chain.Operations.Any(o => o.Kind == "create_equipment" && o.Payload.TryGetProperty("parent", out var pa) && pa.ValueKind == JsonValueKind.Number
            && pa.GetInt64() == parent && o.Payload.GetProperty("slot").GetString() == slot))
        {
            return (null, $"Slot {slot} tas redan i planen.");
        }
        var template = TypeCatalog.Current.Find(parentType)?.SlotList.FirstOrDefault(s => s.Name == slot);
        if (template is null)
        {
            return (null, $"Utrustningen har ingen slot {slot}.");
        }
        if (!template.Accepts.Contains(card.Category, StringComparer.Ordinal))
        {
            return (null, $"Slot {slot} tar inte kategorin {card.Category}.");
        }
        return (site, null);
    }

    private static async Task<bool> ExistsAsync(NpgsqlDataSource db, string sql, CancellationToken ct, params object[] values)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var v in values)
        {
            cmd.Parameters.Add(new() { Value = v });
        }
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The operation in words.</summary>
    public static string Text(PlanOp op, Func<string, long, string> name)
    {
        var p = op.Payload;
        var remove = p.TryGetProperty("remove", out var r) && r.ValueKind == JsonValueKind.True;
        return op.Kind switch
        {
            "create_location" => $"Ny plats {p.GetProperty("name").GetString()} ({p.GetProperty("kind").GetString()}) på {name("site", p.GetProperty("site").GetInt64())}",
            "create_service" => $"Ny tjänst {p.GetProperty("code").GetString()} {p.GetProperty("name").GetString()}",
            "create_circuit" => $"Ny krets {p.GetProperty("code").GetString()} ({p.GetProperty("layer").GetString()}, {Hops(p).Length} hopp)",
            "set_circuit_path" => $"Ny väg för krets {name("circuit", p.GetProperty("id").GetInt64())} ({Hops(p).Length} hopp)",
            "link_circuit" => remove
                ? $"Krets {name("circuit", p.GetProperty("circuit").GetInt64())} går inte längre på {name("circuit", p.GetProperty("carrier").GetInt64())}"
                : $"Krets {name("circuit", p.GetProperty("circuit").GetInt64())} går på {name("circuit", p.GetProperty("carrier").GetInt64())}",
            _ => remove
                ? $"Tjänst {name("service", p.GetProperty("service").GetInt64())} går inte längre på krets {name("circuit", p.GetProperty("circuit").GetInt64())}"
                : $"Tjänst {name("service", p.GetProperty("service").GetInt64())} går på krets {name("circuit", p.GetProperty("circuit").GetInt64())}",
        };
    }
}
