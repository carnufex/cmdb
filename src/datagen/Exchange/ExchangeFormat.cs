using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cmdb.Catalog;
using Cmdb.DataGen.Geo;

namespace Cmdb.DataGen.Exchange;

// The exchange format for importing an existing network into production (#210): one CSV per kind of object, in a
// folder. Objects refer to each other by their id in the source system ("id" columns), never by cmdb's ids, so the
// same files can be imported again and match what is already there. See docs/import.md.

/// <summary>A problem with one row of one file, reported before anything is written.</summary>
internal sealed record ImportError(string File, int Row, string Object, string Message)
{
    public override string ToString() => Row > 0 ? $"{File}:{Row} {Object}: {Message}" : $"{File}: {Message}";
}

internal sealed record XSite(int Row, string Id, string Code, string Name, string SiteType, double X, double Y, string Lifecycle, string Attributes);

internal sealed record XLocation(int Row, string Id, string Site, string? Parent, string Kind, string Name, short? RackUnits);

internal sealed record XEquipment(int Row, string Id, string Site, string? Location, string? Parent, string? Slot, string Name, string Type,
    string Lifecycle, short? RackPosition, string Attributes);

internal sealed record XPort(int Row, string Equipment, string Name);

/// <param name="Route">The cable's route in SWEREF 99 TM as x, y pairs; null runs it straight between its sites.</param>
internal sealed record XCable(int Row, string Id, string Code, string Type, string A, string B, string Lifecycle, double[]? Route, string Attributes);

/// <summary>A terminal: a port on equipment, or one end of a conductor in a cable.</summary>
internal readonly record struct XTerminal(string? Equipment, string? Port, string? Cable, int Conductor, char Side)
{
    public bool IsPort => Equipment is not null;

    public override string ToString() => IsPort ? $"{Equipment} {Port}" : $"{Cable} ledare {Conductor} {Side}";
}

internal sealed record XConnection(int Row, XTerminal A, XTerminal B, string Kind, string Lifecycle);

internal sealed record XCircuit(int Row, string Id, string Code, string Layer, string Lifecycle);

internal sealed record XHop(int Row, string Circuit, int Seq, XTerminal Terminal, string? ChannelKind, int? ChannelNumber);

internal sealed record XDependency(int Row, string Circuit, string Carrier);

internal sealed record XService(int Row, string Id, string Code, string Name, string Type, string Lifecycle, string Attributes);

internal sealed record XServiceCircuit(int Row, string Service, string Circuit);

/// <summary>Everything in an exchange folder.</summary>
internal sealed class ExchangeData
{
    /// <summary>The files the folder has. A links file that is there, even empty, is the whole truth for those links.</summary>
    public HashSet<string> Files { get; } = new(StringComparer.Ordinal);

    public List<XSite> Sites { get; } = [];
    public List<XLocation> Locations { get; } = [];
    public List<XEquipment> Equipment { get; } = [];
    public List<XPort> Ports { get; } = [];
    public List<XCable> Cables { get; } = [];
    public List<XConnection> Connections { get; } = [];
    public List<XCircuit> Circuits { get; } = [];
    public List<XHop> Hops { get; } = [];
    public List<XDependency> Dependencies { get; } = [];
    public List<XService> Services { get; } = [];
    public List<XServiceCircuit> ServiceCircuits { get; } = [];
}

internal static class ExchangeFormat
{
    public const string Sites = "sites.csv";
    public const string Locations = "locations.csv";
    public const string Equipment = "equipment.csv";
    public const string Ports = "ports.csv";
    public const string Cables = "cables.csv";
    public const string Connections = "connections.csv";
    public const string Circuits = "circuits.csv";
    public const string Hops = "circuit-hops.csv";
    public const string Dependencies = "circuit-dependencies.csv";
    public const string Services = "services.csv";
    public const string ServiceCircuits = "service-circuits.csv";

    public static readonly string[] Lifecycles = ["planned", "under_construction", "in_service", "decommissioning", "removed"];
    public static readonly string[] LocationKinds = ["building", "room", "rack", "position"];
    public static readonly string[] ConnectionKinds = ["patch", "splice", "termination", "internal"];
    public static readonly string[] Layers = ["physical", "transmission", "logical"];
    public static readonly string[] ChannelKinds = ["wavelength", "timeslot", "vlan"];

