using System.Text.Json;
using Cmdb.Api.Features.Objects;
using Cmdb.Api.Features.Trace;
using Cmdb.Catalog;
using Npgsql;

namespace Cmdb.Api.Features.Plans;

/// <summary>
/// Names for what a plan creates (#107): planned sites, equipment and cables as object references with their negative
/// ids, and their ports and conductor ends as trace hops, so a diff or a trace in the plan reads like production.
/// </summary>
public sealed class PlannedNames
{
    public Dictionary<long, TraceHop> Terminals { get; } = [];
    public Dictionary<(string Type, long Id), ObjectRef> Objects { get; } = [];

    // Where planned equipment stands and planned cables end, so describing an operation is a lookup, not a scan over
    // every planned terminal (a 10 000-site import has millions, #170).
    private readonly Dictionary<long, ObjectRef> _equipmentSites = [];
    private readonly Dictionary<long, List<string?>> _cableEnds = [];

    public static async Task<PlannedNames> BuildAsync(NpgsqlDataSource db, IReadOnlyList<PlanOp> operations, CancellationToken ct)
    {
        var names = new PlannedNames();
        var creates = operations.Where(o => o.Kind.StartsWith("create_", StringComparison.Ordinal) || o.Kind == "split_cable").ToList();
        if (creates.Count == 0)
        {
            return names;
        }

        // Existing sites the planned equipment and cables sit at.
        var existing = creates.SelectMany(o => o.Kind switch
        {
            "create_equipment" => [o.Payload.GetProperty("site").GetInt64()],
            "create_cable" => new[] { o.Payload.GetProperty("a").GetInt64(), o.Payload.GetProperty("b").GetInt64() },
            "split_cable" => new[] { o.Payload.GetProperty("site").GetInt64(), o.Payload.GetProperty("aSite").GetInt64(), o.Payload.GetProperty("bSite").GetInt64() },
            _ => [],
        }).Where(id => id > 0).Distinct().ToArray();
        var sites = new Dictionary<long, ObjectRef>();
        if (existing.Length > 0)
        {
            await using var cmd = db.CreateCommand("SELECT id, code, name, lifecycle::text FROM site WHERE id = ANY($1)");
            cmd.Parameters.Add(new() { Value = existing });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sites[reader.GetInt64(0)] = new ObjectRef("site", reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
            }
        }

        ObjectRef Site(long id) => id < 0 && names.Objects.TryGetValue(("site", id), out var planned) ? planned
            : sites.GetValueOrDefault(id) ?? new ObjectRef("site", id, $"#{id}");

        foreach (var op in creates)
        {
            var id = Planned.ObjectId(op.Id);
            var p = op.Payload;
            switch (op.Kind)
            {
                case "create_site":
                    names.Objects[("site", id)] = new ObjectRef("site", id, p.GetProperty("code").GetString()!, p.GetProperty("name").GetString(), "planned");
                    break;
                case "create_location":
                    names.Objects[("location", id)] = new ObjectRef("location", id, p.GetProperty("name").GetString()!, p.GetProperty("kind").GetString(), "planned");
                    break;
                case "create_service":
                    names.Objects[("service", id)] = new ObjectRef("service", id, p.GetProperty("code").GetString()!, p.GetProperty("name").GetString(), "planned");
                    break;
                case "create_circuit":
                    names.Objects[("circuit", id)] = new ObjectRef("circuit", id, p.GetProperty("code").GetString()!, p.GetProperty("layer").GetString(), "planned");
                    break;
                case "create_equipment":
                    {
                        var name = p.GetProperty("name").GetString()!;
                        var equipment = new ObjectRef("equipment", id, name, null, "planned");
                        names.Objects[("equipment", id)] = equipment;
                        var site = Site(p.GetProperty("site").GetInt64());
                        names._equipmentSites[id] = site;
                        if (TypeCatalog.Current.Find(p.GetProperty("typeKey").GetString()!) is { } type)
                        {
                            var slot = p.TryGetProperty("slot", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                            foreach (var port in PortExpansion.Expand(type, slot))
                            {
                                var terminal = Planned.Terminal(op.Id, port.Position);
                                names.Terminals[terminal] = new TraceHop(terminal, "port", null, $"{name} · {port.Name} (planerad)", equipment, null, site, null);
                            }
                        }
                        break;
                    }
                case "create_cable":
                    {
                        var typeKey = p.GetProperty("typeKey").GetString()!;
                        var typeName = TypeCatalog.Current.CableTypes.FirstOrDefault(t => t.Key == typeKey)?.Name ?? typeKey;
                        var cable = new ObjectRef("cable", id, $"NY-K{op.Id}", typeName, "planned");
                        names.Objects[("cable", id)] = cable;
                        var (a, b) = (Site(p.GetProperty("a").GetInt64()), Site(p.GetProperty("b").GetInt64()));
                        names._cableEnds[id] = [a.Code, b.Code];
                        for (var k = 1; k <= Planned.ConductorCount(typeKey); k++)
                        {
                            var endA = Planned.Terminal(op.Id, (2 * k) - 1);
                            var endB = Planned.Terminal(op.Id, 2 * k);
                            names.Terminals[endA] = new TraceHop(endA, "conductor_end", null, $"{cable.Code} ledare {k} (A, planerad)", null, cable, a, k);
                            names.Terminals[endB] = new TraceHop(endB, "conductor_end", null, $"{cable.Code} ledare {k} (B, planerad)", null, cable, b, k);
                        }
                        break;
                    }
                case "split_cable":
                    {
                        var typeKey = p.GetProperty("typeKey").GetString()!;
                        var typeName = TypeCatalog.Current.CableTypes.FirstOrDefault(t => t.Key == typeKey)?.Name ?? typeKey;
                        var code = p.GetProperty("code").GetString()!;
                        var partA = new ObjectRef("cable", id, $"{code}-A", typeName, "planned");
                        var partB = new ObjectRef("cable", CableSplit.SecondCable(op.Id), $"{code}-B", typeName, "planned");
                        names.Objects[("cable", partA.Id)] = partA;
                        names.Objects[("cable", partB.Id)] = partB;
                        var (a, middle, b) = (Site(p.GetProperty("aSite").GetInt64()), Site(p.GetProperty("site").GetInt64()), Site(p.GetProperty("bSite").GetInt64()));
                        var conductors = CableSplit.Conductors(p);
                        for (var i = 0; i < conductors.Count; i++)
                        {
                            var (k, number) = (i + 1, conductors[i].Number);
                            void Name(long terminal, ObjectRef cable, ObjectRef at, string side) =>
                                names.Terminals[terminal] = new TraceHop(terminal, "conductor_end", null, $"{cable.Code} ledare {number} ({side}, planerad)", null, cable, at, number);
                            Name(CableSplit.OuterA(op.Id, k), partA, a, "A");
                            Name(CableSplit.InnerA(op.Id, k), partA, middle, "B");
                            Name(CableSplit.InnerB(op.Id, conductors.Count, k), partB, middle, "A");
                            Name(CableSplit.OuterB(op.Id, conductors.Count, k), partB, b, "B");
                        }
                        break;
                    }
                default:
                    break;
            }
        }
        return names;
    }

    /// <summary>The operation in words, for creates.</summary>
    public (string Summary, ObjectRef Target) Describe(PlanOp op)
    {
        var id = Planned.ObjectId(op.Id);
        var p = op.Payload;
        switch (op.Kind)
        {
            case "create_site":
                {
                    var site = Objects[("site", id)];
                    return ($"Ny site {site.Code} {site.Name} ({p.GetProperty("siteType").GetString()})", site);
                }
            case "create_location" or "create_service" or "create_circuit":
                {
                    var type = op.Kind["create_".Length..];
                    var created = Objects[(type, id)];
                    return (NetworkLinks.Text(op, (t, x) => Objects.TryGetValue((t, x), out var o) ? o.Code : $"#{x}"), created);
                }
            case "create_equipment":
                {
                    var equipment = Objects[("equipment", id)];
                    var model = TypeCatalog.Current.Find(p.GetProperty("typeKey").GetString()!)?.Model ?? p.GetProperty("typeKey").GetString();
                    var site = _equipmentSites.GetValueOrDefault(id);
                    return ($"Ny utrustning {equipment.Code} ({model}){(site is null ? "" : $" på {site.Code}")}", equipment);
                }
            default:
                {
                    var cable = Objects[("cable", id)];
                    var ends = _cableEnds.GetValueOrDefault(id) ?? [];
                    return ($"Ny kabel {cable.Code} ({cable.Name}) {string.Join(" – ", ends)}", cable);
                }
        }
    }
}