    // The map's extent in SWEREF 99 TM, as the API's tile grid.
    private const double MinX = -1_200_000, MaxX = 1_800_000, MinY = 5_500_000, MaxY = 8_500_000;

    /// <summary>
    /// Reads every file in <paramref name="folder"/> that exists and checks each row on its own and against the catalog:
    /// required fields, known types, lifecycles and kinds, positions in the map, attributes that fit the type's schema
    /// and ids used once per file. References between objects are checked by the import, which also knows what is
    /// already in the database.
    /// </summary>
    public static ExchangeData Read(string folder, TypeCatalog catalog, List<ImportError> errors)
    {
        var data = new ExchangeData();
        CsvTable? Table(string file)
        {
            var path = Path.Combine(folder, file);
            if (!File.Exists(path))
            {
                return null;
            }
            data.Files.Add(file);
            return CsvTable.Parse(file, File.ReadAllText(path));
        }

        if (Table(Sites) is { } sites)
        {
            Rows(sites, errors, (row, cell, fail) =>
            {
                var (id, code, name, siteType) = (cell("id"), cell("code"), cell("name"), cell("siteType"));
                Required(fail, ("id", id), ("code", code), ("name", name), ("siteType", siteType));
                if (siteType.Length > 0 && catalog.FindSiteType(siteType) is null)
                {
                    fail($"okänd sitetyp '{siteType}' ({string.Join(", ", catalog.SiteTypes.Select(t => t.Key))})");
                }
                var position = Position(cell, fail);
                var attributes = Attributes(catalog, "site", siteType, cell, fail);
                if (id.Length > 0 && position is var (x, y))
                {
                    data.Sites.Add(new XSite(row, id, code, name, siteType, x, y, Lifecycle(cell, fail), attributes));
                }
            });
            Unique(Sites, data.Sites, s => s.Id, s => s.Row, "id", errors);
            Unique(Sites, data.Sites, s => s.Code, s => s.Row, "code", errors);
        }

        if (Table(Locations) is { } locations)
        {
            Rows(locations, errors, (row, cell, fail) =>
            {
                var (id, site, kind, name) = (cell("id"), cell("site"), cell("kind"), cell("name"));
                Required(fail, ("id", id), ("site", site), ("kind", kind), ("name", name));
                if (kind.Length > 0 && !LocationKinds.Contains(kind))
                {
                    fail($"kind är en av {string.Join(", ", LocationKinds)}");
                }
                var units = Short(cell("rackUnits"), "rackUnits", fail);
                data.Locations.Add(new XLocation(row, id, site, Optional(cell("parent")), kind, name, units));
            });
            Unique(Locations, data.Locations, l => l.Id, l => l.Row, "id", errors);
        }

        if (Table(Equipment) is { } equipment)
        {
            Rows(equipment, errors, (row, cell, fail) =>
            {
                var (id, site, name, typeKey) = (cell("id"), cell("site"), cell("name"), cell("type"));
                Required(fail, ("id", id), ("site", site), ("name", name), ("type", typeKey));
                var type = typeKey.Length > 0 ? catalog.Find(typeKey) : null;
                if (typeKey.Length > 0 && type is null)
                {
                    fail($"okänd modell '{typeKey}' i katalogen");
                }
                var (location, parent, slot) = (Optional(cell("location")), Optional(cell("parent")), Optional(cell("slot")));
                if ((parent is null) != (slot is null))
                {
                    fail("parent och slot anges tillsammans");
                }
                if ((location is null) == (parent is null))
                {
                    fail("utrustning sitter antingen i en location eller i en slot (parent)");
                }
                if (type is not null && parent is not null != catalog.CategoryHas(type.Category, CatalogRoles.Card))
                {
                    fail(parent is null ? $"'{typeKey}' är ett kort och sitter i en slot" : $"'{typeKey}' är inget kort och kan inte sitta i en slot");
                }
                var position = Short(cell("rackPosition"), "rackPosition", fail);
                var attributes = Attributes(catalog, "equipment", typeKey, cell, fail);
                data.Equipment.Add(new XEquipment(row, id, site, location, parent, slot, name, typeKey, Lifecycle(cell, fail), position, attributes));
            });
            Unique(Equipment, data.Equipment, e => e.Id, e => e.Row, "id", errors);
        }

        if (Table(Ports) is { } ports)
        {
            Rows(ports, errors, (row, cell, fail) =>
            {
                var (equipmentId, name) = (cell("equipment"), cell("name"));
                Required(fail, ("equipment", equipmentId), ("name", name));
                data.Ports.Add(new XPort(row, equipmentId, name));
            });
        }

        if (Table(Cables) is { } cables)
        {
            Rows(cables, errors, (row, cell, fail) =>
            {
                var (id, code, typeKey, a, b) = (cell("id"), cell("code"), cell("cableType"), cell("a"), cell("b"));
                Required(fail, ("id", id), ("code", code), ("cableType", typeKey), ("a", a), ("b", b));
                if (typeKey.Length > 0 && catalog.FindCable(typeKey) is null)
                {
                    fail($"okänd kabeltyp '{typeKey}' i katalogen");
                }
                if (a.Length > 0 && a == b)
                {
                    fail("a och b är två olika siter");
                }
                var route = Route(cell("route"), fail);
                var attributes = Attributes(catalog, "cable", typeKey, cell, fail);
                data.Cables.Add(new XCable(row, id, code, typeKey, a, b, Lifecycle(cell, fail), route, attributes));
            });
            Unique(Cables, data.Cables, c => c.Id, c => c.Row, "id", errors);
            Unique(Cables, data.Cables, c => c.Code, c => c.Row, "code", errors);
        }

        if (Table(Connections) is { } connections)
        {
            Rows(connections, errors, (row, cell, fail) =>
            {
                var kind = cell("kind");
                if (!ConnectionKinds.Contains(kind))
                {
                    fail($"kind är en av {string.Join(", ", ConnectionKinds)}");
                }
                if (Terminal(cell, "a", fail) is { } a && Terminal(cell, "b", fail) is { } b)
                {
                    if (a == b)
                    {
                        fail("a och b är två olika terminaler");
                    }
                    data.Connections.Add(new XConnection(row, a, b, kind, Lifecycle(cell, fail)));
                }
            });
        }

        if (Table(Circuits) is { } circuits)
        {
            Rows(circuits, errors, (row, cell, fail) =>
            {
                var (id, code, layer) = (cell("id"), cell("code"), cell("layer"));
                Required(fail, ("id", id), ("code", code));
                if (!Layers.Contains(layer))
                {
                    fail($"layer är en av {string.Join(", ", Layers)}");
                }
                data.Circuits.Add(new XCircuit(row, id, code, layer, Lifecycle(cell, fail)));
            });
            Unique(Circuits, data.Circuits, c => c.Id, c => c.Row, "id", errors);
            Unique(Circuits, data.Circuits, c => c.Code, c => c.Row, "code", errors);
        }

        if (Table(Hops) is { } hops)
        {
            Rows(hops, errors, (row, cell, fail) =>
            {
                var circuit = cell("circuit");
                Required(fail, ("circuit", circuit));
                var seq = Int(cell("seq"), "seq", fail);
                if (seq is null or < 0)
                {
                    fail("seq är ett heltal från 0");
                }
                string? channelKind = null;
                int? channelNumber = null;
                if (Optional(cell("channel")) is { } channel)
                {
                    var parts = channel.Split(':');
                    if (parts.Length != 2 || !ChannelKinds.Contains(parts[0])
                        || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
                    {
                        fail($"channel skrivs som typ:nummer, till exempel vlan:100 (typer: {string.Join(", ", ChannelKinds)})");
                    }
                    else
                    {
                        (channelKind, channelNumber) = (parts[0], number);
                    }
                }
                if (Terminal(cell, "", fail) is { } terminal && seq is >= 0)
                {
                    data.Hops.Add(new XHop(row, circuit, seq.Value, terminal, channelKind, channelNumber));
                }
            }, objectColumn: "circuit");
            foreach (var duplicate in data.Hops.GroupBy(h => (h.Circuit, h.Seq)).Where(g => g.Count() > 1))
            {
                errors.Add(new ImportError(Hops, duplicate.Skip(1).First().Row, duplicate.Key.Circuit, $"seq {duplicate.Key.Seq} finns redan på rad {duplicate.First().Row}"));
            }
        }

        if (Table(Dependencies) is { } dependencies)
        {
            Rows(dependencies, errors, (row, cell, fail) =>
            {
                var (circuit, carrier) = (cell("circuit"), cell("carrier"));
                Required(fail, ("circuit", circuit), ("carrier", carrier));
                if (circuit.Length > 0 && circuit == carrier)
                {
                    fail("en krets bär inte sig själv");
                }
                data.Dependencies.Add(new XDependency(row, circuit, carrier));
            }, objectColumn: "circuit");
        }

        if (Table(Services) is { } services)
        {
            Rows(services, errors, (row, cell, fail) =>
            {
                var (id, code, name, type) = (cell("id"), cell("code"), cell("name"), cell("serviceType"));
                Required(fail, ("id", id), ("code", code), ("name", name), ("serviceType", type));
                var attributes = Attributes(catalog, "service", type, cell, fail);
                data.Services.Add(new XService(row, id, code, name, type, Lifecycle(cell, fail), attributes));
            });
            Unique(Services, data.Services, s => s.Id, s => s.Row, "id", errors);
            Unique(Services, data.Services, s => s.Code, s => s.Row, "code", errors);
        }

        if (Table(ServiceCircuits) is { } serviceCircuits)
        {
            Rows(serviceCircuits, errors, (row, cell, fail) =>
            {
                var (service, circuit) = (cell("service"), cell("circuit"));
                Required(fail, ("service", service), ("circuit", circuit));
                data.ServiceCircuits.Add(new XServiceCircuit(row, service, circuit));
            }, objectColumn: "service");
        }
        return data;
    }

    /// <summary>
    /// Reads each row. A row with problems may still be added; the import stops before writing when there is any
    /// problem, and keeping the row avoids follow-on errors such as "unknown site" for a site whose row had a typo.
    /// </summary>
    private static void Rows(CsvTable table, List<ImportError> errors, Action<int, Func<string, string>, Action<string>> read, string objectColumn = "id")
    {
        foreach (var (row, cells) in table.Rows)
        {
            var name = table.Cell(cells, objectColumn) is { Length: > 0 } o ? o : table.Cell(cells, "code");
            read(row, column => table.Cell(cells, column), message => errors.Add(new ImportError(table.File, row, name, message)));
        }
    }

    private static void Required(Action<string> fail, params (string Column, string Value)[] fields)
    {
        foreach (var (column, value) in fields.Where(f => f.Value.Length == 0))
        {
            fail($"{column} saknas");
        }
    }

    private static string? Optional(string value) => value.Length == 0 ? null : value;

    private static string Lifecycle(Func<string, string> cell, Action<string> fail)
    {
        var lifecycle = cell("lifecycle");
        if (lifecycle.Length == 0)
        {
            return "in_service";
        }
        if (!Lifecycles.Contains(lifecycle))
        {
            fail($"lifecycle är en av {string.Join(", ", Lifecycles)}");
        }
        return lifecycle;
    }

    private static (double X, double Y)? Position(Func<string, string> cell, Action<string> fail)
    {
        (double, double)? position;
        if (Number(cell("x")) is { } x && Number(cell("y")) is { } y)
        {
            position = (x, y);
        }
        else if (Number(cell("lat")) is { } lat && Number(cell("lon")) is { } lon)
        {
            position = SwerefTm.FromLatLon(lat, lon);
        }
        else
        {
            fail("position anges som x och y (SWEREF 99 TM) eller lat och lon (WGS 84)");
            return null;
        }
        if (position is var (px, py) && (px is < MinX or > MaxX || py is < MinY or > MaxY))
        {
            fail("positionen ligger utanför kartan");
            return null;
        }
        return position;
    }

    private static double[]? Route(string wkt, Action<string> fail)
    {
        if (wkt.Length == 0)
        {
            return null;
        }
        const string prefix = "LINESTRING";
        var text = wkt.Trim();
        var open = text.IndexOf('(');
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || open < 0 || !text.EndsWith(')'))
        {
            fail("route är en WKT LINESTRING i SWEREF 99 TM");
            return null;
        }
        var coordinates = new List<double>();
        foreach (var point in text[(open + 1)..^1].Split(','))
        {
            var parts = point.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || Number(parts[0]) is not { } x || Number(parts[1]) is not { } y || x is < MinX or > MaxX || y is < MinY or > MaxY)
            {
                fail($"route: '{point.Trim()}' är ingen punkt i kartan");
                return null;
            }
            coordinates.Add(x);
            coordinates.Add(y);
        }
        if (coordinates.Count < 4)
        {
            fail("route har minst två punkter");
            return null;
        }
        return [.. coordinates];
    }

    /// <summary>A terminal from <c>{prefix}Equipment</c> and <c>{prefix}Port</c>, or <c>{prefix}Cable</c>, <c>{prefix}Conductor</c> and <c>{prefix}Side</c>.</summary>
    private static XTerminal? Terminal(Func<string, string> cell, string prefix, Action<string> fail)
    {
        string Col(string name) => prefix.Length == 0 ? char.ToLowerInvariant(name[0]) + name[1..] : prefix + name;
        var (equipment, port, cable) = (cell(Col("Equipment")), cell(Col("Port")), cell(Col("Cable")));
        if (equipment.Length > 0 && port.Length > 0 && cable.Length == 0)
        {
            return new XTerminal(equipment, port, null, 0, ' ');
        }
        var side = cell(Col("Side")).ToUpperInvariant();
        if (cable.Length > 0 && equipment.Length == 0 && Int(cell(Col("Conductor")), Col("Conductor"), _ => { }) is { } n && n > 0 && side is "A" or "B")
        {
            return new XTerminal(null, null, cable, n, side[0]);
        }
        fail($"{(prefix.Length == 0 ? "terminalen" : prefix)} anges med {Col("Equipment")} och {Col("Port")}, eller {Col("Cable")}, {Col("Conductor")} (från 1) och {Col("Side")} (A eller B)");
        return null;
    }

    private static string Attributes(TypeCatalog catalog, string objectType, string typeKey, Func<string, string> cell, Action<string> fail)
    {
        var attributes = new JsonObject();
        if (Optional(cell("attributes")) is { } json)
        {
            if (AttributeCells.Parse(json) is JsonObject parsed)
            {
                attributes = parsed;
            }
            else
            {
                fail("attributes är ett JSON-objekt");
                return "{}";
            }
        }
        var problems = new List<string>();
        if (typeKey.Length > 0 && AttributeCells.Read(catalog.AttributeSchema(objectType, typeKey), cell, problems) is { } fromColumns)
        {
            foreach (var (key, value) in fromColumns)
            {
                attributes[key] = value?.DeepClone();
            }
        }
        var text = attributes.ToJsonString();
        if (problems.Count == 0 && typeKey.Length > 0 && (objectType != "equipment" || catalog.Find(typeKey) is not null))
        {
            using var doc = JsonDocument.Parse(text);
            problems.AddRange(catalog.ValidateAttributes(objectType, typeKey, doc.RootElement));
        }
        foreach (var problem in problems)
        {
            fail($"attributen passar inte typens schema: {problem}");
        }
        return text;
    }

    private static double? Number(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    private static int? Int(string text, string column, Action<string> fail)
    {
        if (text.Length == 0)
        {
            return null;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        fail($"{column} är ett heltal");
        return null;
    }

    private static short? Short(string text, string column, Action<string> fail)
    {
        if (Int(text, column, fail) is not { } value)
        {
            return null;
        }
        if (value is < 1 or > short.MaxValue)
        {
            fail($"{column} är mellan 1 och {short.MaxValue}");
            return null;
        }
        return (short)value;
    }

    private static void Unique<T>(string file, List<T> rows, Func<T, string> key, Func<T, int> row, string column, List<ImportError> errors)
    {
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in rows)
        {
            var value = key(item);
            if (value.Length == 0)
            {
                continue;
            }
            if (!first.TryAdd(value, row(item)))
            {
                errors.Add(new ImportError(file, row(item), value, $"{column} finns redan på rad {first[value]}"));
            }
        }
    }
}
